using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GooglePlayApkDownloader;

internal sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;
    public string? Email { get; set; }
    public string? OutputDir { get; set; }
    public bool Split { get; set; } = true;
    public int Source { get; set; }
    public string? AssetRipperPath { get; set; }
    public string? ProtectedToken { get; set; }
    public bool RememberCredentials { get; set; } = true;
    public int LastMode { get; set; }

    [JsonIgnore]
    public string Token { get; set; } = string.Empty;

    [JsonIgnore]
    public bool CredentialDecryptionFailed { get; set; }
}

internal static class SettingsStore
{
    internal static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GooglePlayApkDownloader");

    internal static readonly string SettingsPath = Path.Combine(AppDirectory, "settings.json");

    public static AppSettings Load(Action<string>? log = null)
    {
        var settings = new AppSettings();
        try
        {
            if (!File.Exists(SettingsPath)) return settings;

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            settings.SchemaVersion = GetInt(root, "SchemaVersion", 1);
            settings.Email = GetString(root, "Email");
            settings.OutputDir = GetString(root, "OutputDir");
            settings.Split = GetBool(root, "Split", true);
            settings.Source = Math.Clamp(GetInt(root, "Source", 0), 0, 2);
            settings.AssetRipperPath = GetString(root, "AssetRipperPath");
            settings.ProtectedToken = GetString(root, "ProtectedToken");
            settings.RememberCredentials = GetBool(root, "RememberCredentials", !string.IsNullOrEmpty(settings.ProtectedToken));
            settings.LastMode = Math.Clamp(GetInt(root, "LastMode", 0), 0, 2);
            if (!string.IsNullOrEmpty(settings.ProtectedToken))
            {
                try
                {
                    settings.Token = Dpapi.Unprotect(settings.ProtectedToken);
                }
                catch (Exception ex)
                {
                    settings.CredentialDecryptionFailed = true;
                    log?.Invoke("凭据解密失败，原加密数据已保留：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("读取设置失败：" + ex.Message);
        }
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        settings.SchemaVersion = 3;
        if (!settings.RememberCredentials)
        {
            settings.ProtectedToken = null;
            settings.CredentialDecryptionFailed = false;
        }
        else if (!settings.CredentialDecryptionFailed || !string.IsNullOrWhiteSpace(settings.Token))
        {
            settings.ProtectedToken = !string.IsNullOrWhiteSpace(settings.Token)
                ? Dpapi.Protect(settings.Token.Trim())
                : null;
            settings.CredentialDecryptionFailed = false;
        }

        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, SettingsPath, true);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, "ApkResourceAssistant", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
