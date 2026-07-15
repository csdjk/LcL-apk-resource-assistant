using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace GooglePlayApkDownloader;

internal enum AssetRipperLaunchOutcome
{
    AutoLoaded,
    Fallback
}

internal enum AssetRipperLaunchStage
{
    Validating,
    ReusingInstance,
    AllocatingPort,
    StartingInstance,
    WaitingForServer,
    ResettingInstance,
    LoadingFolder,
    StartingFallback,
    Completed
}

internal sealed record AssetRipperLaunchProgress(AssetRipperLaunchStage Stage, string Message);

internal sealed record AssetRipperLaunchResult(
    AssetRipperLaunchOutcome Outcome,
    string ExecutablePath,
    string InputDirectory,
    int? Port,
    int? ProcessId,
    string Message,
    string? AutomaticLoadError)
{
    public bool AutoLoaded => Outcome == AssetRipperLaunchOutcome.AutoLoaded;
    public bool Fallback => Outcome == AssetRipperLaunchOutcome.Fallback;

    // The launcher deliberately leaves these UI actions to its caller.
    public bool ShouldCopyInputPath => Fallback;
    public bool ShouldOpenInputDirectory => Fallback;
}

internal sealed record AssetRipperLauncherOptions(
    TimeSpan StartupTimeout,
    TimeSpan RequestTimeout,
    TimeSpan PollInterval,
    int MaximumPortAttempts)
{
    public static AssetRipperLauncherOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(250),
        3);

    public void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        if (RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (MaximumPortAttempts is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(MaximumPortAttempts), "端口尝试次数必须为 1 到 3。");
    }
}

/// <summary>
/// A test seam around process creation and loopback port allocation. Tests can
/// combine this interface with an injected HttpClient/HttpMessageHandler without
/// launching the real AssetRipper UI.
/// </summary>
internal interface IAssetRipperLaunchPlatform
{
    int AllocateLoopbackPort();
    IAssetRipperProcessHandle StartManaged(string executablePath, int port);
    IAssetRipperProcessHandle StartFallback(string executablePath);
}

internal interface IAssetRipperProcessHandle : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void TryTerminate();
}

internal sealed class AssetRipperLauncher : IDisposable
{
    private const string LoopbackHost = "127.0.0.1";

    private readonly AssetRipperLauncherOptions _options;
    private readonly IAssetRipperLaunchPlatform _platform;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    private ManagedSession? _managedSession;
    private int _disposed;

    public AssetRipperLauncher()
        : this(null, null, null, null)
    {
    }

    internal AssetRipperLauncher(
        AssetRipperLauncherOptions? options,
        IAssetRipperLaunchPlatform? platform,
        HttpClient? httpClient,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        _options = options ?? AssetRipperLauncherOptions.Default;
        _options.Validate();
        _platform = platform ?? new WindowsAssetRipperLaunchPlatform();
        _delayAsync = delayAsync ?? Task.Delay;

        if (httpClient is null)
        {
            _httpClient = CreateLoopbackHttpClient();
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public async Task<AssetRipperLaunchResult> LaunchAsync(
        string executablePath,
        string inputDirectory,
        IProgress<AssetRipperLaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Report(progress, AssetRipperLaunchStage.Validating, "正在检查 AssetRipper 和输入目录…");

        var normalizedExecutable = ValidateExecutable(executablePath);
        var normalizedInput = ValidateInputDirectory(inputDirectory);

        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (TryGetReusableSession(normalizedExecutable, out var reusable))
            {
                try
                {
                    Report(progress, AssetRipperLaunchStage.ReusingInstance, "正在复用已启动的 AssetRipper…");
                    EnsureProcessIsRunning(reusable.Process);

                    Report(progress, AssetRipperLaunchStage.ResettingInstance, "正在重置 AssetRipper 当前会话…");
                    await PostResetAsync(reusable.Port, cancellationToken).ConfigureAwait(false);
                    EnsureProcessIsRunning(reusable.Process);

                    Report(progress, AssetRipperLaunchStage.LoadingFolder, "正在自动载入分析目录…");
                    await PostLoadFolderAsync(reusable.Port, normalizedInput, cancellationToken).ConfigureAwait(false);
                    EnsureProcessIsRunning(reusable.Process);

                    var reusedResult = CreateAutoLoadedResult(
                        normalizedExecutable,
                        normalizedInput,
                        reusable,
                        "已复用 AssetRipper 并自动载入分析目录。");
                    Report(progress, AssetRipperLaunchStage.Completed, reusedResult.Message);
                    return reusedResult;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return UseExistingAsFallback(normalizedExecutable, normalizedInput, reusable.Process,
                        reusable.Port, ex, progress);
                }
            }

            return await StartManagedAndLoadAsync(
                    normalizedExecutable,
                    normalizedInput,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private async Task<AssetRipperLaunchResult> StartManagedAndLoadAsync(
        string executablePath,
        string inputDirectory,
        IProgress<AssetRipperLaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startupClock = Stopwatch.StartNew();
        Exception? automaticLoadError = null;

        for (var attempt = 1; attempt <= _options.MaximumPortAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (startupClock.Elapsed >= _options.StartupTimeout)
            {
                automaticLoadError = new TimeoutException("等待 AssetRipper HTTP 服务超时。");
                break;
            }

            IAssetRipperProcessHandle? process = null;
            try
            {
                Report(
                    progress,
                    AssetRipperLaunchStage.AllocatingPort,
                    $"正在分配本机端口（第 {attempt}/{_options.MaximumPortAttempts} 次）…");
                var port = _platform.AllocateLoopbackPort();
                ValidatePort(port);

                Report(progress, AssetRipperLaunchStage.StartingInstance, $"正在启动 AssetRipper（127.0.0.1:{port}）…");
                process = _platform.StartManaged(executablePath, port);

                Report(progress, AssetRipperLaunchStage.WaitingForServer, "正在等待 AssetRipper 界面服务就绪…");
                var remaining = _options.StartupTimeout - startupClock.Elapsed;
                await WaitUntilReadyAsync(process, port, remaining, cancellationToken).ConfigureAwait(false);
                EnsureProcessIsRunning(process);

                try
                {
                    Report(progress, AssetRipperLaunchStage.LoadingFolder, "正在自动载入分析目录…");
                    await PostLoadFolderAsync(port, inputDirectory, cancellationToken).ConfigureAwait(false);
                    EnsureProcessIsRunning(process);

                    var session = new ManagedSession(executablePath, port, process);
                    ReplaceManagedSession(session);
                    process = null; // Only a successfully loaded process becomes a managed session.

                    var result = CreateAutoLoadedResult(
                        executablePath,
                        inputDirectory,
                        session,
                        "AssetRipper 已启动并自动载入分析目录。");
                    Report(progress, AssetRipperLaunchStage.Completed, result.Message);
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    process?.TryTerminate();
                    throw;
                }
                catch (Exception ex)
                {
                    automaticLoadError = ex;
                    if (process is { HasExited: false })
                        return UseExistingAsFallback(executablePath, inputDirectory, process, port, ex, progress);
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                process?.TryTerminate();
                throw;
            }
            catch (AssetRipperProcessExitedException ex)
            {
                automaticLoadError = ex;
                // An unsupported parameter or a port race normally makes the
                // process exit immediately. A new loopback port may recover it.
            }
            catch (TimeoutException ex)
            {
                automaticLoadError = ex;
                if (process is { HasExited: false })
                    return UseExistingAsFallback(executablePath, inputDirectory, process, null, ex, progress);
                break;
            }
            catch (Exception ex)
            {
                automaticLoadError = ex;
                // Allocation and start failures are retried with a fresh port.
            }
            finally
            {
                // Disposing this wrapper only releases our OS process handle. It
                // deliberately does not terminate an AssetRipper window.
                process?.Dispose();
            }
        }

        automaticLoadError ??= new InvalidOperationException("AssetRipper 自动载入接口未就绪。");
        return StartFallback(executablePath, inputDirectory, automaticLoadError, progress, cancellationToken);
    }

    private async Task WaitUntilReadyAsync(
        IAssetRipperProcessHandle process,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException("等待 AssetRipper HTTP 服务超时。");

        var clock = Stopwatch.StartNew();
        Exception? lastError = null;

        while (clock.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureProcessIsRunning(process);

            var requestBudget = Min(_options.RequestTimeout, timeout - clock.Elapsed);
            if (requestBudget <= TimeSpan.Zero)
                break;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, CreateLoopbackUri(port, "/"));
                using var response = await SendAsync(request, requestBudget, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;

                lastError = new HttpRequestException(
                    $"AssetRipper 根路径返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException)
            {
                lastError = ex;
            }

            var delay = Min(_options.PollInterval, timeout - clock.Elapsed);
            if (delay > TimeSpan.Zero)
                await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
        }

        EnsureProcessIsRunning(process);
        throw new TimeoutException("等待 AssetRipper HTTP 服务超时。", lastError);
    }

    private async Task PostResetAsync(int port, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateLoopbackUri(port, "/Reset"))
        {
            Content = content
        };
        using var response = await SendAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "/Reset");
    }

    private async Task PostLoadFolderAsync(int port, string inputDirectory, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Path", inputDirectory)
        ]);
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateLoopbackUri(port, "/LoadFolder"))
        {
            Content = content
        };
        using var response = await SendAsync(request, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "/LoadFolder");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"请求 {request.RequestUri?.AbsolutePath} 超时。", ex);
        }
    }

    private AssetRipperLaunchResult StartFallback(
        string executablePath,
        string inputDirectory,
        Exception automaticLoadError,
        IProgress<AssetRipperLaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, AssetRipperLaunchStage.StartingFallback, "自动载入不可用，正在以兼容模式启动 AssetRipper…");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fallbackProcess = _platform.StartFallback(executablePath);
            var result = new AssetRipperLaunchResult(
                AssetRipperLaunchOutcome.Fallback,
                executablePath,
                inputDirectory,
                null,
                fallbackProcess.Id,
                "已用兼容模式启动 AssetRipper。请复制输入目录路径，并在 AssetRipper 中手动打开；也可同时打开该目录。",
                automaticLoadError.Message);
            Report(progress, AssetRipperLaunchStage.Completed, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fallbackError)
        {
            throw new AssetRipperLaunchException(
                "AssetRipper 自动载入和兼容启动均失败。",
                automaticLoadError,
                fallbackError);
        }
    }

    private static AssetRipperLaunchResult UseExistingAsFallback(
        string executablePath,
        string inputDirectory,
        IAssetRipperProcessHandle process,
        int? port,
        Exception automaticLoadError,
        IProgress<AssetRipperLaunchProgress>? progress)
    {
        var result = new AssetRipperLaunchResult(
            AssetRipperLaunchOutcome.Fallback,
            executablePath,
            inputDirectory,
            port,
            process.Id,
            "AssetRipper 已启动，但自动载入接口不可用。请复制输入目录路径并手动打开。",
            automaticLoadError.Message);
        Report(progress, AssetRipperLaunchStage.Completed, result.Message);
        return result;
    }

    private bool TryGetReusableSession(string executablePath, out ManagedSession session)
    {
        session = null!;
        var current = _managedSession;
        if (current is null)
            return false;

        if (!PathsEqual(current.ExecutablePath, executablePath) || IsExited(current.Process))
        {
            _managedSession = null;
            current.Process.Dispose();
            return false;
        }

        session = current;
        return true;
    }

    private void ReplaceManagedSession(ManagedSession session)
    {
        var previous = _managedSession;
        _managedSession = session;
        if (previous is not null && !ReferenceEquals(previous.Process, session.Process))
            previous.Process.Dispose();
    }

    private static AssetRipperLaunchResult CreateAutoLoadedResult(
        string executablePath,
        string inputDirectory,
        ManagedSession session,
        string message) => new(
            AssetRipperLaunchOutcome.AutoLoaded,
            executablePath,
            inputDirectory,
            session.Port,
            session.Process.Id,
            message,
            null);

    private static string ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择 AssetRipper.exe。", nameof(path));

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到 AssetRipper 可执行文件。", fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AssetRipper 路径必须指向 .exe 文件。", nameof(path));
        return fullPath;
    }

    private static string ValidateInputDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择 AssetRipper 输入目录。", nameof(path));

        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"找不到 AssetRipper 输入目录：{fullPath}");
        return fullPath;
    }

    private static void ValidatePort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new InvalidOperationException($"端口分配器返回了无效端口：{port}。");
    }

    private static Uri CreateLoopbackUri(int port, string path)
    {
        ValidatePort(port);
        if (path.Length == 0 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("HTTP 路径必须是本机绝对路径。", nameof(path));

        return new UriBuilder(Uri.UriSchemeHttp, LoopbackHost, port, path).Uri;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string endpoint)
    {
        if (response.IsSuccessStatusCode || IsSuccessfulCommandRedirect(response))
            return;

        throw new HttpRequestException(
            $"AssetRipper {endpoint} 返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。");
    }

    private static bool IsSuccessfulCommandRedirect(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is < 300 or >= 400)
            return false;

        // AssetRipper's command handler redirects successful operations back to
        // "/". Redirect following stays disabled so no response can make this
        // launcher contact a non-loopback host.
        var location = response.Headers.Location;
        if (location is null)
            return false;
        if (!location.IsAbsoluteUri)
            return string.Equals(location.OriginalString, "/", StringComparison.Ordinal);

        var requestUri = response.RequestMessage?.RequestUri;
        return requestUri is not null
            && string.Equals(location.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(location.Host, LoopbackHost, StringComparison.OrdinalIgnoreCase)
            && location.Port == requestUri.Port
            && string.Equals(location.AbsolutePath, "/", StringComparison.Ordinal);
    }

    private static void EnsureProcessIsRunning(IAssetRipperProcessHandle process)
    {
        if (IsExited(process))
            throw new AssetRipperProcessExitedException("AssetRipper 进程已提前退出，可能不支持 --port 参数或端口被占用。");
    }

    private static bool IsExited(IAssetRipperProcessHandle process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static void Report(
        IProgress<AssetRipperLaunchProgress>? progress,
        AssetRipperLaunchStage stage,
        string message) => progress?.Report(new AssetRipperLaunchProgress(stage, message));

    private static HttpClient CreateLoopbackHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            UseCookies = false,
            UseProxy = false
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Process.Dispose only releases this launcher's handle. AssetRipper is
        // intentionally left running so a user can continue working in it.
        _managedSession?.Process.Dispose();
        _managedSession = null;
        if (_ownsHttpClient)
            _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record ManagedSession(
        string ExecutablePath,
        int Port,
        IAssetRipperProcessHandle Process);
}

internal sealed class AssetRipperLaunchException : Exception
{
    public AssetRipperLaunchException(
        string message,
        Exception automaticLoadError,
        Exception fallbackError)
        : base($"{message} 自动载入：{automaticLoadError.Message} 兼容启动：{fallbackError.Message}", fallbackError)
    {
        AutomaticLoadError = automaticLoadError;
        FallbackError = fallbackError;
    }

    public Exception AutomaticLoadError { get; }
    public Exception FallbackError { get; }
}

internal sealed class AssetRipperProcessExitedException : Exception
{
    public AssetRipperProcessExitedException(string message) : base(message)
    {
    }
}

internal sealed class WindowsAssetRipperLaunchPlatform : IAssetRipperLaunchPlatform
{
    public int AllocateLoopbackPort()
    {
        // Binding explicitly to IPAddress.Loopback avoids exposing a temporary
        // listener on LAN interfaces. The socket is released before AssetRipper
        // starts; the launcher's retry policy handles the unavoidable race.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public IAssetRipperProcessHandle StartManaged(string executablePath, int port)
    {
        var startInfo = CreateVisibleStartInfo(executablePath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        return Start(startInfo);
    }

    public IAssetRipperProcessHandle StartFallback(string executablePath) =>
        Start(CreateVisibleStartInfo(executablePath));

    private static ProcessStartInfo CreateVisibleStartInfo(string executablePath) => new(executablePath)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        WindowStyle = ProcessWindowStyle.Normal
    };

    private static IAssetRipperProcessHandle Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("系统没有返回 AssetRipper 进程句柄。");
        return new SystemAssetRipperProcessHandle(process);
    }
}

internal sealed class SystemAssetRipperProcessHandle : IAssetRipperProcessHandle
{
    private readonly Process _process;

    public SystemAssetRipperProcessHandle(Process process)
    {
        _process = process;
        Id = process.Id;
    }

    public int Id { get; }
    public bool HasExited => _process.HasExited;

    public void TryTerminate()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
        }
        catch
        {
            // Best effort while cancelling a process that was not handed to the user yet.
        }
    }

    public void Dispose() => _process.Dispose();
}
