using System.Media;
using System.Net;
using System.Text;
using Omaclip;
using Omaclip.Mdns;
using Timer = System.Windows.Forms.Timer;

namespace ClipShare;

/// <summary>A clipboard item from this PC or a peer, as shown in the history panel.</summary>
internal sealed record ClipItem(
    string EntryId,
    string? PeerName,
    string SourceName,
    ContentKind Kind,
    string Checksum,
    string? ImageMimeType,
    string Timestamp,
    DateTimeOffset Date,
    byte[] Payload)
{
    public string Id => (PeerName ?? "local") + "/" + EntryId;
    public bool IsRemote => PeerName is not null;
    public string? Text => Kind == ContentKind.Text ? Encoding.UTF8.GetString(Payload) : null;

    public CapturedContent ToContent() => Kind == ContentKind.Text
        ? new CapturedContent.Text(Text ?? "")
        : new CapturedContent.Png(ImageCodec.ToPng(Payload) ?? Payload);
}

/// <summary>Live status for one peer, shown in the menu and settings.</summary>
internal sealed record PeerStatus(Peer Peer, DateTimeOffset? LastSync = null, string? LastError = null)
{
    public bool Connected => LastError is null && LastSync is not null;
}

/// <summary>
/// The app's state and sync engine. Lives on the UI thread; network work runs on the thread pool and
/// comes back to the UI thread through async/await.
/// </summary>
internal sealed class AppModel : IDisposable
{
    public const string LocalSourceName = "This PC";

    public Preferences Preferences { get; } = new();
    public ClipboardMonitor Monitor { get; } = new();
    public ForegroundTracker Foreground { get; } = new();

    public List<ClipItem> LocalItems { get; private set; } = [];
    public Dictionary<string, List<ClipItem>> RemoteItems { get; } = [];
    public List<PeerStatus> PeerStatuses { get; private set; } = [];
    public string NetworkStatus { get; private set; } = "Not started";
    public int? ListeningPort { get; private set; }
    public string? KeyFingerprint { get; private set; }

    /// <summary>mDNS traffic counters, or null when discovery is off (manual peers).</summary>
    public string? DiscoveryDiagnostics => discovery?.Diagnostics;

    /// <summary>Raised on the UI thread whenever anything shown in the UI changes.</summary>
    public event Action? Changed;

    private readonly SharedHistory shared = new();
    private readonly SynchronizationContext ui;
    private SyncServer? server;
    private MdnsDiscovery? discovery;
    private RemoteFetcher? fetcher;
    private PeerCredentials? credentials;
    private Timer? pollTimer;
    private Timer? addressTimer;
    private List<IPAddress> certifiedAddresses = [];
    private readonly HashSet<string> inFlight = [];
    private int generation;
    /// <summary>Newest entry per peer that auto-copy has already considered.</summary>
    private readonly Dictionary<string, string> autoCopySeen = [];

    public AppModel()
    {
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("AppModel must be created on the UI thread");
        Monitor.Captured += Record;
    }

    public void Start()
    {
        Monitor.Start();
        if (!Preferences.HasPassphrase && Preferences.OmaclipConfigPassphrase() is { } imported && SyncKey.IsValid(imported))
            Preferences.SetPassphrase(imported);
        Preferences.RefreshLaunchAtLoginPath();
        RestartNetworking();
    }

    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>All items, newest first.</summary>
    public List<ClipItem> AllItems =>
        LocalItems.Concat(RemoteItems.Values.SelectMany(items => items)).OrderByDescending(item => item.Date).ToList();

    public ClipItem? LatestRemoteItem =>
        RemoteItems.Values.Select(items => items.FirstOrDefault()).OfType<ClipItem>().MaxBy(item => item.Date);

    // MARK: Clipboard

    private void Record(CapturedContent content)
    {
        var nanos = WireClock.NowNanos();
        var (kind, payload, mime) = content switch
        {
            CapturedContent.Text text => (ContentKind.Text, Encoding.UTF8.GetBytes(text.Value), (string?)null),
            CapturedContent.Png png => (ContentKind.Image, png.Data, "image/png"),
            _ => throw new ArgumentOutOfRangeException(nameof(content)),
        };
        var item = new ClipItem(nanos.ToString(), null, LocalSourceName, kind, Hashing.Sha256Hex(payload), mime,
            WireClock.Timestamp(nanos), WireClock.FromNanos(nanos), payload);
        // Copying something that is already in history moves it to the top instead of duplicating it.
        LocalItems.RemoveAll(existing => existing.Kind == item.Kind && existing.Checksum == item.Checksum);
        LocalItems.Insert(0, item);
        var limit = Math.Max(Preferences.MaxHistory, 1);
        if (LocalItems.Count > limit) LocalItems.RemoveRange(limit, LocalItems.Count - limit);
        PublishShared();
        NotifyChanged();
    }

    public void PublishShared()
    {
        var items = LocalItems.Take(Math.Max(Preferences.SharedCount, 0))
            .Select(item => new SharedItem(
                new ManifestEntry(item.EntryId, item.Checksum, item.Kind.Wire(), item.ImageMimeType, item.Timestamp), item.Payload))
            .ToList();
        shared.Replace(items);
    }

    public void ClearLocalHistory()
    {
        LocalItems.Clear();
        PublishShared();
        NotifyChanged();
    }

    /// <summary>Puts an item on the system clipboard without recording it as a new local copy.</summary>
    public void CopyToClipboard(ClipItem item) => Monitor.Write(item.ToContent());

    /// <summary>The "paste latest" shortcut: copy the newest peer entry and paste it into the front app.</summary>
    public void PasteLatestRemote()
    {
        if (LatestRemoteItem is not { } item)
        {
            SystemSounds.Beep.Play();
            return;
        }
        CopyToClipboard(item);
        Paster.PasteToFrontApp(Foreground.PasteTarget());
    }

    // MARK: Networking

    public void RestartNetworking()
    {
        StopNetworking();
        var current = ++generation;
        if (Preferences.Passphrase is not { } passphrase)
        {
            NetworkStatus = "Set the omaclip passphrase in Settings to start syncing";
            NotifyChanged();
            return;
        }
        NetworkStatus = "Deriving key…";
        NotifyChanged();
        _ = Task.Run(() => new PeerCredentials(SyncKey.Derive(passphrase))).ContinueWith(task =>
        {
            ui.Post(_ =>
            {
                if (current != generation)
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    return;
                }
                if (task.IsCompletedSuccessfully) StartNetworking(task.Result);
                else
                {
                    NetworkStatus = task.Exception?.InnerException?.Message ?? "Key derivation failed";
                    NotifyChanged();
                }
            }, null);
        }, TaskScheduler.Default);
    }

    private void StartNetworking(PeerCredentials newCredentials)
    {
        credentials = newCredentials;
        KeyFingerprint = newCredentials.Key.ShortHash;
        try
        {
            var addresses = IPv4.LocalAddresses();
            server = new SyncServer(shared, newCredentials.Key, newCredentials.MakeLeafCertificate(addresses));
            server.Start(Preferences.SyncPort);
            certifiedAddresses = addresses;
            ListeningPort = server.Port;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException)
        {
            server?.Dispose();
            server = null;
            NetworkStatus = "Could not start sync server: " + ex.Message;
            NotifyChanged();
            return;
        }
        fetcher = new RemoteFetcher(newCredentials);
        PublishShared();

        var manual = Peer.ParseList(Preferences.ManualPeers);
        if (manual.Count == 0)
        {
            discovery = new MdnsDiscovery(newCredentials.Key.ShortHash);
            var current = generation;
            discovery.PeersChanged += peers => ui.Post(_ =>
            {
                if (current == generation) SetPeers(peers);
            }, null);
            try
            {
                discovery.Start(server.Port);
                NetworkStatus = $"Listening on port {ListeningPort}, looking for peers on the local network";
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                NetworkStatus = $"Listening on port {ListeningPort}, but local discovery failed ({ex.Message}). Add manual peers instead.";
            }
        }
        else
        {
            SetPeers(manual);
            NetworkStatus = $"Listening on port {ListeningPort}, using manual peers";
        }

        pollTimer = new Timer { Interval = 2000 };
        pollTimer.Tick += (_, _) => PollPeers();
        pollTimer.Start();
        addressTimer = new Timer { Interval = 30_000 };
        addressTimer.Tick += (_, _) => RefreshCertificateIfAddressesChanged();
        addressTimer.Start();
        PollPeers();
        NotifyChanged();
    }

    public void StopNetworking()
    {
        pollTimer?.Dispose();
        pollTimer = null;
        addressTimer?.Dispose();
        addressTimer = null;
        discovery?.Dispose();
        discovery = null;
        server?.Dispose();
        server = null;
        fetcher?.Dispose();
        fetcher = null;
        credentials?.Dispose();
        credentials = null;
        ListeningPort = null;
        KeyFingerprint = null;
        PeerStatuses = [];
        RemoteItems.Clear();
        inFlight.Clear();
        autoCopySeen.Clear();
    }

    private void SetPeers(IReadOnlyList<Peer> peers)
    {
        var previous = PeerStatuses.ToDictionary(status => status.Peer.Name);
        PeerStatuses = peers.Select(peer =>
        {
            if (!previous.TryGetValue(peer.Name, out var status)) return new PeerStatus(peer);
            return status.Peer == peer ? status : new PeerStatus(peer, status.LastSync);
        }).ToList();
        var names = peers.Select(peer => peer.Name).ToHashSet();
        foreach (var name in RemoteItems.Keys.Where(name => !names.Contains(name)).ToList())
        {
            RemoteItems.Remove(name);
            autoCopySeen.Remove(name);
            fetcher?.Forget(name);
        }
        PollPeers();
        NotifyChanged();
    }

    private void RefreshCertificateIfAddressesChanged()
    {
        var addresses = IPv4.LocalAddresses();
        if (addresses.ToHashSet().SetEquals(certifiedAddresses) || credentials is null || server is null) return;
        try
        {
            server.UpdateCertificate(credentials.MakeLeafCertificate(addresses));
            certifiedAddresses = addresses;
            if (ListeningPort is not null) NotifyChanged();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            System.Diagnostics.Debug.WriteLine($"ClipShare: certificate refresh failed: {ex.Message}");
        }
    }

    private void PollPeers()
    {
        if (fetcher is null) return;
        foreach (var status in PeerStatuses.Where(status => !inFlight.Contains(status.Peer.Name)).ToList())
        {
            _ = PollAsync(fetcher, status.Peer, generation);
        }
    }

    private async Task PollAsync(RemoteFetcher client, Peer peer, int current)
    {
        inFlight.Add(peer.Name);
        IReadOnlyList<RemoteEntry>? entries = null;
        Exception? failure = null;
        try
        {
            entries = await Task.Run(() => client.FetchAsync(peer));
        }
        catch (Exception ex) when (ex is PeerException or OperationCanceledException or HttpRequestException or ObjectDisposedException)
        {
            failure = ex;
        }
        if (current != generation) return;
        inFlight.Remove(peer.Name);
        Apply(peer, entries, failure);
    }

    private void Apply(Peer peer, IReadOnlyList<RemoteEntry>? entries, Exception? failure)
    {
        var index = PeerStatuses.FindIndex(status => status.Peer.Name == peer.Name);
        if (index < 0) return;
        var before = PeerStatuses[index];
        if (failure is not null)
        {
            PeerStatuses[index] = before with { LastError = failure.Message };
            if (before.LastError != failure.Message) NotifyChanged();
            return;
        }
        PeerStatuses[index] = before with { LastError = null, LastSync = DateTimeOffset.Now };
        if (entries is null)
        {
            if (!before.Connected) NotifyChanged();
            return;
        }
        var items = entries.Select(entry => new ClipItem(entry.Id, peer.Name, peer.DisplayName, entry.Kind, entry.Checksum,
            entry.ImageMimeType, entry.Timestamp, entry.Date, entry.Payload)).ToList();
        RemoteItems[peer.Name] = items;
        AutoCopyIfNeeded(items.FirstOrDefault(), peer);
        NotifyChanged();
    }

    private void AutoCopyIfNeeded(ClipItem? newest, Peer peer)
    {
        if (newest is null) return;
        var key = newest.Kind.Wire() + ":" + newest.Checksum;
        var firstSync = !autoCopySeen.TryGetValue(peer.Name, out var seen);
        autoCopySeen[peer.Name] = key;
        // Only react to copies made after we connected, and never bounce back what this PC just shared.
        if (!Preferences.AutoCopyRemote || firstSync || seen == key) return;
        if (LocalItems.FirstOrDefault() is { } local && local.Kind == newest.Kind && local.Checksum == newest.Checksum) return;
        CopyToClipboard(newest);
    }

    public void Dispose()
    {
        StopNetworking();
        Monitor.Dispose();
        Foreground.Dispose();
    }
}
