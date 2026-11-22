namespace ClipShare;

/// <summary>
/// Global keyboard shortcuts via RegisterHotKey, received on a hidden message window. Needs no special
/// permission. Shortcuts are re-registered whenever they change.
/// </summary>
internal sealed class HotKeyCenter : NativeWindow, IDisposable
{
    public static readonly HotKeyCenter Shared = new();

    private sealed class Binding(Shortcut shortcut, Action action)
    {
        public Shortcut Shortcut = shortcut;
        public readonly Action Action = action;
        public bool Registered;
    }

    private readonly Dictionary<string, int> idsByName = [];
    private readonly Dictionary<int, Binding> bindings = [];
    private int nextId = 1;
    private bool suspended;

    /// <summary>Raised when registration results change, so Settings can show conflicts.</summary>
    public event Action? Changed;

    private HotKeyCenter()
    {
        CreateHandle(new CreateParams { Caption = "ClipShare hot keys", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    /// <summary>Registers (or replaces) the shortcut called <paramref name="name"/>.</summary>
    public void Register(string name, Shortcut shortcut, Action action)
    {
        if (!idsByName.TryGetValue(name, out var id)) idsByName[name] = id = nextId++;
        bindings[id] = new Binding(shortcut, action);
        RegisterAll();
    }

    /// <summary>Changes the keys for an already registered shortcut.</summary>
    public void Update(string name, Shortcut shortcut)
    {
        if (!idsByName.TryGetValue(name, out var id) || bindings[id].Shortcut == shortcut) return;
        bindings[id].Shortcut = shortcut;
        RegisterAll();
    }

    /// <summary>False when the shortcut could not be registered (usually because another app owns it).</summary>
    public bool IsRegistered(string name) =>
        suspended || (idsByName.TryGetValue(name, out var id) && bindings[id].Registered);

    /// <summary>Temporarily releases all shortcuts, e.g. while the user records a new one.</summary>
    public void Suspend()
    {
        suspended = true;
        RegisterAll();
    }

    public void Resume()
    {
        suspended = false;
        RegisterAll();
    }

    private void RegisterAll()
    {
        foreach (var (id, binding) in bindings)
        {
            if (binding.Registered) Native.UnregisterHotKey(Handle, id);
            binding.Registered = false;
        }
        if (!suspended)
        {
            foreach (var (id, binding) in bindings.OrderBy(b => b.Key))
            {
                binding.Registered = Native.RegisterHotKey(Handle, id, binding.Shortcut.Modifiers | Native.MOD_NOREPEAT, binding.Shortcut.Key);
            }
        }
        Changed?.Invoke();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && bindings.TryGetValue((int)m.WParam, out var binding))
        {
            Paster.MaskModifierRelease();
            binding.Action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var (id, binding) in bindings)
        {
            if (binding.Registered) Native.UnregisterHotKey(Handle, id);
        }
        DestroyHandle();
    }
}
