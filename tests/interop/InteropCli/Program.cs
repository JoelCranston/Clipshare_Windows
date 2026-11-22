// interop-cli serve    <passphrase> <port>       serve two entries and advertise over mDNS until stdin closes
// interop-cli fetch    <passphrase> <host:port>
// interop-cli discover <passphrase> <seconds>
using System.Text;
using Omaclip;
using Omaclip.Mdns;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: interop-cli serve|fetch|discover <passphrase> <arg>");
    return 2;
}
using var credentials = new PeerCredentials(SyncKey.Derive(args[1]));

switch (args[0])
{
    case "serve":
    {
        var history = new SharedHistory();
        var nanos = WireClock.NowNanos();
        var text = Encoding.UTF8.GetBytes("hello from windows <&> ✓");
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a, 9, 8, 7];
        history.Replace(
        [
            new SharedItem(new ManifestEntry(nanos.ToString(), Hashing.Sha256Hex(text), "text", null, WireClock.Timestamp(nanos)), text),
            new SharedItem(new ManifestEntry((nanos - 1_000_000_000).ToString(), Hashing.Sha256Hex(png), "image", "image/png",
                WireClock.Timestamp(nanos - 1_000_000_000)), png),
        ]);
        using var server = new SyncServer(history, credentials.Key, credentials.MakeLeafCertificate(IPv4.LocalAddresses()));
        server.Start(int.Parse(args[2]));
        using var discovery = new MdnsDiscovery(credentials.Key.ShortHash, "windows-interop");
        discovery.PeersChanged += peers => Console.WriteLine("peers " + string.Join(" ", peers.Select(p => $"{p.Name}@{p.Host}:{p.Port}")));
        discovery.Start(server.Port);
        Console.WriteLine($"listening {server.Port}");
        Console.In.ReadToEnd();
        return 0;
    }
    case "fetch":
    {
        var peer = Peer.Parse("omaclip@" + args[2]) ?? throw new ArgumentException("bad peer");
        using var fetcher = new RemoteFetcher(credentials);
        IReadOnlyList<RemoteEntry>? entries;
        try { entries = await fetcher.FetchAsync(peer); }
        catch (PeerException ex)
        {
            Console.WriteLine("error: " + ex.Message);
            return 1;
        }
        foreach (var entry in entries ?? [])
        {
            Console.WriteLine(entry.Kind == ContentKind.Text
                ? $"text \"{entry.Text}\" {entry.Timestamp} {entry.Date:O}"
                : $"image {entry.ImageMimeType} {entry.Payload.Length}-bytes {entry.Timestamp}");
        }
        Console.WriteLine(await fetcher.FetchAsync(peer) is null ? "second fetch: unchanged" : "second fetch: CHANGED?");
        return 0;
    }
    case "discover":
    {
        using var discovery = new MdnsDiscovery(credentials.Key.ShortHash, "windows-discover");
        IReadOnlyList<Peer> latest = [];
        discovery.PeersChanged += peers => latest = peers;
        discovery.Start(1);
        await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[2])));
        foreach (var peer in latest) Console.WriteLine($"peer {peer.Name} {peer.Host}:{peer.Port}");
        return 0;
    }
    default:
        Console.Error.WriteLine("unknown mode");
        return 2;
}
