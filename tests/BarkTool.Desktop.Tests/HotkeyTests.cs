using System.Text.Json;
using BarkTool.Desktop;

internal static class HotkeyTests
{
    public static void Run()
    {
        DefaultsAndParsing();
        ConflictAndRollback();
        ReuseAndDispatch();
        CleanupFailures();
        Console.WriteLine("PASS hotkeys defaults, parsing, duplicate rejection, transactional conflict, dispatch and release (injected adapter; no real hotkeys claimed)");
    }

    private static void DefaultsAndParsing()
    {
        var defaults = JsonSerializer.Deserialize<HotkeySettings>("{}")!;
        Check(!defaults.Enabled && defaults.Compose == "Ctrl+Alt+B" && defaults.Clipboard == "Ctrl+Alt+V" && defaults.Image == "Ctrl+Alt+P", "old settings disabled defaults");
        Check(JsonSerializer.Deserialize<HotkeySettings>(JsonSerializer.Serialize(defaults)) == defaults, "JSON round trip");
        Check(HotkeyCombination.TryParse(" alt + CTRL + b ", out var combo, out _) && combo == new HotkeyCombination(3, 'B'), "normalized parse");
        Check(combo.ToString() == "Ctrl+Alt+B", "canonical text");
        Check(HotkeyCombination.TryParse("Shift+Win+F24", out _, out _), "function key");
        foreach (var invalid in new[] { "B", "Ctrl+", "Ctrl+Ctrl+B", "Ctrl+B+V", "Ctrl+F25", "Ctrl++B", "Alt+Unknown" })
            Check(!HotkeyCombination.TryParse(invalid, out _, out _), "reject " + invalid);
        Check(HotkeyCombination.TryFromKey(System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.D1, out var digit) && digit.VirtualKey == '1', "WPF digit capture");
        Check(HotkeyCombination.TryFromVirtualKey(System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, 0x42, out var rawB) && rawB == new HotkeyCombination(3, 'B'), "raw virtual key capture");
        Check(HotkeyCombination.TryFromVirtualKey(System.Windows.Input.ModifierKeys.Shift, 0x4E, out var rawN) && rawN == new HotkeyCombination(4, 'N'), "virtual key not dependent on WPF Key name");
        Check(HotkeyCombination.TryFromVirtualKey(System.Windows.Input.ModifierKeys.Control, 0x70, out var f1) && f1.VirtualKey == 0x70, "function key capture");
        Check(!HotkeyCombination.TryFromVirtualKey(System.Windows.Input.ModifierKeys.None, 0x41, out _), "single letter without modifier rejected");
        Check(!HotkeyCombination.TryFromVirtualKey(System.Windows.Input.ModifierKeys.Control, 0x0D, out _), "modifier plus Enter rejected");
    }

    private static void ConflictAndRollback()
    {
        var adapter = new FakeAdapter();
        using var manager = new HotkeyManager(new IntPtr(1), _ => { }, adapter);
        var initial = new HotkeySettings { Enabled = true };
        manager.Apply(initial);
        Check(adapter.Live.Count == 3 && adapter.Live.Values.All(x => (x.Modifiers & HotkeyCombination.NoRepeat) != 0), "all three use MOD_NOREPEAT");
        var original = adapter.Live.ToDictionary();
        var calls = adapter.Calls;
        Reject(() => manager.Apply(initial with { Clipboard = "Alt+Ctrl+B" }), "重复");
        Check(adapter.Calls == calls, "duplicate rejected before adapter operation");
        Reject(() => manager.Apply(initial with { Compose = "bad" }), "编写消息");
        Check(adapter.Calls == calls, "invalid combo rejected before adapter operation");
        adapter.FailKey = 'Y';
        Reject(() => manager.Apply(initial with { Compose = "Ctrl+Alt+X", Clipboard = "Ctrl+Alt+Y" }), "发送剪贴板");
        Check(manager.Settings == initial, "failed apply does not commit settings");
        Check(original.Count == adapter.Live.Count && original.All(x => adapter.Live.TryGetValue(x.Key, out var value) && value == x.Value), "conflict preserves old registration IDs and rolls back staged keys");
        Check(adapter.Unregistered.All(id => !original.ContainsKey(id)), "conflict never releases a prior working key");
        adapter.FailKey = 0;
        manager.Apply(initial with { Compose = "Ctrl+Alt+X" });
        Check(adapter.Live.Count == 3 && adapter.Live.Values.Any(x => x.Key == 'X') && adapter.Live.Values.All(x => x.Key != 'B'), "successful replacement");
        manager.Apply(initial with { ComposeEnabled = false, ClipboardEnabled = false });
        Check(adapter.Live.Count == 1 && adapter.Live.Values.Single().Key == 'P', "per-action enable");
        manager.Apply(initial with { CaptureEnabled = true });
        Check(adapter.Live.Count == 4 && adapter.Live.Values.Any(x => x.Key == 'S'), "capture shortcut registration");
        Reject(() => manager.Apply(initial with { CaptureEnabled = true, Capture = initial.Compose }), "重复");
        manager.Apply(initial with { Enabled = false });
        Check(adapter.Live.Count == 0, "disable releases all");
        manager.Dispose();
        manager.Dispose();
        try { manager.Apply(initial); throw new Exception("disposed apply accepted"); }
        catch (ObjectDisposedException) { }
    }

    private static void ReuseAndDispatch()
    {
        var adapter = new FakeAdapter();
        var actions = new List<HotkeyAction>();
        var manager = new HotkeyManager(new IntPtr(1), actions.Add, adapter);
        manager.Apply(new() { Enabled = true });
        var compose = adapter.Live.Single(x => x.Value.Key == 'B');
        Check(manager.ProcessHotkey(compose.Key, Payload(compose.Value)), "registered callback");
        Check(actions.SequenceEqual(new[] { HotkeyAction.Compose }), "compose action");
        var calls = adapter.Calls;
        manager.Apply(new() { Enabled = true, Compose = "Ctrl+Alt+V", Clipboard = "Ctrl+Alt+B" });
        Check(adapter.Calls == calls, "swap reuses owned combos without conflict window");
        Check(manager.ProcessHotkey(compose.Key, Payload(compose.Value)) && actions.Last() == HotkeyAction.Clipboard, "swap updates action mapping");
        Check(!manager.ProcessHotkey(compose.Key, new IntPtr(0)), "wrong payload ignored");
        Check(!manager.ProcessHotkey(-100, Payload(compose.Value)), "unknown ID ignored");
        manager.Dispose();
        Check(adapter.Live.Count == 0, "dispose releases all");
        Check(!manager.ProcessHotkey(compose.Key, Payload(compose.Value)), "no dispatch after disposal");
    }

    private static void CleanupFailures()
    {
        var adapter = new FakeAdapter();
        var manager = new HotkeyManager(new IntPtr(1), _ => { }, adapter);
        var initial = new HotkeySettings { Enabled = true };
        manager.Apply(initial);
        var clipboardId = adapter.Live.Single(x => x.Value.Key == 'V').Key;
        adapter.FailReleaseId = clipboardId;
        // Compose release succeeds, then clipboard release fails; original compose is restored.
        Reject(() => manager.Apply(initial with { Enabled = false }), "释放发送剪贴板");
        Check(manager.Settings == initial && adapter.Live.Count == 3 && adapter.Live.Values.Any(x => x.Key == 'B'), "release failure restores old configuration");
        Reject(manager.Dispose, "释放发送剪贴板");
        Check(adapter.Live.Count == 1, "dispose continues after one release failure");
        adapter.FailReleaseId = 0;
        manager.Dispose();
        Check(adapter.Live.Count == 0, "failed cleanup can be retried");

        var stagedAdapter = new FakeAdapter();
        var callbacks = 0;
        using var stagedManager = new HotkeyManager(new IntPtr(1), _ => callbacks++, stagedAdapter);
        stagedManager.Apply(initial);
        stagedAdapter.FailKey = 'Y';
        stagedAdapter.FailReleaseKey = 'X';
        Reject(() => stagedManager.Apply(initial with { Compose = "Ctrl+Alt+X", Clipboard = "Ctrl+Alt+Y" }), "清理未提交的快捷键");
        var staged = stagedAdapter.Live.Single(x => x.Value.Key == 'X');
        Check(!stagedManager.ProcessHotkey(staged.Key, Payload(staged.Value)) && callbacks == 0, "uncommitted leaked registration never dispatches");
        Check(stagedManager.Settings == initial && stagedAdapter.Live.Count == 4, "cleanup failure tracks staged ownership and retains old keys");
        stagedAdapter.FailReleaseKey = 0;
        stagedManager.Dispose();
        Check(stagedAdapter.Live.Count == 0, "dispose retries staged cleanup");
    }

    private static IntPtr Payload((uint Modifiers, uint Key) combination) => new(unchecked((int)((combination.Key << 16) | (combination.Modifiers & 0xF))));

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (HotkeyConfigurationException ex)
        {
            Check(ex.Message.Contains(message), "actionable Chinese error: " + ex.Message);
            return;
        }
        throw new InvalidOperationException("expected hotkey rejection: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeAdapter : IHotkeyRegistrationAdapter
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Live { get; } = new();
        public List<int> Unregistered { get; } = new();
        public uint FailKey { get; set; }
        public int FailReleaseId { get; set; }
        public uint FailReleaseKey { get; set; }
        public int Calls { get; private set; }

        public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int errorCode)
        {
            Calls++;
            if (virtualKey == FailKey || Live.ContainsKey(id) || Live.Values.Contains((modifiers, virtualKey)))
            { errorCode = 1409; return false; }
            Live.Add(id, (modifiers, virtualKey));
            errorCode = 0;
            return true;
        }

        public bool Unregister(IntPtr window, int id, out int errorCode)
        {
            Calls++;
            if (id == FailReleaseId || (Live.TryGetValue(id, out var item) && item.Key == FailReleaseKey))
            { errorCode = 5; return false; }
            Unregistered.Add(id);
            var result = Live.Remove(id);
            errorCode = result ? 0 : 1419;
            return result;
        }
    }
}
