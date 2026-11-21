using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Omaclip.Mdns;

public static class DnsType
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Any = 255;
}

public sealed record DnsQuestion(string Name, ushort Type, bool UnicastResponse = false);

/// <summary>A resource record. Only the fields for its <see cref="Type"/> are set.</summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, bool CacheFlush = false)
{
    /// <summary>PTR target, or SRV target host.</summary>
    public string? Target { get; init; }
    public ushort Port { get; init; }
    public IReadOnlyList<string>? Text { get; init; }
    public IPAddress? Address { get; init; }

    public static DnsRecord Ptr(string name, string target, uint ttl) => new(name, DnsType.Ptr, ttl) { Target = target };
    public static DnsRecord Srv(string name, string target, ushort port, uint ttl, bool flush) =>
        new(name, DnsType.Srv, ttl, flush) { Target = target, Port = port };
    public static DnsRecord Txt(string name, IReadOnlyList<string> text, uint ttl, bool flush) =>
        new(name, DnsType.Txt, ttl, flush) { Text = text };
    public static DnsRecord A(string name, IPAddress address, uint ttl, bool flush) =>
        new(name, DnsType.A, ttl, flush) { Address = address };
}

/// <summary>Just enough of RFC 1035 / RFC 6762 to browse for and advertise one DNS-SD service.</summary>
public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Authorities { get; } = [];
    public List<DnsRecord> Additionals { get; } = [];

    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Authorities).Concat(Additionals);

    public static bool NamesEqual(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    // MARK: Encoding

    public byte[] Encode()
    {
        var buffer = new List<byte>(512);
        WriteUInt16(buffer, Id);
        WriteUInt16(buffer, (ushort)(IsResponse ? 0x8400 : 0x0000)); // QR + AA for responses
        WriteUInt16(buffer, (ushort)Questions.Count);
        WriteUInt16(buffer, (ushort)Answers.Count);
        WriteUInt16(buffer, (ushort)Authorities.Count);
        WriteUInt16(buffer, (ushort)Additionals.Count);
        foreach (var question in Questions)
        {
            WriteName(buffer, question.Name);
            WriteUInt16(buffer, question.Type);
            WriteUInt16(buffer, (ushort)(question.UnicastResponse ? 0x8001 : 0x0001));
        }
        foreach (var record in AllRecords) WriteRecord(buffer, record);
        return buffer.ToArray();
    }

    private static void WriteRecord(List<byte> buffer, DnsRecord record)
    {
        WriteName(buffer, record.Name);
        WriteUInt16(buffer, record.Type);
        WriteUInt16(buffer, (ushort)(record.CacheFlush ? 0x8001 : 0x0001));
        WriteUInt32(buffer, record.Ttl);
        var data = new List<byte>();
        switch (record.Type)
        {
            case DnsType.Ptr:
                WriteName(data, record.Target!);
                break;
            case DnsType.Srv:
                WriteUInt16(data, 0); // priority
                WriteUInt16(data, 0); // weight
                WriteUInt16(data, record.Port);
                WriteName(data, record.Target!);
                break;
            case DnsType.Txt:
                var strings = record.Text ?? [];
                if (strings.Count == 0) data.Add(0);
                foreach (var text in strings)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (bytes.Length > 255) throw new ArgumentException("TXT string too long");
                    data.Add((byte)bytes.Length);
                    data.AddRange(bytes);
                }
                break;
            case DnsType.A:
                data.AddRange(record.Address!.GetAddressBytes());
                break;
            default:
                throw new NotSupportedException($"Cannot encode record type {record.Type}");
        }
        WriteUInt16(buffer, (ushort)data.Count);
        buffer.AddRange(data);
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException($"DNS label too long: {label}");
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
    }

    private static void WriteUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        WriteUInt16(buffer, (ushort)(value >> 16));
        WriteUInt16(buffer, (ushort)value);
    }

    // MARK: Parsing

    /// <summary>Parses a packet, or returns null if it is malformed. Records of other types are skipped.</summary>
    public static DnsMessage? TryParse(ReadOnlySpan<byte> packet)
    {
        try
        {
            return Parse(packet.ToArray());
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    private static DnsMessage Parse(byte[] packet)
    {
        if (packet.Length < 12) throw new FormatException("short packet");
        var message = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0)),
            IsResponse = (packet[2] & 0x80) != 0,
        };
        int questions = ReadUInt16(packet, 4), answers = ReadUInt16(packet, 6),
            authorities = ReadUInt16(packet, 8), additionals = ReadUInt16(packet, 10);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            var name = ReadName(packet, ref offset);
            var type = ReadUInt16(packet, offset);
            var klass = ReadUInt16(packet, offset + 2);
            offset += 4;
            message.Questions.Add(new DnsQuestion(name, type, (klass & 0x8000) != 0));
        }
        ReadRecords(packet, ref offset, answers, message.Answers);
        ReadRecords(packet, ref offset, authorities, message.Authorities);
        ReadRecords(packet, ref offset, additionals, message.Additionals);
        return message;
    }

    private static void ReadRecords(byte[] packet, ref int offset, int count, List<DnsRecord> into)
    {
        for (var i = 0; i < count; i++)
        {
            var name = ReadName(packet, ref offset);
            var type = ReadUInt16(packet, offset);
            var klass = ReadUInt16(packet, offset + 2);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset + 4));
            var length = ReadUInt16(packet, offset + 8);
            offset += 10;
            var start = offset;
            if (start + length > packet.Length) throw new FormatException("record overruns packet");
            offset += length;
            var flush = (klass & 0x8000) != 0;
            var record = type switch
            {
                DnsType.Ptr => ReadPtr(packet, start, name, ttl),
                DnsType.Srv => ReadSrv(packet, start, name, ttl, flush),
                DnsType.Txt => DnsRecord.Txt(name, ReadTxt(packet, start, length), ttl, flush),
                DnsType.A when length == 4 => DnsRecord.A(name, new IPAddress(packet.AsSpan(start, 4)), ttl, flush),
                _ => null,
            };
            if (record is not null) into.Add(record);
        }
    }

    private static DnsRecord ReadPtr(byte[] packet, int start, string name, uint ttl)
    {
        var cursor = start;
        return DnsRecord.Ptr(name, ReadName(packet, ref cursor), ttl);
    }

    private static DnsRecord ReadSrv(byte[] packet, int start, string name, uint ttl, bool flush)
    {
        var port = ReadUInt16(packet, start + 4);
        var cursor = start + 6;
        return DnsRecord.Srv(name, ReadName(packet, ref cursor), port, ttl, flush);
    }

    private static List<string> ReadTxt(byte[] packet, int start, int length)
    {
        var result = new List<string>();
        var cursor = start;
        while (cursor < start + length)
        {
            var size = packet[cursor++];
            if (cursor + size > start + length) throw new FormatException("TXT overruns record");
            if (size > 0) result.Add(Encoding.UTF8.GetString(packet, cursor, size));
            cursor += size;
        }
        return result;
    }

    private static ushort ReadUInt16(byte[] packet, int offset) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));

    /// <summary>Reads a possibly compressed name, leaving <paramref name="offset"/> just past it.</summary>
    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            var length = packet[cursor];
            if (length == 0)
            {
                cursor++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped) offset = cursor + 2;
                jumped = true;
                if (++jumps > 32 || pointer >= packet.Length) throw new FormatException("bad compression pointer");
                cursor = pointer;
                continue;
            }
            if ((length & 0xC0) != 0) throw new FormatException("unsupported label type");
            labels.Add(Encoding.UTF8.GetString(packet, cursor + 1, length));
            cursor += 1 + length;
        }
        if (!jumped) offset = cursor;
        return string.Join('.', labels);
    }
}
