using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GooglePlayApkDownloader;

internal enum DownloadSource
{
    ApkPure,
    GooglePlayAnonymous,
    GooglePlayPersonal
}

internal sealed record DownloadRequest
{
    public string PackageOrUrl { get; init; } = "";
    public string OutputRoot { get; init; } = "";
    public DownloadSource Source { get; init; } = DownloadSource.ApkPure;
    public bool DownloadSplitApks { get; init; } = true;
    public string? GoogleEmail { get; init; }
    public string? GoogleToken { get; init; }
}

internal sealed record PreparedDownload(
    string PackageName,
    DownloadSource Source,
    string JobDirectory,
    string OriginalApksDirectory,
    string ManifestPath,
    DateTimeOffset CreatedAtUtc,
    TaskManifest Manifest);

internal sealed record DownloadResult(
    string PackageName,
    DownloadSource Source,
    string JobDirectory,
    string OriginalApksDirectory,
    IReadOnlyList<string> ApkFiles,
    string ManifestPath,
    DateTimeOffset CreatedAtUtc,
    TaskManifest Manifest)
{
    public string SourceLabel => DownloadService.GetSourceLabel(Source);
}

internal sealed record GooglePlayCredentials(string Email, string Token);

/// <summary>
/// Owns the APK download stage. This service deliberately does not extract or
/// inspect the downloaded packages; callers decide whether and when to proceed.
/// </summary>
internal sealed class DownloadService : IDisposable
{
    private const string AuroraAuthEndpoint = "https://auroraoss.com/api/auth/";
    private const string BundledApkeepResourceName = "BundledApkeep.exe";
    private const ulong DesiredStackReserve = 64UL * 1024 * 1024;

    private static readonly Regex PackageNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AasTokenPattern = new(
        @"AAS\s+Token:\s*(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly SemaphoreSlim EnginePreparationLock = new(1, 1);
    private static readonly object JobDirectoryLock = new();

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Action<string>? _log;
    private readonly string _apkeepPath;
    private bool _disposed;

    public DownloadService(HttpClient? httpClient = null, Action<string>? log = null, string? apkeepPath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _log = log;
        _apkeepPath = apkeepPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GooglePlayApkDownloader",
            "tools",
            "apkeep.exe");
    }

    internal string ApkeepPath => _apkeepPath;

    /// <summary>
    /// Validates the request, prepares the embedded downloader and creates a new
    /// timestamped task containing an empty Original_APKs directory.
    /// </summary>
    public async Task<PreparedDownload> PrepareAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var packageName = ParsePackageName(request.PackageOrUrl);
        var outputRoot = ValidateOutputRoot(request.OutputRoot);
        if (!Enum.IsDefined(request.Source))
            throw new ArgumentOutOfRangeException(nameof(request.Source), request.Source, "未知下载源。");
        ValidateCredentials(request);

        Log("正在准备内置下载引擎…");
        await EnsureApkeepAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        TaskManifest? manifest = null;
        try
        {
            var jobDirectory = CreateUniqueJobDirectory(outputRoot, packageName);
            var originalApksDirectory = Path.Combine(jobDirectory, "Original_APKs");
            Directory.CreateDirectory(originalApksDirectory);

            var createdAtUtc = DateTimeOffset.UtcNow;
            manifest = new TaskManifest
            {
                PackageName = packageName,
                Source = GetSourceLabel(request.Source),
                Mode = WorkflowMode.Download,
                Status = WorkflowTaskStatus.Pending,
                CurrentStage = WorkflowStage.Created,
                JobRoot = jobDirectory,
                OriginalApksDirectory = originalApksDirectory,
                SourceFiles = [],
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc
            };
            var prepared = new PreparedDownload(
                packageName,
                request.Source,
                jobDirectory,
                originalApksDirectory,
                Path.Combine(jobDirectory, "task.json"),
                createdAtUtc,
                manifest);

            // Persist the task before honoring a late cancellation so every created
            // directory has a terminal state that can be inspected or resumed.
            await TaskManifestStore.SaveAsync(manifest, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Log($"已创建下载任务：{jobDirectory}");
            return prepared;
        }
        catch (OperationCanceledException)
        {
            if (manifest != null) await TaskManifestStore.TryRecordTerminalStateAsync(manifest, true, null).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (manifest != null) await TaskManifestStore.TryRecordTerminalStateAsync(manifest, false, ex).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a task and downloads its APK files. No extraction is performed.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        return await DownloadAsync(prepared, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads into an already prepared task. This overload lets a coordinator
    /// expose the task directory before starting the network/process work.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        PreparedDownload prepared,
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(request);
        var manifest = prepared.Manifest;

        try
        {
            EnsurePreparationMatchesRequest(prepared, request);
            cancellationToken.ThrowIfCancellationRequested();
            manifest = await TaskManifestStore.TransitionAsync(
                manifest,
                WorkflowStage.Downloading,
                WorkflowTaskStatus.Running,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            switch (prepared.Source)
            {
                case DownloadSource.ApkPure:
                    await DownloadFromApkPureAsync(prepared, cancellationToken).ConfigureAwait(false);
                    break;
                case DownloadSource.GooglePlayAnonymous:
                case DownloadSource.GooglePlayPersonal:
                    await DownloadFromGooglePlayAsync(prepared, request, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Source), request.Source, "未知下载源。");
            }

            var apkFiles = Directory
                .EnumerateFiles(prepared.OriginalApksDirectory, "*.apk", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (apkFiles.Length == 0)
                throw new InvalidOperationException("下载进程已经结束，但任务目录中没有找到 APK 文件。");

            manifest = manifest with
            {
                SourceFiles = apkFiles,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            manifest = await TaskManifestStore.TransitionAsync(
                manifest,
                WorkflowStage.Downloaded,
                WorkflowTaskStatus.Completed,
                WorkflowStage.Downloaded,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Log($"下载完成，共 {apkFiles.Length} 个 APK。");
            return new DownloadResult(
                prepared.PackageName,
                prepared.Source,
                prepared.JobDirectory,
                prepared.OriginalApksDirectory,
                apkFiles,
                prepared.ManifestPath,
                prepared.CreatedAtUtc,
                manifest);
        }
        catch (OperationCanceledException)
        {
            await TaskManifestStore.TryRecordTerminalStateAsync(manifest, true, null).ConfigureAwait(false);
            Log("下载已取消。");
            throw;
        }
        catch (Exception ex)
        {
            await TaskManifestStore.TryRecordTerminalStateAsync(manifest, false, ex).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<GooglePlayCredentials> RequestAnonymousCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log("正在申请匿名 Google Play 临时凭据…");

        using var request = new HttpRequestMessage(HttpMethod.Get, AuroraAuthEndpoint);
        request.Headers.UserAgent.ParseAdd("AuroraStore/4.6.5");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"匿名服务返回 HTTP {(int)response.StatusCode}，请稍后再试。");

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("email", out var emailElement) ||
                !json.RootElement.TryGetProperty("auth", out var authElement))
                throw new InvalidOperationException("匿名服务响应缺少 email 或 auth 字段。");

            var email = emailElement.GetString();
            var token = authElement.GetString();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) ||
                !token.StartsWith("ya29.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("匿名服务返回的数据格式不正确。");

            Log("匿名 Google Play 凭据申请成功。");
            return new GooglePlayCredentials(email, token);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("匿名服务返回的不是有效 JSON。", ex);
        }
    }

    public async Task<string> ExchangeOAuthAsync(
        string email,
        string oneTimeOAuthToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        email = ValidateSingleLineValue(email, "Google 邮箱");
        oneTimeOAuthToken = ValidateSingleLineValue(oneTimeOAuthToken, "一次性 OAuth token");
        if (!oneTimeOAuthToken.StartsWith("oauth2_4/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("一次性 OAuth token 应以 oauth2_4/ 开头。");

        await EnsureApkeepAsync(cancellationToken).ConfigureAwait(false);
        Log("正在兑换 Google Play AAS token…");
        var run = await RunProcessAsync(
            _apkeepPath,
            ["-e", email, "--oauth-token", oneTimeOAuthToken],
            cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"token 兑换失败，下载引擎退出码：{run.ExitCode}。");

        var match = AasTokenPattern.Match(run.CapturedOutput);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            throw new InvalidOperationException("下载引擎输出中没有识别到 AAS token。");

        Log("AAS token 兑换完成。");
        return match.Groups[1].Value;
    }

    internal static string ParsePackageName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("请输入 Google Play 链接或 Android 包名。");

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("play.google.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.play.google.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("商店链接必须来自 play.google.com。");

            var match = Regex.Match(uri.Query, @"(?:^|[?&])id=([^&]+)", RegexOptions.CultureInvariant);
            if (!match.Success)
                throw new InvalidOperationException("Google Play 链接中缺少应用 id 参数。");
            candidate = Uri.UnescapeDataString(match.Groups[1].Value);
        }

        if (!PackageNamePattern.IsMatch(candidate))
            throw new InvalidOperationException("Android 包名格式不正确。");
        return candidate;
    }

    internal static string GetSourceLabel(DownloadSource source) => source switch
    {
        DownloadSource.ApkPure => "APKPure",
        DownloadSource.GooglePlayAnonymous => "Google Play（一键匿名）",
        DownloadSource.GooglePlayPersonal => "Google Play（个人账号）",
        _ => "未知"
    };

    private async Task DownloadFromApkPureAsync(
        PreparedDownload prepared,
        CancellationToken cancellationToken)
    {
        Log("正在从 APKPure 下载…");
        var run = await RunProcessAsync(
            _apkeepPath,
            ["-a", prepared.PackageName, "-d", "apk-pure", prepared.OriginalApksDirectory],
            cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException(
                $"APKPure 下载进程退出码：{run.ExitCode}。可切换到 Google Play（一键匿名）重试。");
    }

    private async Task DownloadFromGooglePlayAsync(
        PreparedDownload prepared,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        GooglePlayCredentials credentials;
        if (prepared.Source == DownloadSource.GooglePlayAnonymous)
        {
            credentials = await RequestAnonymousCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            credentials = new GooglePlayCredentials(
                ValidateSingleLineValue(request.GoogleEmail, "Google 邮箱"),
                ValidateSingleLineValue(request.GoogleToken, "AAS/AUTH token"));
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"apkeep-{Guid.NewGuid():N}.ini");
        try
        {
            var isAuthToken = credentials.Token.StartsWith("ya29.", StringComparison.OrdinalIgnoreCase);
            var tokenKey = isAuthToken ? "auth_token" : "aas_token";
            var config = $"[google]\nemail = {credentials.Email}\n{tokenKey} = {credentials.Token}\n";
            await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var options = "locale=zh_CN,timezone=Asia/Shanghai" +
                          (request.DownloadSplitApks ? ",split_apk=true" : "");
            var arguments = new List<string>
            {
                "-a", prepared.PackageName,
                "-d", "google-play",
                "-i", configPath,
                "-o", options
            };
            if (isAuthToken)
                arguments.Add("--accept-tos");
            arguments.Add(prepared.OriginalApksDirectory);

            Log("正在从 Google Play 下载…");
            var run = await RunProcessAsync(_apkeepPath, arguments, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"Google Play 下载进程退出码：{run.ExitCode}。");
        }
        finally
        {
            TryDeleteFile(configPath);
        }
    }

    private async Task EnsureApkeepAsync(CancellationToken cancellationToken)
    {
        await EnginePreparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_apkeepPath))
            {
                var parent = Path.GetDirectoryName(_apkeepPath)
                             ?? throw new InvalidOperationException("内置下载引擎路径无效。");
                Directory.CreateDirectory(parent);
                var tempPath = _apkeepPath + $".{Guid.NewGuid():N}.download";
                try
                {
                    await using var source = typeof(DownloadService).Assembly
                        .GetManifestResourceStream(BundledApkeepResourceName)
                        ?? throw new InvalidOperationException("程序中缺少内置下载引擎资源。");
                    await using (var target = new FileStream(
                                     tempPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     81920,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(tempPath, _apkeepPath, false);
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            PatchPeStackReserve(_apkeepPath);
        }
        finally
        {
            EnginePreparationLock.Release();
        }
    }

    private static void PatchPeStackReserve(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length < 256)
            return;

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (reader.ReadUInt16() != 0x5A4D)
            return;

        stream.Position = 0x3c;
        var peHeaderOffset = reader.ReadInt32();
        if (peHeaderOffset < 0 || peHeaderOffset > stream.Length - 112)
            return;

        stream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != 0x00004550)
            return;

        var optionalHeaderOffset = peHeaderOffset + 24L;
        stream.Position = optionalHeaderOffset;
        var magic = reader.ReadUInt16();
        var stackReserveOffset = optionalHeaderOffset + 72L;
        if (magic == 0x20B)
        {
            if (stackReserveOffset > stream.Length - sizeof(ulong))
                return;
            stream.Position = stackReserveOffset;
            if (reader.ReadUInt64() >= DesiredStackReserve)
                return;
            stream.Position = stackReserveOffset;
            writer.Write(DesiredStackReserve);
        }
        else if (magic == 0x10B)
        {
            if (stackReserveOffset > stream.Length - sizeof(uint))
                return;
            stream.Position = stackReserveOffset;
            if (reader.ReadUInt32() >= DesiredStackReserve)
                return;
            stream.Position = stackReserveOffset;
            writer.Write((uint)DesiredStackReserve);
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("启动内置下载引擎失败。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("启动内置下载引擎失败。", ex);
        }

        var outputLines = new List<string>();
        var outputLock = new object();
        var standardOutput = PumpReaderAsync(process.StandardOutput, outputLines, outputLock);
        var standardError = PumpReaderAsync(process.StandardError, outputLines, outputLock);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKillProcessTree((Process)state!), process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            var exited = await WaitForExitWithoutCancellationAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (exited)
            {
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }
            else
            {
                _ = Task.WhenAll(standardOutput, standardError).ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        lock (outputLock)
            return new ProcessRunResult(process.ExitCode, string.Join(Environment.NewLine, outputLines));
    }

    private async Task PumpReaderAsync(StreamReader reader, List<string> capture, object captureLock)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (captureLock)
                capture.Add(line);
            Log(RedactCredentialOutput(line));
        }
    }

    private static async Task<bool> WaitForExitWithoutCancellationAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited) return true;
            using var timeoutSource = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The process already exited or never reached a waitable state.
            return true;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string RedactCredentialOutput(string line)
    {
        if (AasTokenPattern.IsMatch(line))
            return AasTokenPattern.Replace(line, "AAS Token: ***");
        return line;
    }

    private static string ValidateOutputRoot(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new InvalidOperationException("请选择 APK 保存根目录。");
        try
        {
            return Path.GetFullPath(outputRoot.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("APK 保存根目录无效。", ex);
        }
    }

    private static void ValidateCredentials(DownloadRequest request)
    {
        if (request.Source != DownloadSource.GooglePlayPersonal)
            return;
        _ = ValidateSingleLineValue(request.GoogleEmail, "Google 邮箱");
        _ = ValidateSingleLineValue(request.GoogleToken, "AAS/AUTH token");
    }

    private static string ValidateSingleLineValue(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"请填写{fieldName}。");
        if (normalized.Contains('\r') || normalized.Contains('\n'))
            throw new InvalidOperationException($"{fieldName}格式不正确。");
        return normalized;
    }

    private static void EnsurePreparationMatchesRequest(PreparedDownload prepared, DownloadRequest request)
    {
        var requestPackage = ParsePackageName(request.PackageOrUrl);
        if (!string.Equals(prepared.PackageName, requestPackage, StringComparison.Ordinal) ||
            prepared.Source != request.Source)
            throw new InvalidOperationException("下载请求与已准备的任务不匹配。");
        var relativeOriginalDirectory = Path.GetRelativePath(
            Path.GetFullPath(prepared.JobDirectory),
            Path.GetFullPath(prepared.OriginalApksDirectory));
        if (!Directory.Exists(prepared.OriginalApksDirectory) ||
            relativeOriginalDirectory.Equals("..", StringComparison.Ordinal) ||
            relativeOriginalDirectory.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeOriginalDirectory))
            throw new InvalidOperationException("任务的 Original_APKs 目录无效。");
        ValidateCredentials(request);
    }

    private static string CreateUniqueJobDirectory(string outputRoot, string packageName)
    {
        var root = Path.GetFullPath(outputRoot);
        var safePackage = AnalysisPipeline.SanitizeFileName(packageName);
        lock (JobDirectoryLock)
        {
            Directory.CreateDirectory(root);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var taskName = $"{safePackage}_{timestamp}";
            var candidate = Path.Combine(root, taskName);
            var suffix = 2;
            while (Directory.Exists(candidate))
                candidate = Path.Combine(root, $"{taskName}-{suffix++}");
            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private void Log(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch
        {
            // Logging must never fail a download.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed record ProcessRunResult(int ExitCode, string CapturedOutput);
}
