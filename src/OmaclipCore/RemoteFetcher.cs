using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Omaclip;

public sealed class PeerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A clipboard item downloaded from a peer.</summary>
public sealed record RemoteEntry(
    string Id,
    string Checksum,
    ContentKind Kind,
    string? ImageMimeType,
    string Timestamp,
    DateTimeOffset Date,
    byte[] Payload)
{
    public string? Text => Kind == ContentKind.Text ? Encoding.UTF8.GetString(Payload) : null;
}

/// <summary>
/// Pulls peers' histories with omaclip's v2 flow: checksum, then manifest (whose own checksum is
/// verified), then only payloads not already held, each verified against its SHA-256. A peer's history
/// is replaced only once every payload has arrived; payloads that did arrive are kept for the retry.
/// </summary>
public sealed class RemoteFetcher : IDisposable
{
    private sealed class PeerState
    {
        public string CommittedChecksum = "";
        public List<RemoteEntry> Entries = [];
        /// <summary>Payloads by "kind:checksum", including ones fetched for a manifest that is not committed yet.</summary>
        public Dictionary<string, byte[]> Payloads = new(StringComparer.Ordinal);
    }

    private readonly HttpClient client;
    private readonly string authHex;
    private readonly Lock gate = new();
    private readonly Dictionary<string, PeerState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Address, DateTimeOffset Expires)> resolved = new(StringComparer.OrdinalIgnoreCase);

    public RemoteFetcher(PeerCredentials credentials)
    {
        authHex = credentials.Key.Hex;
        var handler = new HttpClientHandler
        {
            // Trust only leaves signed by the passphrase-derived CA that name the IP we dialed.
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                certificate is not null && credentials.VerifyPeerCertificate(certificate.RawData, request.RequestUri?.Host),
            MaxConnectionsPerServer = 2,
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public void Dispose() => client.Dispose();

    public void Forget(string peerName)
    {
        lock (gate) states.Remove(peerName);
    }

    /// <summary>Resolves host names, caching each answer for 30 seconds.</summary>
    private async Task<string> AddressAsync(string host, CancellationToken cancellation)
    {
        if (IPv4.IsLiteral(host)) return host;
        lock (gate)
        {
            if (resolved.TryGetValue(host, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Address;
        }
        var address = await IPv4.ResolveAsync(host, cancellation).ConfigureAwait(false);
        lock (gate)
        {
            if (address is null)
            {
                resolved.Remove(host);
                throw new PeerException($"Can't resolve {host}");
            }
            resolved[host] = (address, DateTimeOffset.UtcNow.AddSeconds(30));
        }
        return address;
    }

    private async Task<byte[]> GetAsync(string address, int port, string path, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{address}:{port}{path}");
        request.Headers.Add("X-Omaclip-Pass", authHex);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PeerException(Describe(ex), ex);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new PeerException("Timed out", ex);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new PeerException("Passphrase mismatch (HTTP 401)");
            if (response.StatusCode != HttpStatusCode.OK) throw new PeerException($"HTTP {(int)response.StatusCode}");
            return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        }
    }

    private static string Describe(HttpRequestException ex)
    {
        if (ex.InnerException is System.Security.Authentication.AuthenticationException)
            return "Untrusted certificate (different passphrase?)";
        if (ex.HttpRequestError == HttpRequestError.ConnectionError) return "Can't connect";
        return ex.Message;
    }

    /// <summary>Returns the peer's newest-first history if it changed since the last call, or null if not.</summary>
    public async Task<IReadOnlyList<RemoteEntry>?> FetchAsync(Peer peer, CancellationToken cancellation = default)
    {
        // Connect by IP so the certificate's IP address SANs can be checked, as omaclip does.
        var address = await AddressAsync(peer.Host, cancellation).ConfigureAwait(false);
        PeerState state;
        lock (gate)
        {
            if (!states.TryGetValue(peer.Name, out state!)) states[peer.Name] = state = new PeerState();
        }

        var checksumBody = await GetAsync(address, peer.Port, "/api/clipboard/checksum", cancellation).ConfigureAwait(false);
        var checksum = Decode<ChecksumResponse>(checksumBody).Checksum;
        if (string.IsNullOrEmpty(checksum)) throw new PeerException("Peer sent no checksum");
        if (checksum == state.CommittedChecksum) return null;

        var manifest = Decode<Manifest>(await GetAsync(address, peer.Port, "/api/clipboard", cancellation).ConfigureAwait(false));
        var entries = manifest.Entries ?? [];
        if (manifest.Checksum != GoJson.ManifestChecksum(entries)) throw new PeerException("Invalid manifest checksum");
        // The manifest can be newer than the checksum response.
        if (manifest.Checksum == state.CommittedChecksum) return null;

        var fresh = new List<RemoteEntry>(entries.Count);
        PeerException? failure = null;
        foreach (var entry in entries)
        {
            var kind = ContentKinds.Parse(entry.ContentType) ?? throw new PeerException($"Unsupported content type {entry.ContentType}");
            var cacheKey = entry.ContentType + ":" + entry.Checksum;
            if (!state.Payloads.TryGetValue(cacheKey, out var payload))
            {
                try
                {
                    var data = await GetAsync(address, peer.Port, $"/api/clipboard/{Uri.EscapeDataString(entry.Id)}/content", cancellation)
                        .ConfigureAwait(false);
                    if (Hashing.Sha256Hex(data) != entry.Checksum) throw new PeerException($"Checksum mismatch for entry {entry.Id}");
                    state.Payloads[cacheKey] = payload = data;
                }
                catch (PeerException ex)
                {
                    failure ??= ex;
                    continue;
                }
            }
            fresh.Add(new RemoteEntry(entry.Id, entry.Checksum, kind, entry.ImageMimeType, entry.Timestamp,
                WireClock.Parse(entry.Timestamp) ?? DateTimeOffset.UtcNow, payload));
        }

        // Keep only payloads the committed or the incoming history still needs.
        var needed = new HashSet<string>(state.Entries.Select(e => e.Kind.Wire() + ":" + e.Checksum)
            .Concat(entries.Select(e => e.ContentType + ":" + e.Checksum)), StringComparer.Ordinal);
        foreach (var key in state.Payloads.Keys.Where(k => !needed.Contains(k)).ToList()) state.Payloads.Remove(key);

        if (failure is not null) throw failure;

        state.CommittedChecksum = manifest.Checksum;
        state.Entries = fresh;
        return fresh;
    }

    private static T Decode<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body) ?? throw new PeerException("Empty response");
        }
        catch (JsonException ex)
        {
            throw new PeerException("Malformed response", ex);
        }
    }
}
