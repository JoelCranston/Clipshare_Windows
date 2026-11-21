using System.Net;
using System.Text;
using Omaclip;

namespace OmaclipCore.Tests;

/// <summary>
/// Vectors were produced with Go using the same code paths as omaclip v0.10
/// (golang.org/x/crypto argon2/hkdf, encoding/json); they are shared with the macOS client's tests.
/// </summary>
public class ProtocolTests
{
    private static SyncKey KeyOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public void Argon2KeyMatchesOmaclip()
    {
        var key = SyncKey.Derive("correct horse battery");
        Assert.Equal("4dbd6c61bd63d4c3a007e1d5423dbc6bf1bbe77463b6623b9829f516cf21cb1d", key.Hex);
        Assert.Equal("4dbd6c61bd63d4c3", key.ShortHash);
    }

    [Fact]
    public void CAKeyMatchesOmaclip()
    {
        var key = new SyncKey(Convert.FromHexString("4dbd6c61bd63d4c3a007e1d5423dbc6bf1bbe77463b6623b9829f516cf21cb1d"));
        using var credentials = new PeerCredentials(key);
        Assert.Equal("5deddde25ead041d2a1c7e90e6cd3c6b3c5d73b7b82d98e0121c7eb503b7c63b",
            Convert.ToHexStringLower(credentials.CaPrivateScalar));
        Assert.Equal(
            "04d042cbae38fa8476fb1e155d9086da9a3d47cb50f6faab55a03c0de0d26157a459ef49715bb49704f9b5768fe5155dcc97db3c41d62abaf5421785c531cb6861",
            Convert.ToHexStringLower(credentials.CaPublicPoint));
    }

    [Fact]
    public void ScalarReductionWraps()
    {
        var input = Convert.FromHexString("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632555");
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000006",
            Convert.ToHexStringLower(PeerCredentials.ReduceToScalar(input)));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData(" leading space", false)]
    [InlineData("trailing tab\t", false)]
    [InlineData("correct horse battery", true)]
    [InlineData("12345678", true)]
    public void PassphraseValidation(string passphrase, bool valid) => Assert.Equal(valid, SyncKey.IsValid(passphrase));

    [Fact]
    public void PassphraseLengthCountsUtf8Bytes()
    {
        Assert.True(SyncKey.IsValid("ééééé")); // 5 characters, 10 bytes
        Assert.False(SyncKey.IsValid(new string('é', 65))); // 130 bytes
    }

    [Fact]
    public void ManifestJsonMatchesGo()
    {
        var entries = new List<ManifestEntry>
        {
            new("1793400907123456789", "aa", "text", null, "2026-10-30T22:55:07.123456789Z"),
            new("1793400901000000000", "bb", "image", "image/png", "2026-10-30T22:55:01Z"),
            new("3", "cc", "text", null, "2026-10-30T15:55:01.5-07:00"),
        };
        Assert.Equal(
            """[{"id":"1793400907123456789","checksum":"aa","contentType":"text","timestamp":"2026-10-30T22:55:07.123456789Z"},{"id":"1793400901000000000","checksum":"bb","contentType":"image","imageMimeType":"image/png","timestamp":"2026-10-30T22:55:01Z"},{"id":"3","checksum":"cc","contentType":"text","timestamp":"2026-10-30T15:55:01.5-07:00"}]""",
            GoJson.Entries(entries));
        Assert.Equal("7a738558917f921e27457303646b83b80c8a6075ab87f34a47a4c81e818c167a", GoJson.ManifestChecksum(entries));
        Assert.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", GoJson.ManifestChecksum([]));
    }

    [Fact]
    public void GoStringEscaping()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\u003c\\u003e\\u0026\\u0001\\u2028é😀\"", GoJson.String("a\"b\\c\n<>&\u0001\u2028é😀"));
    }

    [Fact]
    public void TimestampFormattingAndParsing()
    {
        Assert.Equal("2026-10-30T22:55:07.123456789Z", WireClock.Timestamp(1_793_400_907_123_456_789));
        Assert.Equal("2026-10-30T22:55:01Z", WireClock.Timestamp(1_793_400_901_000_000_000));
        Assert.Equal("2026-10-30T22:55:01.5Z", WireClock.Timestamp(1_793_400_901_500_000_000));
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T22:55:01.5Z"), WireClock.Parse("2026-10-30T15:55:01.5-07:00"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T22:55:07.1234567Z"), WireClock.Parse("2026-10-30T22:55:07.123456789Z"));
        Assert.NotNull(WireClock.Parse("2026-10-30T22:55:01Z"));
        Assert.Null(WireClock.Parse("yesterday"));
        Assert.True(WireClock.NowNanos() < WireClock.NowNanos());
    }

    [Fact]
    public void LeafCertificateVerifiesOnlyWithSameCA()
    {
        using var credentials = new PeerCredentials(KeyOf(7));
        using var other = new PeerCredentials(KeyOf(8));
        using var leaf = credentials.MakeLeafCertificate([IPAddress.Loopback, IPAddress.Parse("192.168.1.20")]);
        Assert.True(leaf.HasPrivateKey);
        Assert.True(credentials.VerifyPeerCertificate(leaf.RawData, "192.168.1.20"));
        Assert.True(credentials.VerifyPeerCertificate(leaf.RawData, null));
        Assert.False(credentials.VerifyPeerCertificate(leaf.RawData, "10.0.0.1"));
        Assert.False(other.VerifyPeerCertificate(leaf.RawData, "192.168.1.20"));
        Assert.False(credentials.VerifyPeerCertificate(leaf.RawData, "192.168.1.20", DateTimeOffset.UtcNow.AddYears(2)));
        Assert.False(credentials.VerifyPeerCertificate([1, 2, 3], null));
    }

    [Fact]
    public void LeafIssuerIsEncodedLikeGo()
    {
        using var credentials = new PeerCredentials(KeyOf(7));
        using var leaf = credentials.MakeLeafCertificate([IPAddress.Loopback]);
        // SEQUENCE { SET { SEQUENCE { OID commonName, PrintableString "omaclip-ca" } } }
        Assert.Equal("3015311330110603550403130a6f6d61636c69702d6361", Convert.ToHexStringLower(leaf.IssuerName.RawData));
    }

    [Fact]
    public void PeerListParsing()
    {
        var peers = Peer.ParseList("omarchy@100.101.102.103:36742; 192.168.1.5:4000\nlaptop@omarchy.local:36742, box.tailnet.ts.net:1;bad@:1;x@host:99999;y@has space:5");
        Assert.Equal(
        [
            new Peer("omarchy", "100.101.102.103", 36742),
            new Peer("192.168.1.5:4000", "192.168.1.5", 4000),
            new Peer("laptop", "omarchy.local", 36742),
            new Peer("box.tailnet.ts.net:1", "box.tailnet.ts.net", 1),
        ], peers);
        Assert.Equal("omarchy", new Peer("omarchy-41234", "1.2.3.4", 41234).DisplayName);
        Assert.Equal("1.2.3.4:5", new Peer("1.2.3.4:5", "1.2.3.4", 5).DisplayName);
    }

    [Fact]
    public async Task HostResolution()
    {
        Assert.Equal("10.1.2.3", await IPv4.ResolveAsync("10.1.2.3"));
        Assert.Equal("127.0.0.1", await IPv4.ResolveAsync("localhost"));
        Assert.Contains(IPAddress.Loopback, IPv4.LocalAddresses());
    }

    [Fact]
    public void AdvertisableAddressesSkipDockerAndLinkLocal()
    {
        Assert.True(IPv4.IsAdvertisable(IPAddress.Parse("192.168.1.20")));
        Assert.True(IPv4.IsAdvertisable(IPAddress.Parse("100.101.102.103")));
        Assert.False(IPv4.IsAdvertisable(IPAddress.Parse("172.17.0.1")));
        Assert.False(IPv4.IsAdvertisable(IPAddress.Parse("169.254.10.1")));
        Assert.False(IPv4.IsAdvertisable(IPAddress.Loopback));
    }

    private static (SyncServer Server, PeerCredentials Credentials) StartServer(SharedHistory history, byte keyFill = 3)
    {
        var credentials = new PeerCredentials(KeyOf(keyFill));
        var leaf = credentials.MakeLeafCertificate([IPAddress.Loopback]);
        var server = new SyncServer(history, credentials.Key, leaf);
        server.Start(0);
        return (server, credentials);
    }

    private static SharedItem TextItem(string id, string text, string timestamp = "2026-10-30T22:55:01Z")
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return new SharedItem(new ManifestEntry(id, Hashing.Sha256Hex(payload), "text", null, timestamp), payload);
    }

    /// <summary>Runs the real HTTPS server and pulls from it with the real client, as an omaclip peer would.</summary>
    [Fact]
    public async Task ServerAndFetcherRoundTrip()
    {
        var history = new SharedHistory();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        history.Replace(
        [
            TextItem("2", "hello from windows ✓", "2026-10-30T22:55:02.25Z"),
            new SharedItem(new ManifestEntry("1", Hashing.Sha256Hex(png), "image", "image/png", "2026-10-30T22:55:01Z"), png),
        ]);
        var (server, credentials) = StartServer(history);
        using var _ = server;
        using var __ = credentials;

        using var fetcher = new RemoteFetcher(credentials);
        // A host name exercises resolution; the fetcher connects by IP so the IP SAN is checked.
        var peer = new Peer("self", "localhost", server.Port);
        var entries = await fetcher.FetchAsync(peer);
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("hello from windows ✓", entries[0].Text);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T22:55:02.25Z"), entries[0].Date);
        Assert.Equal(png, entries[1].Payload);
        Assert.Equal("image/png", entries[1].ImageMimeType);
        Assert.Null(await fetcher.FetchAsync(peer));

        history.Replace([TextItem("3", "newer"), TextItem("2", "hello from windows ✓", "2026-10-30T22:55:02.25Z")]);
        var updated = await fetcher.FetchAsync(peer);
        Assert.Equal(["newer", "hello from windows ✓"], updated!.Select(e => e.Text));

        using var wrong = new PeerCredentials(KeyOf(4));
        using var wrongFetcher = new RemoteFetcher(wrong);
        await Assert.ThrowsAsync<PeerException>(() => wrongFetcher.FetchAsync(peer));
    }

    [Fact]
    public async Task ServerRejectsMissingPassphraseAndUnknownRoutes()
    {
        var history = new SharedHistory();
        history.Replace([TextItem("1", "secret")]);
        var (server, credentials) = StartServer(history);
        using var _ = server;
        using var __ = credentials;

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler);
        var url = $"https://127.0.0.1:{server.Port}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url + "/api/clipboard")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Omaclip-Pass", credentials.Key.Hex);
        var checksum = await client.GetStringAsync(url + "/api/clipboard/checksum");
        Assert.Equal("{\"checksum\":\"" + history.CurrentChecksum + "\"}\n", checksum);
        Assert.Equal("secret", await client.GetStringAsync(url + "/api/clipboard/1/content"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + "/api/clipboard/9/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(url + "/api/clipboard", new StringContent("x"))).StatusCode);
    }
}
