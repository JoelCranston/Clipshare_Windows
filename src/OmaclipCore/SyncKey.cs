using System.Text;
using Konscious.Security.Cryptography;

namespace Omaclip;

public sealed class PassphraseException(string message) : Exception(message);

/// <summary>The 32-byte Argon2id key every omaclip peer derives from the shared passphrase.</summary>
public sealed class SyncKey : IEquatable<SyncKey>
{
    public byte[] Bytes { get; }

    /// <summary>Sent as the <c>X-Omaclip-Pass</c> header on every request.</summary>
    public string Hex { get; }

    /// <summary>Advertised in mDNS TXT records as <c>ph=</c> so peers can skip mismatched meshes.</summary>
    public string ShortHash => Hex[..16];

    public SyncKey(byte[] bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("A sync key is 32 bytes.", nameof(bytes));
        Bytes = bytes;
        Hex = Convert.ToHexStringLower(bytes);
    }

    /// <summary>Applies omaclip's passphrase rules (lengths are in UTF-8 bytes, like Go's <c>len</c>).</summary>
    public static void Validate(string passphrase)
    {
        var length = Encoding.UTF8.GetByteCount(passphrase);
        if (length < 8) throw new PassphraseException("The passphrase must be at least 8 characters.");
        if (length > 128) throw new PassphraseException("The passphrase must be at most 128 characters.");
        if (passphrase[0] is ' ' or '\t') throw new PassphraseException("The passphrase must not start with whitespace.");
        if (passphrase[^1] is ' ' or '\t') throw new PassphraseException("The passphrase must not end with whitespace.");
    }

    public static bool IsValid(string passphrase)
    {
        try { Validate(passphrase); return true; } catch (PassphraseException) { return false; }
    }

    /// <summary>
    /// Argon2id(passphrase, salt: passphrase reversed by code point, t=1, m=64 MiB, p=4, 32 bytes).
    /// Slow on purpose (around a hundred milliseconds), so call it off the UI thread.
    /// </summary>
    public static SyncKey Derive(string passphrase)
    {
        Validate(passphrase);
        var runes = passphrase.EnumerateRunes().Reverse();
        var reversed = new StringBuilder();
        foreach (var rune in runes) reversed.Append(rune.ToString());
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = Encoding.UTF8.GetBytes(reversed.ToString()),
            Iterations = 1,
            MemorySize = 64 * 1024,
            DegreeOfParallelism = 4,
        };
        return new SyncKey(argon.GetBytes(32));
    }

    public bool Equals(SyncKey? other) => other is not null && Hex == other.Hex;
    public override bool Equals(object? obj) => Equals(obj as SyncKey);
    public override int GetHashCode() => Hex.GetHashCode(StringComparison.Ordinal);
}
