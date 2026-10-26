using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;

namespace BarkTool.Desktop;

public enum HotkeyAction { Compose, Clipboard, Image, Capture }

// Strings intentionally remain human-readable in JSON and in the settings controls.
// Missing fields in old settings files retain these disabled defaults.
public sealed record HotkeySettings
{
    public bool Enabled { get; init; }
    public bool ComposeEnabled { get; init; } = true;
    public bool ClipboardEnabled { get; init; } = true;
    public bool ImageEnabled { get; init; } = true;
    public bool CaptureEnabled { get; init; }
    public string Capture { get; init; } = "Ctrl+Alt+S";
    public string Compose { get; init; } = "Ctrl+Alt+B";
    public string Clipboard { get; init; } = "Ctrl+Alt+V";
    public string Image { get; init; } = "Ctrl+Alt+P";
}

public readonly record struct HotkeyCombination(uint Modifiers, uint VirtualKey)
{
    public const uint NoRepeat = 0x4000;

    public static bool TryParse(string? text, out HotkeyCombination combination, out string error)
    {
        combination = default;
        error = "请输入组合键，例如 Ctrl+Alt+B（支持 Ctrl、Alt、Shift、Win 和字母、数字、F1–F24）。";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        uint key = 0;
        foreach (var token in tokens)
        {
            var upper = token.ToUpperInvariant();
            uint modifier = upper switch { "ALT" => 1, "CTRL" or "CONTROL" => 2, "SHIFT" => 4, "WIN" or "WINDOWS" => 8, _ => 0 };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }
            if (key != 0) return false;
            if (upper.Length == 1 && (upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
                key = upper[0];
            else if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), NumberStyles.None,
                         CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
                key = (uint)(0x70 + function - 1);
            else return false;
        }
        if (key == 0 || modifiers == 0) return false;
        combination = new(modifiers, key);
        error = "";
        return true;
    }

    // Useful for a small settings editor that captures WPF KeyEventArgs.
    public static bool TryFromKey(ModifierKeys modifiers, Key key, out HotkeyCombination combination)
    {
        return TryFromVirtualKey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), out combination);
    }

    // Virtual keys are derived from the raw key code, so 主键盘数字与 Edit/NumLock 状态不影响结果。
    public static bool TryFromVirtualKey(ModifierKeys modifiers, uint virtualKey, out HotkeyCombination combination)
    {
        if (virtualKey is 0x0D or 0x20) return TryParse(string.Join('+', ModifierTokens(modifiers)), out combination, out _);
        var parts = ModifierTokens(modifiers);
        if (virtualKey is >= 0x30 and <= 0x39) parts.Add(((char)virtualKey).ToString());
        else if (virtualKey is >= 0x41 and <= 0x5A) parts.Add(((char)virtualKey).ToString());
        else if (virtualKey is >= 0x70 and <= 0x87) parts.Add($"F{virtualKey - 0x70 + 1}");
        else return TryParse("", out combination, out _);
        return TryParse(string.Join('+', parts), out combination, out _);
    }

    private static List<string> ModifierTokens(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(VirtualKey is >= 0x70 and <= 0x87 ? $"F{VirtualKey - 0x70 + 1}" : ((char)VirtualKey).ToString());
        return string.Join('+', parts);
    }
}

public interface IHotkeyRegistrationAdapter
{
    bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int errorCode);
    bool Unregister(IntPtr window, int id, out int errorCode);
}

public sealed class HotkeyConfigurationException : InvalidOperationException
{
    public HotkeyAction? Action { get; }
    public HotkeyConfigurationException(string message, HotkeyAction? action = null) : base(message) => Action = action;
}

/// <summary>
/// Window-thread-affine global hotkeys, without keyboard hooks. Apply stages new combinations
/// before releasing old ones; a registration conflict never relinquishes a working old hotkey.
/// Dispose before destroying the HwndSource. Native cleanup errors are reported, not ignored.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private sealed record Registration(int Id, HotkeyCombination Combination, HotkeyAction Action);
    private const int WmHotkey = 0x0312;
    private static int nextId = 0x4000;
    private readonly IntPtr window;
    private readonly IHotkeyRegistrationAdapter adapter;
    private readonly Action<HotkeyAction> callback;
    private readonly HwndSource? source;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<HotkeyCombination, Registration> registrations = new();
    private bool disposed;
    public HotkeySettings Settings { get; private set; } = new();

    public HotkeyManager(HwndSource source, Action<HotkeyAction> callback)
        : this(source?.Handle ?? throw new ArgumentNullException(nameof(source)), callback, new NativeRegistrationAdapter())
    {
        source.Dispatcher.VerifyAccess();
        this.source = source;
        source.AddHook(WindowHook);
    }

    // Injection constructor intentionally does not attach a native hook or claim any real keys.
    public HotkeyManager(IntPtr window, Action<HotkeyAction> callback, IHotkeyRegistrationAdapter adapter)
    {
        if (window == IntPtr.Zero) throw new ArgumentException("快捷键需要有效窗口句柄。", nameof(window));
        this.window = window;
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.Compose => "编写消息", HotkeyAction.Clipboard => "发送剪贴板", HotkeyAction.Image => "发送图片", HotkeyAction.Capture => "截图预览", _ => action.ToString()
    };

    public static bool TryValidate(HotkeySettings settings, out string error)
    {
        try { _ = Parse(settings); error = ""; return true; }
        catch (HotkeyConfigurationException ex) { error = ex.Message; return false; }
    }

    private static Dictionary<HotkeyCombination, HotkeyAction> Parse(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parsed = new Dictionary<HotkeyCombination, HotkeyAction>();
        foreach (var (action, text, enabled) in new[] { (HotkeyAction.Compose, settings.Compose, settings.ComposeEnabled), (HotkeyAction.Clipboard, settings.Clipboard, settings.ClipboardEnabled), (HotkeyAction.Image, settings.Image, settings.ImageEnabled), (HotkeyAction.Capture, settings.Capture, settings.CaptureEnabled) })
        {
            if (!enabled) continue;
            if (!HotkeyCombination.TryParse(text, out var combination, out var error))
                throw new HotkeyConfigurationException($"{ActionName(action)}：{error}", action);
            if (parsed.TryGetValue(combination, out var previous))
                throw new HotkeyConfigurationException($"{ActionName(action)}与{ActionName(previous)}的快捷键重复：{combination}。请使用不同组合。", action);
            parsed.Add(combination, action);
        }
        return parsed;
    }

    public void Apply(HotkeySettings settings)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        var desired = Parse(settings); // Reject all duplicates before any native operation, even when disabled.
        if (!settings.Enabled) desired.Clear();
        var added = new List<Registration>();
        foreach (var (combination, action) in desired)
        {
            if (registrations.ContainsKey(combination)) continue;
            var id = Interlocked.Increment(ref nextId);
            if (id > 0xBFFF)
            {
                var cleanup = ReleaseStaged(added);
                throw new HotkeyConfigurationException("快捷键标识已用尽，请重启应用。" + cleanup, action);
            }
            if (!adapter.Register(window, id, combination.Modifiers | HotkeyCombination.NoRepeat, combination.VirtualKey, out var error))
            {
                var cleanup = ReleaseStaged(added);
                throw new HotkeyConfigurationException($"{ActionName(action)}快捷键 {combination} 注册失败，可能已被其他程序占用。{NativeError(error)}原有快捷键保持不变。{cleanup}", action);
            }
            added.Add(new(id, combination, action));
        }

        var removed = new List<Registration>();
        foreach (var old in registrations.Values.ToArray())
        {
            if (desired.ContainsKey(old.Combination)) continue;
            if (!adapter.Unregister(window, old.Id, out var error))
            {
                var failures = ReleaseStaged(added);
                foreach (var prior in removed)
                {
                    if (adapter.Register(window, prior.Id, prior.Combination.Modifiers | HotkeyCombination.NoRepeat, prior.Combination.VirtualKey, out var restoreError))
                        registrations[prior.Combination] = prior;
                    else failures += $"恢复{ActionName(prior.Action)}失败：{NativeError(restoreError)}";
                }
                throw new HotkeyConfigurationException($"释放{ActionName(old.Action)}快捷键失败：{NativeError(error)}配置未保存。{failures}", old.Action);
            }
            registrations.Remove(old.Combination);
            removed.Add(old);
        }
        foreach (var item in added) registrations.Add(item.Combination, item);
        foreach (var combination in desired.Keys)
            registrations[combination] = registrations[combination] with { Action = desired[combination] };
        Settings = settings;
    }

    private string ReleaseStaged(IEnumerable<Registration> added)
    {
        var errors = "";
        foreach (var item in added)
        {
            if (!adapter.Unregister(window, item.Id, out var error))
            {
                // Retain ownership for a later Dispose retry, but never dispatch an uncommitted key.
                registrations[item.Combination] = item with { Action = (HotkeyAction)(-1) };
                errors += $"清理未提交的快捷键 {item.Combination} 失败：{NativeError(error)}";
            }
        }
        return errors;
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && ProcessHotkey(wParam.ToInt32(), lParam)) handled = true;
        return IntPtr.Zero;
    }

    // Public for host message forwarding and adapter tests. Validate payload as well as registration ID:
    // a queued message for a replaced/disabled combination must not invoke a new action.
    public bool ProcessHotkey(int id, IntPtr payload)
    {
        VerifyThread();
        if (disposed) return false;
        var value = unchecked((uint)payload.ToInt64());
        var combination = new HotkeyCombination(value & 0xF, (value >> 16) & 0xFFFF);
        if (!registrations.TryGetValue(combination, out var item) || item.Id != id || !Enum.IsDefined(item.Action)) return false;
        callback(item.Action);
        return true;
    }

    public void Dispose()
    {
        VerifyThread();
        disposed = true; // Never dispatch once cleanup starts, including on a cleanup failure.
        source?.RemoveHook(WindowHook);
        var errors = new List<string>();
        foreach (var item in registrations.Values.ToArray())
        {
            if (adapter.Unregister(window, item.Id, out var error)) registrations.Remove(item.Combination);
            else errors.Add($"释放{ActionName(item.Action)}快捷键 {item.Combination} 失败：{NativeError(error)}");
        }
        if (errors.Count > 0) throw new HotkeyConfigurationException(string.Join("；", errors));
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != threadId)
            throw new InvalidOperationException("快捷键操作必须在创建窗口的线程执行。");
    }

    private static string NativeError(int error) => $"系统错误 {error}（{new Win32Exception(error).Message}）。";

    private sealed class NativeRegistrationAdapter : IHotkeyRegistrationAdapter
    {
        public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int errorCode)
        {
            var success = RegisterHotKey(window, id, modifiers, virtualKey);
            errorCode = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
        public bool Unregister(IntPtr window, int id, out int errorCode)
        {
            var success = UnregisterHotKey(window, id);
            errorCode = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
