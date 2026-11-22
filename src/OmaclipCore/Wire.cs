using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Omaclip;

/// <summary>The payload kinds omaclip protocol v2 understands.</summary>
public enum ContentKind { Text, Image }

public static class ContentKinds
{
    public static string Wire(this ContentKind kind) => kind == ContentKind.Text ? "text" : "image";

    public static ContentKind? Parse(string value) => value switch
    {
        "text" => ContentKind.Text,
        "image" => ContentKind.Image,
        _ => null,
    };
}

/// <summary>
/// One item in a peer's shared history, without its payload. <see cref="Timestamp"/> is kept as the
/// exact string the peer sent so the manifest checksum can be recomputed byte for byte.
/// </summary>
public sealed record ManifestEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("imageMimeType")] string? ImageMimeType,
    [property: JsonPropertyName("timestamp")] string Timestamp);

/// <summary>A newest-first snapshot of a peer's shared history.</summary>
public sealed record Manifest(
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("entries")] List<ManifestEntry>? Entries);

internal sealed record ChecksumResponse([property: JsonPropertyName("checksum")] string? Checksum);

/// <summary>
/// Encodes values exactly the way Go's <c>encoding/json</c> does, which omaclip relies on when it
/// fingerprints a manifest.
/// </summary>
public static class GoJson
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Quotes and escapes a string with Go's default (HTML-safe) rules.</summary>
    public static string String(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x22: builder.Append("\\\""); break;
                case 0x5C: builder.Append("\\\\"); break;
                case 0x08: builder.Append("\\b"); break;
                case 0x0C: builder.Append("\\f"); break;
                case 0x0A: builder.Append("\\n"); break;
                case 0x0D: builder.Append("\\r"); break;
                case 0x09: builder.Append("\\t"); break;
                case < 0x20 or 0x3C or 0x3E or 0x26 or 0x2028 or 0x2029:
                    var v = rune.Value;
                    builder.Append("\\u")
                        .Append(HexDigits[(v >> 12) & 0xF]).Append(HexDigits[(v >> 8) & 0xF])
                        .Append(HexDigits[(v >> 4) & 0xF]).Append(HexDigits[v & 0xF]);
                    break;
                default:
                    builder.Append(rune.ToString());
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>Encodes manifest entries as Go would marshal <c>[]clipboard.ManifestEntry</c>.</summary>
    public static string Entries(IEnumerable<ManifestEntry> entries)
    {
        var items = entries.Select(entry =>
        {
            var fields = new List<string>
            {
                "\"id\":" + String(entry.Id),
                "\"checksum\":" + String(entry.Checksum),
                "\"contentType\":" + String(entry.ContentType),
            };
            if (!string.IsNullOrEmpty(entry.ImageMimeType)) fields.Add("\"imageMimeType\":" + String(entry.ImageMimeType));
            fields.Add("\"timestamp\":" + String(entry.Timestamp));
            return "{" + string.Join(",", fields) + "}";
        });
        return "[" + string.Join(",", items) + "]";
    }

    /// <summary>The fingerprint omaclip computes over a manifest's ordered metadata.</summary>
    public static string ManifestChecksum(IEnumerable<ManifestEntry> entries) =>
        Hashing.Sha256Hex(Encoding.UTF8.GetBytes(Entries(entries)));

    /// <summary>Encodes a whole manifest response body.</summary>
    public static (string Checksum, byte[] Body) Manifest(IReadOnlyList<ManifestEntry> entries)
    {
        var checksum = ManifestChecksum(entries);
        var body = "{\"checksum\":" + String(checksum) + ",\"entries\":" + Entries(entries) + "}\n";
        return (checksum, Encoding.UTF8.GetBytes(body));
    }
}

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>Wall-clock helpers that produce omaclip-style IDs and timestamps.</summary>
public static partial class WireClock
{
    private static readonly Lock Gate = new();
    private static ulong lastNanos;

    /// <summary>Returns a strictly increasing Unix time in nanoseconds.</summary>
    public static ulong NowNanos()
    {
        var nanos = (ulong)(DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        lock (Gate)
        {
            if (nanos <= lastNanos) nanos = lastNanos + 1;
            lastNanos = nanos;
            return nanos;
        }
    }

    /// <summary>Formats Unix nanoseconds the way Go marshals a UTC <c>time.Time</c> (RFC 3339, trimmed nanoseconds).</summary>
    public static string Timestamp(ulong nanos)
    {
        var seconds = (long)(nanos / 1_000_000_000);
        var fraction = nanos % 1_000_000_000;
        var text = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (fraction > 0) text += "." + fraction.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        return text + "Z";
    }

    /// <summary>The instant a Unix-nanosecond value names.</summary>
    public static DateTimeOffset FromNanos(ulong nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100));

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex Rfc3339();

    /// <summary>Parses an RFC 3339 timestamp with or without fractional seconds (up to nanoseconds).</summary>
    public static DateTimeOffset? Parse(string timestamp)
    {
        var match = Rfc3339().Match(timestamp);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) return null;
        var zone = match.Groups[3].Value;
        var offset = zone is "Z" or "z" ? TimeSpan.Zero : TimeSpan.ParseExact(zone[1..], @"hh\:mm", CultureInfo.InvariantCulture);
        if (zone.StartsWith('-')) offset = -offset;
        var ticks = 0L;
        if (match.Groups[2].Success)
        {
            var digits = match.Groups[2].Value.PadRight(7, '0')[..7];
            ticks = long.Parse(digits, CultureInfo.InvariantCulture);
        }
        try { return new DateTimeOffset(local.AddTicks(ticks), offset); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
