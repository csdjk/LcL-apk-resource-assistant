using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GooglePlayApkDownloader;

internal sealed record RecentTaskEntry(
    string JobRoot,
    string PackageName,
    GameEngine Engine,
    string? EngineVersion,
    WorkflowStage Stage,
    WorkflowTaskStatus Status,
    string? InputDirectory,
    string? OriginalApksDirectory,
    string? RecoveryDirectory,
    string? Source,
    DateTimeOffset UpdatedAtUtc);

internal sealed record RecentTaskIndex(int SchemaVersion, IReadOnlyList<RecentTaskEntry> Tasks);

internal sealed class RecentTaskService
{
    private const int MaximumTasks = 30;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RecentTaskService(string? indexPath = null)
    {
        _indexPath = Path.GetFullPath(indexPath ?? Path.Combine(SettingsStore.AppDirectory, "recent-tasks.json"));
    }

    public async Task<IReadOnlyList<RecentTaskEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task TrackAsync(string jobRoot, CancellationToken cancellationToken = default)
    {
        var entry = await ReadTaskAsync(jobRoot, cancellationToken);
        if (entry == null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tasks = (await LoadCoreAsync(cancellationToken)).ToList();
            tasks.RemoveAll(item => PathsEqual(item.JobRoot, entry.JobRoot));
            tasks.Insert(0, entry);
            await SaveCoreAsync(tasks, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string jobRoot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tasks = (await LoadCoreAsync(cancellationToken)).Where(item => !PathsEqual(item.JobRoot, jobRoot)).ToList();
            await SaveCoreAsync(tasks, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RecentTaskEntry>> RefreshFromOutputRootAsync(
        string? outputRoot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var byPath = (await LoadCoreAsync(cancellationToken))
                .ToDictionary(item => Path.GetFullPath(item.JobRoot), item => item, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(outputRoot) && Directory.Exists(outputRoot))
            {
                foreach (var root in EnumerateTaskRoots(outputRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = await ReadTaskAsync(root, cancellationToken);
                    if (entry != null) byPath[Path.GetFullPath(entry.JobRoot)] = entry;
                }
            }
            var tasks = byPath.Values.Where(item => Directory.Exists(item.JobRoot))
                .OrderByDescending(item => item.UpdatedAtUtc).Take(MaximumTasks).ToArray();
            await SaveCoreAsync(tasks, cancellationToken);
            return tasks;
        }
        finally { _gate.Release(); }
    }

    internal async Task<RecentTaskEntry?> ReadTaskAsync(string jobRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobRoot) || !Directory.Exists(jobRoot)) return null;
        var normalized = Path.GetFullPath(jobRoot);
        var manifest = await TaskManifestStore.TryLoadAsync(normalized, cancellationToken);
        if (manifest == null) return null;
        return new RecentTaskEntry(
            normalized,
            manifest.PackageName,
            manifest.Engine ?? GameEngine.Unknown,
            manifest.EngineVersion,
            manifest.CurrentStage,
            manifest.Status,
            ResolveExistingPath(normalized, manifest.InputDirectory),
            ResolveExistingPath(normalized, manifest.OriginalApksDirectory),
            ResolveExistingPath(normalized, manifest.RecoveryDirectory),
            manifest.Source,
            manifest.UpdatedAtUtc);
    }

    private async Task<IReadOnlyList<RecentTaskEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath)) return [];
        try
        {
            await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var index = await JsonSerializer.DeserializeAsync<RecentTaskIndex>(stream, JsonOptions, cancellationToken);
            return index?.Tasks.Where(item => Directory.Exists(item.JobRoot))
                .OrderByDescending(item => item.UpdatedAtUtc).Take(MaximumTasks).ToArray() ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    private async Task SaveCoreAsync(IEnumerable<RecentTaskEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        var tasks = entries.Where(item => Directory.Exists(item.JobRoot))
            .GroupBy(item => Path.GetFullPath(item.JobRoot), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAtUtc).First())
            .OrderByDescending(item => item.UpdatedAtUtc).Take(MaximumTasks).ToArray();
        var temp = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp,
                JsonSerializer.Serialize(new RecentTaskIndex(1, tasks), JsonOptions),
                new UTF8Encoding(false), cancellationToken);
            if (File.Exists(_indexPath)) File.Replace(temp, _indexPath, null, true);
            else File.Move(temp, _indexPath);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static IEnumerable<string> EnumerateTaskRoots(string outputRoot)
    {
        var root = Path.GetFullPath(outputRoot);
        foreach (var first in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(first, "task.json"))) yield return first;
            foreach (var second in Directory.EnumerateDirectories(first, "*", SearchOption.TopDirectoryOnly))
                if (File.Exists(Path.Combine(second, "task.json"))) yield return second;
        }
        if (File.Exists(Path.Combine(root, "task.json"))) yield return root;
    }

    private static string? ResolveExistingPath(string jobRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(jobRoot, path));
        return Directory.Exists(full) || File.Exists(full) ? full : null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
