using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Omaclip;

public static class IPv4
{
    public static bool IsLiteral(string text) =>
        IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
        text.Count(c => c == '.') == 3;

    /// <summary>
    /// IPv4 addresses on interfaces that are up, plus 127.0.0.1 first, like omaclip's leaf
    /// certificate SANs.
    /// </summary>
    public static List<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Loopback };
        foreach (var (_, address) in InterfaceAddresses(includeAll: true))
        {
            if (!result.Contains(address)) result.Add(address);
        }
        return result;
    }

    /// <summary>Non-loopback IPv4 addresses with the interface index they belong to.</summary>
    /// <param name="includeAll">When false, keeps only addresses worth advertising on the LAN (see <see cref="IsAdvertisable"/>).</param>
    public static IEnumerable<(int InterfaceIndex, IPAddress Address)> InterfaceAddresses(bool includeAll)
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!includeAll && !nic.SupportsMulticast) continue;
            IPInterfaceProperties properties;
            int index;
            try
            {
                properties = nic.GetIPProperties();
                index = properties.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException) { continue; }
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                if (!includeAll && !IsAdvertisable(address)) continue;
                yield return (index, address);
            }
        }
    }

    /// <summary>
    /// The same filter omaclip applies to the addresses it advertises over mDNS: no link-local and no
    /// 172.16.0.0/12 (Docker, WSL and Hyper-V virtual switches commonly live there).
    /// </summary>
    public static bool IsAdvertisable(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 0 || b[0] == 127 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 172 && (b[1] & 0xF0) == 16) return false;
        return true;
    }

    /// <summary>
    /// Resolves a host name to an IPv4 address with the system resolver (which also handles
    /// <c>.local</c> names over mDNS and Tailscale MagicDNS). IPv4 literals are returned unchanged.
    /// </summary>
    public static async Task<string?> ResolveAsync(string host, CancellationToken cancellation = default)
    {
        if (IsLiteral(host)) return host;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellation).ConfigureAwait(false);
            return addresses.FirstOrDefault()?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

/// <summary>A remote omaclip instance to pull history from.</summary>
public sealed record Peer(string Name, string Host, int Port)
{
    /// <summary>A short label: mDNS names look like <c>hostname-port</c>, so the port suffix is dropped.</summary>
    public string DisplayName
    {
        get
        {
            if (Name.Contains(':')) return Name;
            var dot = Name.IndexOf('.');
            var baseName = dot > 0 ? Name[..dot] : Name;
            var suffix = "-" + Port;
            return baseName.EndsWith(suffix, StringComparison.Ordinal) && baseName.Length > suffix.Length
                ? baseName[..^suffix.Length]
                : baseName;
        }
    }

    /// <summary>
    /// Parses omaclip's <c>--peers-list</c> syntax, <c>[name@]host:port</c>, separated by <c>;</c>,
    /// <c>,</c> or newlines. Unlike omaclip, <c>host</c> may be a host name as well as an IPv4 address.
    /// </summary>
    public static List<Peer> ParseList(string text) =>
        text.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .OfType<Peer>()
            .ToList();

    public static Peer? Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var name = "";
        var address = text;
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            name = text[..at];
            address = text[(at + 1)..];
        }
        var parts = address.Split(':');
        if (parts.Length != 2 || !IsValidHost(parts[0]) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, null, out var port) || port is < 1 or > 65535)
            return null;
        if (name.Length == 0) name = address;
        return new Peer(name, parts[0], port);
    }

    /// <summary>An IPv4 address or a DNS host name such as <c>omarchy</c>, <c>omarchy.local</c> or <c>omarchy.tailnet.ts.net</c>.</summary>
    internal static bool IsValidHost(string host)
    {
        if (IPv4.IsLiteral(host)) return true;
        if (host.Length is 0 or > 253 || host[0] is '.' or '-') return false;
        return host.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.'));
    }
}
