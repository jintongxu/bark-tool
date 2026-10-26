using System.Runtime.InteropServices;
using System.Text.Json;

namespace BarkTool.Desktop;

internal sealed record AppSettings(
    string BarkUrl = "", string DeviceKey = "", string ImageUrl = "", string ImageToken = "",
    string Group = "Bark Tool", double? FloatingLeft = null, double? FloatingTop = null, string EncryptionKey = "", HotkeySettings? Hotkeys = null, FloatingOptions? Floating = null,
    string GameProcesses = "", string NotificationLevel = "active", bool AutoCopy = true, ImageOptimizationOptions? ImageOptimization = null);

internal static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarkTool");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly string SecretPath = Path.Combine(DirectoryPath, "secrets.dat");

    private sealed record PublicSettings(string BarkUrl, string ImageUrl, string Group, double? FloatingLeft, double? FloatingTop,
        HotkeySettings? Hotkeys = null, FloatingOptions? Floating = null,
        string GameProcesses = "", string NotificationLevel = "active", bool AutoCopy = true, ImageOptimizationOptions? ImageOptimization = null);
    private sealed record Secrets(string DeviceKey, string ImageToken, string EncryptionKey = "");

    public static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PublicSettings>(File.ReadAllText(FilePath))
                : null;
            Secrets? secrets = null;
            if (File.Exists(SecretPath))
            {
                var plaintext = Dpapi.Unprotect(File.ReadAllBytes(SecretPath));
                secrets = JsonSerializer.Deserialize<Secrets>(plaintext);
            }
            return new AppSettings(settings?.BarkUrl ?? "", secrets?.DeviceKey ?? "",
                settings?.ImageUrl ?? "", secrets?.ImageToken ?? "", settings?.Group ?? "Bark Tool",
                settings?.FloatingLeft, settings?.FloatingTop, secrets?.EncryptionKey ?? "", settings?.Hotkeys,
                settings?.Floating, settings?.GameProcesses ?? "", settings?.NotificationLevel ?? "active", settings?.AutoCopy ?? true, settings?.ImageOptimization);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("无法读取本地设置；请检查本机用户数据目录。", ex);
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var encrypted = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new Secrets(settings.DeviceKey, settings.ImageToken, settings.EncryptionKey)));
        var publicSettings = JsonSerializer.Serialize(new PublicSettings(settings.BarkUrl, settings.ImageUrl, settings.Group, settings.FloatingLeft, settings.FloatingTop,
            settings.Hotkeys, settings.Floating, settings.GameProcesses, settings.NotificationLevel, settings.AutoCopy, settings.ImageOptimization));
        var secretTemp = SecretPath + ".tmp";
        var configTemp = FilePath + ".tmp";
        File.WriteAllBytes(secretTemp, encrypted);
        File.WriteAllText(configTemp, publicSettings);
        File.Move(secretTemp, SecretPath, true);
        File.Move(configTemp, FilePath, true);
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] input) => Transform(input, true);
    public static byte[] Unprotect(byte[] input) => Transform(input, false);

    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var blob = new Blob { Length = bytes.Length, Data = pointer };
            Blob output;
            var ok = encrypt
                ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out output);
            if (!ok) throw new System.Security.Cryptography.CryptographicException("无法解密当前用户的本地配置。");
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
