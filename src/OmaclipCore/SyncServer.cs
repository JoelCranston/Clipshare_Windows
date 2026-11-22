using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Omaclip;

/// <summary>
/// The HTTPS endpoint omaclip peers poll to read this computer's clipboard history.
///
/// Routes (all require <c>X-Omaclip-Pass: &lt;sync key hex&gt;</c>):
/// <c>GET /api/clipboard/checksum</c>, <c>GET /api/clipboard</c>, <c>GET /api/clipboard/{id}/content</c>.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly SharedHistory history;
    private readonly byte[] authHex;
    private readonly CancellationTokenSource stopping = new();
    private volatile X509Certificate2 certificate;
    private TcpListener? listener;

    public int Port { get; private set; }

    public SyncServer(SharedHistory history, SyncKey key, X509Certificate2 certificate)
    {
        this.history = history;
        authHex = Encoding.ASCII.GetBytes(key.Hex);
        this.certificate = certificate;
    }

    /// <summary>Swaps in a new certificate (e.g. after this computer's IP addresses change). Open connections keep the old one.</summary>
    public void UpdateCertificate(X509Certificate2 newCertificate) => certificate = newCertificate;

    /// <summary>Binds on all IPv4 interfaces. Port 0 picks a free port; read <see cref="Port"/> afterwards.</summary>
    public void Start(int port)
    {
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start(64);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener, stopping.Token);
    }

    public void Stop()
    {
        if (!stopping.IsCancellationRequested) stopping.Cancel();
        listener?.Stop();
        listener = null;
    }

    public void Dispose()
    {
        Stop();
        stopping.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener server, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = ServeAsync(client, cancellation);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellation)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                handshake.CancelAfter(HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, handshake.Token).ConfigureAwait(false);
            }

            var reader = new BufferedStream(tls, 8192);
            while (!cancellation.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(IdleTimeout);
                var request = await HttpRequest.ReadAsync(reader, idle.Token).ConfigureAwait(false);
                if (request is null) return;
                var keepAlive = request.KeepAlive;
                var (status, contentType, body) = Respond(request);
                await WriteResponseAsync(tls, request.Version, status, contentType, body, keepAlive, cancellation).ConfigureAwait(false);
                if (!keepAlive) return;
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException
                                       or SocketException or ObjectDisposedException or InvalidDataException)
        {
            // The peer went away or spoke something other than HTTPS; nothing to report.
        }
    }

    private bool Authorized(HttpRequest request)
    {
        var given = Encoding.ASCII.GetBytes(request.Header("X-Omaclip-Pass") ?? "");
        return CryptographicOperations.FixedTimeEquals(given, authHex);
    }

    private const string PlainText = "text/plain; charset=utf-8";

    private (int Status, string ContentType, byte[] Body) Respond(HttpRequest request)
    {
        if (request.Method != "GET") return (405, PlainText, "method not allowed\n"u8.ToArray());
        if (!Authorized(request)) return (401, PlainText, "unauthorized\n"u8.ToArray());

        var path = request.Target.Split('?', 2)[0];
        const string json = "application/json";
        if (path == "/api/clipboard/checksum")
            return (200, json, Encoding.UTF8.GetBytes("{\"checksum\":" + GoJson.String(history.CurrentChecksum) + "}\n"));
        if (path == "/api/clipboard")
            return (200, json, history.CurrentManifestBody);

        const string prefix = "/api/clipboard/", suffix = "/content";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal) &&
            path.Length > prefix.Length + suffix.Length)
        {
            var id = Uri.UnescapeDataString(path[prefix.Length..^suffix.Length]);
            if (history.Item(id) is { } item)
            {
                var type = item.Entry.ContentType == "text" ? PlainText : item.Entry.ImageMimeType ?? "application/octet-stream";
                return (200, type, item.Payload);
            }
        }
        return (404, PlainText, "not found\n"u8.ToArray());
    }

    private static async Task WriteResponseAsync(Stream stream, string version, int status, string contentType, byte[] body,
        bool keepAlive, CancellationToken cancellation)
    {
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = new StringBuilder()
            .Append(version).Append(' ').Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Date: ").Append(DateTime.UtcNow.ToString("r")).Append("\r\n");
        if (!keepAlive) head.Append("Connection: close\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>The parts of an HTTP/1.x request this server needs.</summary>
    private sealed class HttpRequest
    {
        public required string Method { get; init; }
        public required string Target { get; init; }
        public required string Version { get; init; }
        public required List<(string Name, string Value)> Headers { get; init; }

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        public bool KeepAlive
        {
            get
            {
                var connection = Header("Connection") ?? "";
                if (connection.Contains("close", StringComparison.OrdinalIgnoreCase)) return false;
                return Version == "HTTP/1.1" || connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Reads one request (and skips any body). Returns null when the connection closed cleanly.</summary>
        public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken cancellation)
        {
            var bytes = new List<byte>(512);
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, cancellation).ConfigureAwait(false);
                if (read == 0)
                {
                    if (bytes.Count == 0) return null;
                    throw new InvalidDataException("connection closed mid-request");
                }
                bytes.Add(one[0]);
                if (bytes.Count > MaxHeaderBytes) throw new InvalidDataException("request header too large");
                var n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
                // Ignore blank lines before the request line, as Go does.
                if (n == 2 && bytes[0] == '\r' && bytes[1] == '\n') bytes.Clear();
            }

            var lines = Encoding.Latin1.GetString(bytes.ToArray()).Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new InvalidDataException("malformed request line");
            var headers = new List<(string, string)>();
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("malformed header");
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            var request = new HttpRequest { Method = requestLine[0], Target = requestLine[1], Version = requestLine[2], Headers = headers };

            if (request.Header("Transfer-Encoding") is not null) throw new InvalidDataException("request bodies are not supported");
            if (long.TryParse(request.Header("Content-Length"), out var length) && length > 0)
            {
                if (length > 1024 * 1024) throw new InvalidDataException("request body too large");
                var skip = new byte[length];
                await stream.ReadExactlyAsync(skip, cancellation).ConfigureAwait(false);
            }
            return request;
        }
    }
}
