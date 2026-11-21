using System.Net;
using Omaclip;
using Omaclip.Mdns;

namespace OmaclipCore.Tests;

public class MdnsTests
{
    private const string Instance = "omarchy-41234._omaclip._tcp.local";

    [Fact]
    public void MessageRoundTrips()
    {
        var message = new DnsMessage { Id = 7, IsResponse = true };
        message.Questions.Add(new DnsQuestion(MdnsDiscovery.ServiceType, DnsType.Ptr, UnicastResponse: true));
        message.Answers.Add(DnsRecord.Ptr(MdnsDiscovery.ServiceType, Instance, 120));
        message.Additionals.Add(DnsRecord.Srv(Instance, "omarchy-41234.local", 41234, 120, flush: true));
        message.Additionals.Add(DnsRecord.Txt(Instance, ["version=2", "ph=4dbd6c61bd63d4c3"], 120, flush: true));
        message.Additionals.Add(DnsRecord.A("omarchy-41234.local", IPAddress.Parse("192.168.1.30"), 120, flush: true));

        var parsed = DnsMessage.TryParse(message.Encode());
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.Id);
        Assert.True(parsed.IsResponse);
        Assert.True(parsed.Questions.Single().UnicastResponse);
        Assert.Equal(Instance, parsed.Answers.Single().Target);
        var srv = parsed.Additionals.Single(r => r.Type == DnsType.Srv);
        Assert.Equal(41234, srv.Port);
        Assert.Equal("omarchy-41234.local", srv.Target);
        Assert.True(srv.CacheFlush);
        Assert.Equal(["version=2", "ph=4dbd6c61bd63d4c3"], parsed.Additionals.Single(r => r.Type == DnsType.Txt).Text);
        Assert.Equal(IPAddress.Parse("192.168.1.30"), parsed.Additionals.Single(r => r.Type == DnsType.A).Address);
    }

    [Fact]
    public void ParsesCompressedNames()
    {
        // Header (1 answer), then a PTR whose owner is "_omaclip._tcp.local" and whose target
        // is "omarchy-1" + pointer to the owner name at offset 12.
        var packet = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
        void Label(string text) { packet.Add((byte)text.Length); packet.AddRange(System.Text.Encoding.ASCII.GetBytes(text)); }
        Label("_omaclip"); Label("_tcp"); Label("local"); packet.Add(0);
        packet.AddRange([0, 12, 0, 1, 0, 0, 0, 120]);
        var rdata = new List<byte> { 9 };
        rdata.AddRange(System.Text.Encoding.ASCII.GetBytes("omarchy-1"));
        rdata.AddRange([0xC0, 12]);
        packet.AddRange([0, (byte)rdata.Count]);
        packet.AddRange(rdata);

        var parsed = DnsMessage.TryParse(packet.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal("_omaclip._tcp.local", parsed.Answers.Single().Name);
        Assert.Equal("omarchy-1._omaclip._tcp.local", parsed.Answers.Single().Target);
    }

    [Fact]
    public void RejectsMalformedPackets()
    {
        Assert.Null(DnsMessage.TryParse([1, 2, 3]));
        // Claims one question but ends inside its name; and a pointer loop.
        Assert.Null(DnsMessage.TryParse([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 5, (byte)'a']));
        Assert.Null(DnsMessage.TryParse([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1]));
    }

    /// <summary>
    /// Two discovery instances on this machine find each other over real multicast sockets, and an
    /// instance with a different passphrase is ignored. Skipped when no LAN interface is up.
    /// </summary>
    [Fact]
    public async Task InstancesOnOneMachineDiscoverEachOther()
    {
        if (!IPv4.InterfaceAddresses(includeAll: false).Any()) return;
        using var a = new MdnsDiscovery("aaaaaaaaaaaaaaaa", "test-a");
        using var b = new MdnsDiscovery("aaaaaaaaaaaaaaaa", "test-b");
        using var stranger = new MdnsDiscovery("bbbbbbbbbbbbbbbb", "test-c");
        var found = new TaskCompletionSource<IReadOnlyList<Peer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<Peer> latest = [];
        a.PeersChanged += peers =>
        {
            latest = peers;
            if (peers.Any(p => p.Name == "test-b-2222")) found.TrySetResult(peers);
        };
        a.Start(1111);
        b.Start(2222);
        stranger.Start(3333);
        var completed = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(completed == found.Task, "test-a did not discover test-b; saw: " + string.Join(", ", latest));
        var peer = found.Task.Result.Single(p => p.Name == "test-b-2222");
        Assert.Equal(2222, peer.Port);
        Assert.Equal("test-b", peer.DisplayName);
        Assert.DoesNotContain(found.Task.Result, p => p.Name.StartsWith("test-c", StringComparison.Ordinal));
        Assert.DoesNotContain(found.Task.Result, p => p.Name.StartsWith("test-a", StringComparison.Ordinal));
    }
}
