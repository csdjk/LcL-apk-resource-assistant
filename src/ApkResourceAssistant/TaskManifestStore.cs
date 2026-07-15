using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GooglePlayApkDownloader;

internal static class TaskManifestStore
{
    private const string FileName = "task.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetPath(string jobRoot) => Path.Combine(Path.GetFullPath(jobRoot), FileName);

    public static async Task<TaskManifest?> TryLoadAsync(string jobRoot, CancellationToken cancellationToken = default)
    {
        var path = GetPath(jobRoot);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<TaskManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(TaskManifest manifest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.JobRoot)) throw new ArgumentException("任务清单缺少任务目录。", nameof(manifest));
        var path = GetPath(manifest.JobRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken);
            if (File.Exists(path))
                File.Replace(temp, path, null, true);
            else
                File.Move(temp, path);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
            gate.Release();
        }
    }

    public static async Task<TaskManifest> TransitionAsync(
        TaskManifest manifest,
        WorkflowStage stage,
        WorkflowTaskStatus status = WorkflowTaskStatus.Running,
        WorkflowStage? completedStage = null,
        string? inputDirectory = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var completed = manifest.CompletedStages?.ToList() ?? [];
        if (completedStage.HasValue && !completed.Contains(completedStage.Value)) completed.Add(completedStage.Value);
        var updated = manifest with
        {
            CurrentStage = stage,
            Status = status,
            CompletedStages = completed,
            InputDirectory = inputDirectory ?? manifest.InputDirectory,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastError = error
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public static async Task TryRecordTerminalStateAsync(TaskManifest manifest, bool cancelled, Exception? exception)
    {
        try
        {
            await TransitionAsync(
                manifest,
                manifest.CurrentStage,
                cancelled ? WorkflowTaskStatus.Cancelled : WorkflowTaskStatus.Failed,
                error: cancelled ? "操作已取消。" : exception?.Message,
                cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Preserve the original failure if the disk or manifest itself is unavailable.
        }
    }
}
