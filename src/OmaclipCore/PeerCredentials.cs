using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Omaclip;

/// <summary>
/// TLS material shared by everyone who knows the passphrase.
///
/// omaclip derives a deterministic P-256 CA key from the sync key; each peer signs a random leaf
/// certificate with it, and peers trust only leaves signed by that CA.
/// </summary>
public sealed class PeerCredentials : IDisposable
{
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";

    public SyncKey Key { get; }
    public ECDsa CaKey { get; }

    public PeerCredentials(SyncKey key)
    {
        Key = key;
        var seed = HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Bytes, 32, Encoding.UTF8.GetBytes("omaclip-ca-v1"), []);
        CaKey = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = ReduceToScalar(seed),
        });
    }

    public void Dispose() => CaKey.Dispose();

    /// <summary>The CA's 32-byte private scalar, big-endian.</summary>
    public byte[] CaPrivateScalar => CaKey.ExportParameters(true).D!;

    /// <summary>The CA public key as an uncompressed point (04 || X || Y).</summary>
    public byte[] CaPublicPoint
    {
        get
        {
            var q = CaKey.ExportParameters(false).Q;
            return [0x04, .. q.X!, .. q.Y!];
        }
    }

    /// <summary>Order of the P-256 group minus one, big-endian.</summary>
    private static readonly byte[] OrderMinusOne = Convert.FromHexString(
        "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632550");

    /// <summary>
    /// Computes <c>(x mod (n - 1)) + 1</c> for a 32-byte big-endian <c>x</c>, matching omaclip's Go code.
    /// Because 2^256 &lt; 2(n - 1), the modulo is at most one subtraction.
    /// </summary>
    internal static byte[] ReduceToScalar(byte[] x)
    {
        if (x.Length != 32) throw new ArgumentException("Expected 32 bytes.", nameof(x));
        var value = (byte[])x.Clone();
        if (value.AsSpan().SequenceCompareTo(OrderMinusOne) >= 0)
        {
            var borrow = 0;
            for (var i = 31; i >= 0; i--)
            {
                var digit = value[i] - OrderMinusOne[i] - borrow;
                borrow = digit < 0 ? 1 : 0;
                value[i] = (byte)(digit & 0xFF);
            }
        }
        var carry = 1;
        for (var i = 31; i >= 0 && carry > 0; i--)
        {
            var sum = value[i] + carry;
            value[i] = (byte)(sum & 0xFF);
            carry = sum >> 8;
        }
        return value;
    }

    /// <summary>
    /// A one-RDN name with the common name as a PrintableString, the way Go encodes it, so our
    /// leaf's issuer bytes match the subject of the CA certificate omaclip builds.
    /// </summary>
    internal static X500DistinguishedName PrintableName(string commonName)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSetOf())
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("2.5.4.3");
            writer.WriteCharacterString(UniversalTagNumber.PrintableString, commonName);
        }
        return new X500DistinguishedName(writer.Encode());
    }

    /// <summary>
    /// Creates a fresh server certificate signed by the shared CA, valid for the given IPv4 addresses
    /// and <c>localhost</c>. The result carries its private key and can be handed to SslStream.
    /// </summary>
    public X509Certificate2 MakeLeafCertificate(IEnumerable<IPAddress> addresses, DateTimeOffset? now = null)
    {
        var start = now ?? DateTimeOffset.UtcNow;
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(PrintableName("omaclip"), leafKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        foreach (var address in addresses) names.AddIpAddress(address);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01); // positive and without a leading zero byte
        using var signed = request.Create(
            PrintableName("omaclip-ca"),
            X509SignatureGenerator.CreateForECDsa(CaKey),
            start.AddMinutes(-5),
            start.AddDays(365),
            serial);
        using var withKey = signed.CopyWithPrivateKey(leafKey);
        // Round-trip through PKCS#12: on Windows, SChannel can't use the ephemeral key that
        // CopyWithPrivateKey produces for a server certificate.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// Accepts a peer's certificate only if the shared CA signed it, it is in date, and (when a host
    /// is given as an IPv4 literal) it names that address.
    /// </summary>
    public bool VerifyPeerCertificate(byte[] der, string? host, DateTimeOffset? now = null)
    {
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var certificate = reader.ReadSequence();
            var tbs = certificate.ReadEncodedValue();
            var algorithm = certificate.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != EcdsaWithSha256) return false;
            var signature = certificate.ReadBitString(out _);
            if (!CaKey.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return false;

            using var parsed = X509CertificateLoader.LoadCertificate(der);
            var at = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
            if (parsed.NotBefore.ToUniversalTime() > at.AddMinutes(5) || at > parsed.NotAfter.ToUniversalTime()) return false;

            if (host is null || !IPAddress.TryParse(host, out var wanted) || wanted.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return true;
            foreach (var extension in parsed.Extensions)
            {
                if (extension is X509SubjectAlternativeNameExtension san &&
                    san.EnumerateIPAddresses().Any(address => address.Equals(wanted)))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }
    }
}
