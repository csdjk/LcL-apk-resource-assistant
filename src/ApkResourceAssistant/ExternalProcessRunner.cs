using System.Diagnostics;
using System.Text;

namespace GooglePlayApkDownloader;

internal sealed record ExternalProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? LogPath = null,
    IReadOnlyList<string>? SensitiveValues = null);

internal sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, Action<string>? output = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.WorkingDirectory);
        if (request.LogPath != null) Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
        var start = new ProcessStartInfo(request.ExecutablePath)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"启动 {Path.GetFileName(request.ExecutablePath)} 失败。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        var stdout = Redact(await stdoutTask.ConfigureAwait(false), request.SensitiveValues);
        var stderr = Redact(await stderrTask.ConfigureAwait(false), request.SensitiveValues);
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) output?.Invoke(line);
        foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) output?.Invoke(line);
        if (request.LogPath != null)
            await File.WriteAllTextAsync(request.LogPath, stdout + (string.IsNullOrEmpty(stderr) ? "" : Environment.NewLine + stderr),
                new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        return new ExternalProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string Redact(string value, IReadOnlyList<string>? secrets)
    {
        if (secrets == null) return value;
        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
}
