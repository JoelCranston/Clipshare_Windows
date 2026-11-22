namespace Omaclip;

/// <summary>One local clipboard item offered to peers.</summary>
public sealed record SharedItem(ManifestEntry Entry, byte[] Payload);

/// <summary>
/// Thread-safe snapshot of what the sync server hands out. The UI replaces it whenever local history
/// changes; server threads only read it.
/// </summary>
public sealed class SharedHistory
{
    private sealed record Snapshot(string Checksum, byte[] ManifestBody, Dictionary<string, SharedItem> Items);

    private volatile Snapshot snapshot;

    public SharedHistory()
    {
        var empty = GoJson.Manifest([]);
        snapshot = new Snapshot(empty.Checksum, empty.Body, []);
    }

    /// <summary>Replaces the shared history. Items must be newest first.</summary>
    public void Replace(IReadOnlyList<SharedItem> items)
    {
        var encoded = GoJson.Manifest(items.Select(item => item.Entry).ToList());
        var byId = new Dictionary<string, SharedItem>(StringComparer.Ordinal);
        foreach (var item in items) byId[item.Entry.Id] = item;
        snapshot = new Snapshot(encoded.Checksum, encoded.Body, byId);
    }

    public string CurrentChecksum => snapshot.Checksum;

    public byte[] CurrentManifestBody => snapshot.ManifestBody;

    public SharedItem? Item(string id) => snapshot.Items.GetValueOrDefault(id);
}
