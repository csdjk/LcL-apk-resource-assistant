using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GooglePlayApkDownloader;

internal enum ExternalToolId { GdreTools, Repak, Retoc, FModel }

internal sealed record ExternalToolSpec(
    ExternalToolId Id,
    string DisplayName,
    string Version,
    string DownloadUrl,
    string ArchiveSha256,
    string ExecutableName);

internal sealed record ExternalToolProgress(string Message, long BytesReceived = 0, long? TotalBytes = null)
{
    public int? Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes.Value, 0, 100) : null;
}

internal sealed record ExternalToolInstallation(ExternalToolSpec Spec, string Directory, string ExecutablePath);

internal interface IExternalToolProvider
{
    Task<ExternalToolInstallation> EnsureInstalledAsync(
        ExternalToolId id,
        IProgress<ExternalToolProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ExternalToolManager : IExternalToolProvider, IDisposable
{
    internal static readonly IReadOnlyDictionary<ExternalToolId, ExternalToolSpec> Catalog =
        new Dictionary<ExternalToolId, ExternalToolSpec>
        {
            [ExternalToolId.GdreTools] = new(ExternalToolId.GdreTools, "GDRETools", "2.6.0-beta.4",
                "https://github.com/GDRETools/gdsdecomp/releases/download/v2.6.0-beta.4/GDRE_tools-v2.6.0-beta.4-windows.zip",
                "F3780089035594805B82AFD5B853645134B78D6E5774A21C3074F62A6B8E0F85", "gdre_tools.exe"),
            [ExternalToolId.Repak] = new(ExternalToolId.Repak, "repak", "0.2.3",
                "https://github.com/trumank/repak/releases/download/v0.2.3/repak_cli-x86_64-pc-windows-msvc.zip",
                "6720D602144D75DF477A99D5BEDB6EA780997546AFC335901D4937CAFEAA73FA", "repak.exe"),
            [ExternalToolId.Retoc] = new(ExternalToolId.Retoc, "retoc", "0.1.5",
                "https://github.com/trumank/retoc/releases/download/v0.1.5/retoc_cli-x86_64-pc-windows-msvc.zip",
                "CC036B06AD3BDCF7003690B00D82719980C374E48A95BF0654F9959148D263AA", "retoc.exe"),
            [ExternalToolId.FModel] = new(ExternalToolId.FModel, "FModel", "dec-2025",
                "https://github.com/4sval/FModel/releases/download/dec-2025/FModel.zip",
                "1BE47F969C716C9ED3AF5D1F941404202DC0A98760BDFC7D8E2B9AE08BE86624", "FModel.exe")
        };

    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly IReadOnlyDictionary<ExternalToolId, ExternalToolSpec> _catalog;
    private readonly ConcurrentDictionary<ExternalToolId, SemaphoreSlim> _locks = new();

    public ExternalToolManager(string? cacheRoot = null, HttpClient? httpClient = null,
        IReadOnlyDictionary<ExternalToolId, ExternalToolSpec>? catalog = null)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(SettingsStore.AppDirectory, "Tools"));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _ownsClient = httpClient == null;
        _catalog = catalog ?? Catalog;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ApkResourceAssistant", "4.0"));
    }

    public async Task<ExternalToolInstallation> EnsureInstalledAsync(ExternalToolId id,
        IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_catalog.TryGetValue(id, out var spec)) throw new ArgumentOutOfRangeException(nameof(id));
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = Path.Combine(_cacheRoot, spec.Id.ToString(), spec.Version);
            var executable = Path.Combine(destination, spec.ExecutableName);
            var marker = Path.Combine(destination, ".installed.json");
            if (File.Exists(executable) && File.Exists(marker))
            {
                try
                {
                    var installed = JsonSerializer.Deserialize<InstallMarker>(await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false));
                    var executableHash = await ComputeSha256Async(executable, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(installed?.ArchiveSha256, spec.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(installed?.ExecutableSha256, executableHash, StringComparison.OrdinalIgnoreCase))
                        return new ExternalToolInstallation(spec, destination, executable);
                }
                catch (JsonException) { }
                progress?.Report(new ExternalToolProgress($"{spec.DisplayName} 缓存完整性异常，正在重新安装…"));
            }

            progress?.Report(new ExternalToolProgress($"正在下载 {spec.DisplayName} {spec.Version}…"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var archive = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            var staging = destination + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                using var response = await _httpClient.GetAsync(spec.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new ExternalToolProgress($"正在下载 {spec.DisplayName}…", received, total));
                    }
                }
                var hash = await ComputeSha256Async(archive, cancellationToken).ConfigureAwait(false);
                if (!hash.Equals(spec.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{spec.DisplayName} 下载文件校验失败。期望 {spec.ArchiveSha256}，实际 {hash}。");

                Directory.CreateDirectory(staging);
                ExtractZipSafely(archive, staging, cancellationToken);
                if (!File.Exists(Path.Combine(staging, spec.ExecutableName)))
                    throw new InvalidDataException($"{spec.DisplayName} 压缩包中缺少 {spec.ExecutableName}。");
                var executableHash = await ComputeSha256Async(Path.Combine(staging, spec.ExecutableName), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, ".installed.json"),
                    JsonSerializer.Serialize(new InstallMarker(spec.ArchiveSha256, executableHash, DateTimeOffset.UtcNow)),
                    cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(staging, destination);
                progress?.Report(new ExternalToolProgress($"{spec.DisplayName} 已安装并通过 SHA-256 校验。"));
                return new ExternalToolInstallation(spec, destination, executable);
            }
            finally
            {
                TryDeleteFile(archive);
                TryDeleteDirectory(staging);
            }
        }
        finally { gate.Release(); }
    }

    internal static void ExtractZipSafely(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException($"工具压缩包包含不安全路径：{entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"工具压缩包包含路径穿越条目：{entry.FullName}");
            if (normalized.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private sealed record InstallMarker(string ArchiveSha256, string ExecutableSha256, DateTimeOffset InstalledAtUtc);
    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); foreach (var gate in _locks.Values) gate.Dispose(); }
}
