using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Omaclip.Mdns;

/// <summary>
/// Advertises this computer as <c>_omaclip._tcp</c> and finds omaclip peers that share the passphrase,
/// speaking multicast DNS directly so it behaves the same on every Windows version and matches the
/// records omaclip's own responder (hashicorp/mdns) sends and expects.
///
/// <see cref="PeersChanged"/> is raised on a background thread.
/// </summary>
public sealed class MdnsDiscovery : IDisposable
{
    public const string ServiceType = "_omaclip._tcp.local";
    public const string ProtocolVersion = "2";
    public const int MdnsPort = 5353;
    private const uint Ttl = 120;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndPoint = new(Group, MdnsPort);
    /// <summary>
    /// How often we query for peers and re-announce ourselves. omaclip drops a peer it hasn't heard from
    /// in three of its 2-second browse cycles, so announcing this often keeps us listed even when a
    /// firewall stops omaclip's queries from reaching us.
    /// </summary>
    private static readonly TimeSpan BrowseInterval = TimeSpan.FromSeconds(4);
    /// <summary>A peer is dropped after this long without being seen, like omaclip's three missed browse cycles.</summary>
    private static readonly TimeSpan PeerLifetime = TimeSpan.FromSeconds(20);

    private sealed record LocalAddress(int InterfaceIndex, IPAddress Address, IPAddress Mask);

    private sealed class Instance
    {
        public DateTimeOffset LastSeen;
        public string? Target;
        public int Port;
        public IReadOnlyList<string>? Text;
        public DateTimeOffset LastQueried;
    }

    public event Action<IReadOnlyList<Peer>>? PeersChanged;

    private readonly string shortHash;
    private readonly Lock gate = new();
    private readonly Lock sendGate = new();
    private readonly Dictionary<string, Instance> instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int, IPAddress)> joined = [];
    private List<LocalAddress> locals = [];
    private IReadOnlyList<Peer> lastReported = [];
    private Socket? multicastSocket;
    private Socket? querySocket;
    private CancellationTokenSource? running;
    private int port;

    /// <summary>The DNS-SD instance label, <c>&lt;hostname&gt;-&lt;port&gt;</c> as omaclip names itself.</summary>
    public string InstanceLabel { get; private set; } = "";
    private string InstanceName => InstanceLabel + "." + ServiceType;
    /// <summary>The SRV target. Like omaclip, a per-instance name so it never collides with the OS's own host record.</summary>
    private string HostName => InstanceLabel + ".local";

    private readonly string hostLabel;

    public MdnsDiscovery(string shortHash, string? hostLabel = null)
    {
        this.shortHash = shortHash;
        this.hostLabel = Sanitize(hostLabel ?? Dns.GetHostName().Split('.')[0]);
    }

    private static string Sanitize(string label)
    {
        var cleaned = new string(label.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return cleaned.Length == 0 ? "windows" : cleaned[..Math.Min(cleaned.Length, 50)];
    }

    public void Start(int servicePort)
    {
        port = servicePort;
        InstanceLabel = $"{hostLabel}-{servicePort}";

        multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        multicastSocket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

        // Queries go out from an ephemeral port: omaclip's responder answers by unicast to the
        // sender, and on Windows a unicast reply to port 5353 may land in the OS's own mDNS socket.
        querySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        querySocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        querySocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        querySocket.Bind(new IPEndPoint(IPAddress.Any, 0));

        RefreshInterfaces();
        running = new CancellationTokenSource();
        var token = running.Token;
        _ = ReceiveLoopAsync(multicastSocket, isMulticast: true, token);
        _ = ReceiveLoopAsync(querySocket, isMulticast: false, token);
        _ = BrowseLoopAsync(token);
    }

    public void Stop()
    {
        if (running is null) return;
        try { Announce(ttl: 0); } catch (SocketException) { } catch (ObjectDisposedException) { }
        running.Cancel();
        running.Dispose();
        running = null;
        multicastSocket?.Dispose();
        querySocket?.Dispose();
        multicastSocket = querySocket = null;
        lock (gate)
        {
            instances.Clear();
            hostAddresses.Clear();
            joined.Clear();
            lastReported = [];
        }
    }

    public void Dispose() => Stop();

    // MARK: Interfaces

    private void RefreshInterfaces()
    {
        var current = new List<LocalAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    !nic.SupportsMulticast) continue;
                var properties = nic.GetIPProperties();
                var index = properties.GetIPv4Properties()?.Index ?? -1;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IPv4.IsAdvertisable(unicast.Address)) continue;
                    current.Add(new LocalAddress(index, unicast.Address, unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
                }
            }
        }
        catch (NetworkInformationException) { }

        lock (gate)
        {
            locals = current;
            foreach (var local in current)
            {
                if (!joined.Add((local.InterfaceIndex, local.Address))) continue;
                try
                {
                    multicastSocket?.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(Group, local.Address));
                }
                catch (SocketException)
                {
                    // Already a member through another address on the same interface, or the
                    // interface can't do multicast; either way nothing more to do.
                }
            }
        }
    }

    // MARK: Sending

    private void SendMulticast(Socket? socket, DnsMessage message, IPAddress? viaInterface)
    {
        if (socket is null) return;
        var packet = message.Encode();
        List<LocalAddress> targets;
        lock (gate) targets = viaInterface is null ? locals : locals.Where(l => l.Address.Equals(viaInterface)).ToList();
        lock (sendGate)
        {
            if (targets.Count == 0)
            {
                TrySend(socket, packet, GroupEndPoint);
                return;
            }
            foreach (var local in targets.DistinctBy(l => l.InterfaceIndex))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                }
                catch (SocketException) { continue; }
                TrySend(socket, packet, GroupEndPoint);
            }
        }
    }

    private void TrySend(Socket socket, byte[] packet, EndPoint target)
    {
        try
        {
            socket.SendTo(packet, target);
            Interlocked.Increment(ref packetsSent);
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref sendErrors);
            lastSendError = ex.Message;
        }
        catch (ObjectDisposedException) { }
    }

    // MARK: Diagnostics

    private long packetsSent, sendErrors, queriesReceived, queriesAnswered, responsesReceived;
    private volatile string? lastSendError;
    private volatile string? lastQueryFrom;

    /// <summary>A one-line summary of mDNS traffic, for the Settings status section.</summary>
    public string Diagnostics
    {
        get
        {
            string interfaces;
            lock (gate) interfaces = locals.Count == 0 ? "none" : string.Join(", ", locals.Select(l => l.Address));
            var text = $"advertising {InstanceLabel} on {interfaces}; sent {Interlocked.Read(ref packetsSent)}, " +
                       $"answered {Interlocked.Read(ref queriesAnswered)} of {Interlocked.Read(ref queriesReceived)} lookups" +
                       (lastQueryFrom is { } from ? $" (last from {from})" : "") +
                       $", heard {Interlocked.Read(ref responsesReceived)} replies";
            if (Interlocked.Read(ref sendErrors) is > 0 and var errors) text += $"; {errors} send errors, last: {lastSendError}";
            return text;
        }
    }

    private List<DnsRecord> ServiceRecords(IEnumerable<IPAddress> addresses, uint ttl, bool flush)
    {
        var text = new[] { "version=" + ProtocolVersion, "ph=" + shortHash };
        var records = new List<DnsRecord>
        {
            DnsRecord.Srv(InstanceName, HostName, (ushort)port, ttl, flush),
            DnsRecord.Txt(InstanceName, text, ttl, flush),
        };
        records.AddRange(addresses.Distinct().Select(a => DnsRecord.A(HostName, a, ttl, flush)));
        return records;
    }

    /// <summary>Unsolicited announcement (or goodbye, with a zero TTL) on every interface.</summary>
    private void Announce(uint ttl)
    {
        List<LocalAddress> snapshot;
        lock (gate) snapshot = locals;
        foreach (var group in snapshot.GroupBy(l => l.InterfaceIndex))
        {
            var message = new DnsMessage { IsResponse = true };
            message.Answers.Add(DnsRecord.Ptr(ServiceType, InstanceName, ttl));
            message.Answers.AddRange(ServiceRecords(group.Select(l => l.Address), ttl, flush: true));
            SendMulticast(multicastSocket, message, group.First().Address);
        }
    }

    // MARK: Browsing

    private async Task BrowseLoopAsync(CancellationToken cancellation)
    {
        var cycle = 0;
        try
        {
            Announce(Ttl);
            while (!cancellation.IsCancellationRequested)
            {
                if (cycle % 6 == 5) RefreshInterfaces();
                if (cycle > 0) Announce(Ttl);
                var query = new DnsMessage();
                query.Questions.Add(new DnsQuestion(ServiceType, DnsType.Ptr));
                SendMulticast(querySocket, query, null);
                Reconcile();
                cycle++;
                await Task.Delay(BrowseInterval, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReceiveLoopAsync(Socket socket, bool isMulticast, CancellationToken cancellation)
    {
        var buffer = new byte[9000];
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, from, cancellation).ConfigureAwait(false);
                var message = DnsMessage.TryParse(buffer.AsSpan(0, result.ReceivedBytes));
                if (message is null || result.RemoteEndPoint is not IPEndPoint sender) continue;
                if (message.IsResponse)
                {
                    Interlocked.Increment(ref responsesReceived);
                    HandleResponse(message);
                }
                else if (isMulticast) HandleQuery(message, sender, result.PacketInformation.Interface);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException)
            {
                // E.g. ICMP port unreachable reported on Windows; keep listening.
                if (cancellation.IsCancellationRequested) return;
            }
        }
    }

    // MARK: Responding

    private void HandleQuery(DnsMessage query, IPEndPoint sender, int interfaceIndex)
    {
        if (query.Questions.Count == 0) return;
        // Our own browse queries loop back to us; don't answer or count them.
        if (querySocket?.LocalEndPoint is IPEndPoint own && sender.Port == own.Port && IsLocal(sender.Address)) return;
        Interlocked.Increment(ref queriesReceived);
        List<IPAddress> addresses;
        lock (gate)
        {
            addresses = locals.Where(l => l.InterfaceIndex == interfaceIndex).Select(l => l.Address).ToList();
            if (addresses.Count == 0)
            {
                // Unknown interface: answer with the address that shares the sender's subnet, else all.
                addresses = locals.Where(l => SameSubnet(l, sender.Address)).Select(l => l.Address).ToList();
                if (addresses.Count == 0) addresses = locals.Select(l => l.Address).ToList();
            }
        }
        if (addresses.Count == 0) return;

        var legacy = sender.Port != MdnsPort;
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();
        var wantsUnicast = false;
        foreach (var question in query.Questions)
        {
            var any = question.Type == DnsType.Any;
            if (DnsMessage.NamesEqual(question.Name, ServiceType) && (any || question.Type == DnsType.Ptr))
            {
                answers.Add(DnsRecord.Ptr(ServiceType, InstanceName, Ttl));
                additionals.AddRange(ServiceRecords(addresses, Ttl, flush: !legacy));
            }
            else if (DnsMessage.NamesEqual(question.Name, InstanceName))
            {
                var records = ServiceRecords(addresses, Ttl, flush: !legacy);
                answers.AddRange(records.Where(r => r.Type is DnsType.Srv or DnsType.Txt &&
                                                    (any || r.Type == question.Type || question.Type == DnsType.Ptr)));
                additionals.AddRange(records.Where(r => r.Type == DnsType.A));
            }
            else if (DnsMessage.NamesEqual(question.Name, HostName) && (any || question.Type == DnsType.A))
            {
                answers.AddRange(addresses.Select(a => DnsRecord.A(HostName, a, Ttl, flush: !legacy)));
            }
            else
            {
                continue;
            }
            wantsUnicast |= question.UnicastResponse;
        }
        if (answers.Count == 0) return;
        Interlocked.Increment(ref queriesAnswered);
        lastQueryFrom = sender.Address.ToString();
        // An additional record that is also an answer is redundant.
        additionals.RemoveAll(a => answers.Contains(a));

        var response = new DnsMessage { IsResponse = true, Id = legacy ? query.Id : (ushort)0 };
        if (legacy) response.Questions.AddRange(query.Questions);
        response.Answers.AddRange(answers);
        response.Additionals.AddRange(additionals.Distinct());

        if (legacy || wantsUnicast)
        {
            lock (sendGate) TrySend(multicastSocket!, response.Encode(), sender);
        }

        // Always answer by multicast as well. omaclip asks from an ephemeral port, so the reply above
        // goes to that port by unicast, and a Linux firewall such as Omarchy's ufw drops it (it doesn't
        // match the outgoing multicast query). omaclip also listens on the mDNS group, which gets through.
        var multicast = legacy ? new DnsMessage { IsResponse = true } : response;
        if (legacy)
        {
            multicast.Answers.AddRange(response.Answers);
            multicast.Additionals.AddRange(response.Additionals);
        }
        List<LocalAddress> on;
        lock (gate) on = locals.Where(l => addresses.Contains(l.Address)).ToList();
        SendMulticast(multicastSocket, multicast, on.FirstOrDefault()?.Address);
    }

    private bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        lock (gate) return locals.Any(l => l.Address.Equals(address));
    }

    private static bool SameSubnet(LocalAddress local, IPAddress other)
    {
        var a = local.Address.GetAddressBytes();
        var b = other.GetAddressBytes();
        var m = local.Mask.GetAddressBytes();
        if (b.Length != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        }
        return true;
    }

    // MARK: Collecting answers

    private static string? InstanceLabelOf(string name)
    {
        var suffix = "." + ServiceType;
        var trimmed = name.TrimEnd('.');
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length
            ? trimmed[..^suffix.Length]
            : null;
    }

    private void HandleResponse(DnsMessage message)
    {
        var now = DateTimeOffset.UtcNow;
        var toQuery = new List<DnsQuestion>();
        lock (gate)
        {
            // A records first, so a host's address set from this packet replaces older ones.
            var freshAddresses = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in message.AllRecords.Where(r => r.Type == DnsType.A))
            {
                var name = record.Name.TrimEnd('.');
                if (record.Ttl == 0) continue;
                if (!freshAddresses.TryGetValue(name, out var list)) freshAddresses[name] = list = [];
                if (!list.Contains(record.Address!)) list.Add(record.Address!);
            }
            foreach (var (name, list) in freshAddresses) hostAddresses[name] = list;

            foreach (var record in message.AllRecords)
            {
                switch (record.Type)
                {
                    case DnsType.Ptr when DnsMessage.NamesEqual(record.Name, ServiceType):
                    {
                        var label = InstanceLabelOf(record.Target!);
                        if (label is null || label.Equals(InstanceLabel, StringComparison.OrdinalIgnoreCase)) break;
                        if (record.Ttl == 0)
                        {
                            instances.Remove(label);
                            break;
                        }
                        if (!instances.TryGetValue(label, out var instance)) instances[label] = instance = new Instance();
                        instance.LastSeen = now;
                        break;
                    }
                    case DnsType.Srv:
                    case DnsType.Txt:
                    {
                        var label = InstanceLabelOf(record.Name);
                        if (label is null || label.Equals(InstanceLabel, StringComparison.OrdinalIgnoreCase)) break;
                        if (record.Ttl == 0)
                        {
                            instances.Remove(label);
                            break;
                        }
                        if (!instances.TryGetValue(label, out var instance)) instances[label] = instance = new Instance();
                        instance.LastSeen = now;
                        if (record.Type == DnsType.Srv)
                        {
                            instance.Target = record.Target!.TrimEnd('.');
                            instance.Port = record.Port;
                        }
                        else
                        {
                            instance.Text = record.Text;
                        }
                        break;
                    }
                }
            }

            // Ask directly for whatever an instance is still missing.
            foreach (var (label, instance) in instances)
            {
                var incomplete = instance.Target is null || instance.Text is null ||
                                 !hostAddresses.ContainsKey(instance.Target);
                if (!incomplete || now - instance.LastQueried < TimeSpan.FromSeconds(2)) continue;
                instance.LastQueried = now;
                var full = label + "." + ServiceType;
                if (instance.Target is null) toQuery.Add(new DnsQuestion(full, DnsType.Srv));
                if (instance.Text is null) toQuery.Add(new DnsQuestion(full, DnsType.Txt));
                if (instance.Target is not null && !hostAddresses.ContainsKey(instance.Target))
                    toQuery.Add(new DnsQuestion(instance.Target, DnsType.A));
            }
        }

        if (toQuery.Count > 0)
        {
            var query = new DnsMessage();
            query.Questions.AddRange(toQuery);
            SendMulticast(querySocket, query, null);
        }
        Reconcile();
    }

    /// <summary>Recomputes the usable peers, drops stale ones, and reports changes.</summary>
    private void Reconcile()
    {
        IReadOnlyList<Peer> peers;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in instances.Where(i => now - i.Value.LastSeen > PeerLifetime).Select(i => i.Key).ToList())
                instances.Remove(stale);

            var list = new List<Peer>();
            foreach (var (label, instance) in instances)
            {
                if (instance.Target is null || instance.Port <= 0 || instance.Text is null) continue;
                var version = instance.Text.FirstOrDefault(t => t.StartsWith("version=", StringComparison.Ordinal));
                var hash = instance.Text.FirstOrDefault(t => t.StartsWith("ph=", StringComparison.Ordinal));
                if (version != "version=" + ProtocolVersion || hash != "ph=" + shortHash) continue;
                if (!hostAddresses.TryGetValue(instance.Target, out var addresses) || addresses.Count == 0) continue;
                var address = addresses.FirstOrDefault(a => locals.Any(l => SameSubnet(l, a))) ?? addresses[0];
                list.Add(new Peer(label, address.ToString(), instance.Port));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (list.SequenceEqual(lastReported)) return;
            lastReported = peers = list;
        }
        PeersChanged?.Invoke(peers);
    }
}
