using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GooglePlayApkDownloader;

internal enum GameEngine
{
    Unknown,
    Unity,
    Godot,
    Unreal
}

internal sealed record SplitInfo(string FileName, string Kind, long Size);

internal sealed record AnalysisResult(
    string PackageName,
    string Source,
    string JobRoot,
    string OriginalApksDirectory,
    string InputDirectory,
    string ReportPath,
    string JsonPath,
    GameEngine Engine,
    string ScriptingBackend,
    IReadOnlyList<SplitInfo> Splits,
    IReadOnlyList<string> KeyFiles,
    IReadOnlyDictionary<string, int> ExtensionCounts,
    long ExtractedBytes,
    int ExtractedFiles);

/// <summary>
/// Backwards-compatible facade retained for the v2 UI and existing integrations.
/// The v3 UI should call <see cref="WorkflowCoordinator"/> directly.
/// </summary>
internal static class AnalysisPipeline
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task<AnalysisResult> ExtractAndAnalyzeAsync(
        string packageName,
        string source,
        string jobRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IProgress<WorkflowProgress>? workflowProgress = progress == null
            ? null
            : new Progress<WorkflowProgress>(value => progress.Report(value.Message));
        var coordinator = new WorkflowCoordinator();
        return coordinator.ContinueDownloadedTaskInPlaceAsync(
            packageName,
            source,
            jobRoot,
            workflowProgress,
            cancellationToken);
    }

    internal static async Task<(long Bytes, int Files)> ExtractApkSafelyAsync(
        string apkPath,
        string destination,
        CancellationToken cancellationToken = default)
        => await ExtractApkSafelyAsync(apkPath, destination, null, cancellationToken);

    internal static async Task<(long Bytes, int Files)> ExtractApkSafelyAsync(
        string apkPath,
        string destination,
        Action<long, int>? entryProgress,
        CancellationToken cancellationToken = default)
    {
        var destinationFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = destinationFull + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(apkPath);

        // Validate every path before writing the first byte so a malicious late entry cannot
        // leave a partially extracted archive behind.
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = ResolveSafeArchiveTarget(entry, destinationFull, rootPrefix);
        }

        Directory.CreateDirectory(destinationFull);
        long bytes = 0;
        var files = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveSafeArchiveTarget(entry, destinationFull, rootPrefix);
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, 128 * 1024, cancellationToken);
            bytes = checked(bytes + entry.Length);
            files++;
            entryProgress?.Invoke(bytes, files);
        }
        return (bytes, files);
    }

    private static string ResolveSafeArchiveTarget(ZipArchiveEntry entry, string destinationFull, string rootPrefix)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(normalized))
            throw new InvalidDataException($"APK 包含不安全路径：{entry.FullName}");

        var target = Path.GetFullPath(Path.Combine(destinationFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var directoryEntry = normalized.EndsWith('/');
        var isRootDirectoryEntry = directoryEntry && target.Equals(destinationFull, StringComparison.OrdinalIgnoreCase);
        if (!isRootDirectoryEntry && !target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"APK 包含路径穿越条目：{entry.FullName}");
        return target;
    }

    internal static GameEngine DetectEngine(IEnumerable<string> paths)
    {
        var unity = 0;
        var godot = 0;
        var unreal = 0;
        foreach (var raw in paths)
        {
            var path = raw.Replace('\\', '/').ToLowerInvariant();
            var name = Path.GetFileName(path);
            if (name is "globalgamemanagers" or "global-metadata.dat" or "libunity.so" or "libil2cpp.so") unity += 8;
            if (path.Contains("assets/bin/data/") || path.EndsWith("data.unity3d") || path.EndsWith(".assets") || path.EndsWith(".bundle")) unity += 3;
            if (path.Contains("/.godot/") || path.StartsWith(".godot/") || path.EndsWith(".pck") || name.Contains("godot")) godot += 8;
            if (path.EndsWith(".scn") || path.EndsWith(".tscn") || path.EndsWith(".res") || path.EndsWith(".tres")) godot += 2;
            if (path.EndsWith(".pak") || name is "libue4.so" or "libunreal.so" || path.Contains("ue4game/") || path.Contains("unrealengine/")) unreal += 8;
            if (path.EndsWith(".uasset") || path.EndsWith(".umap")) unreal += 3;
        }
        var best = Math.Max(unity, Math.Max(godot, unreal));
        if (best < 3) return GameEngine.Unknown;
        if (unity == best) return GameEngine.Unity;
        if (godot == best) return GameEngine.Godot;
        return GameEngine.Unreal;
    }

    internal static string DetectScriptingBackend(IEnumerable<string> paths)
    {
        var list = paths.Select(x => x.Replace('\\', '/').ToLowerInvariant()).ToList();
        if (list.Any(x => x.EndsWith("libil2cpp.so"))) return "IL2CPP";
        if (list.Any(x => x.EndsWith("assembly-csharp.dll") || x.Contains("/managed/") && x.EndsWith(".dll"))) return "Mono";
        return "未知/不适用";
    }

    internal static List<string> FindKeyFiles(IEnumerable<string> paths)
    {
        return paths.Where(raw =>
        {
            var path = raw.Replace('\\', '/').ToLowerInvariant();
            var name = Path.GetFileName(path);
            return name is "libil2cpp.so" or "global-metadata.dat" or "libunity.so" or "libue4.so" or "libunreal.so"
                    or "globalgamemanagers" or "main.pck"
                || path.EndsWith("assembly-csharp.dll") || path.EndsWith(".bundle") || path.EndsWith(".unity3d")
                || path.EndsWith(".assets") || path.EndsWith(".pak") || path.EndsWith(".pck")
                || path.EndsWith(".scn") || path.EndsWith(".tscn") || path.EndsWith(".res") || path.EndsWith(".tres")
                || path.EndsWith(".uasset") || path.EndsWith(".umap");
        }).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyDictionary<string, int> CountExtensions(IEnumerable<string> paths)
    {
        return paths
            .GroupBy(x => string.IsNullOrEmpty(Path.GetExtension(x)) ? "(无扩展名)" : Path.GetExtension(x).ToLowerInvariant())
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task WriteAnalysisArtifactsAsync(AnalysisResult result, CancellationToken cancellationToken)
    {
        var keyDir = Path.Combine(result.JobRoot, "KeyFiles");
        Directory.CreateDirectory(keyDir);
        await WriteKeyIndexesAsync(keyDir, result.KeyFiles, cancellationToken);
        await File.WriteAllTextAsync(result.ReportPath, BuildChineseReport(result), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(result.JsonPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false), cancellationToken);
    }

    private static async Task WriteKeyIndexesAsync(string keyDir, IReadOnlyList<string> keyFiles, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, Func<string, bool>>
        {
            ["IL2CPP关键文件.txt"] = x => x.EndsWith("libil2cpp.so", StringComparison.OrdinalIgnoreCase) || x.EndsWith("global-metadata.dat", StringComparison.OrdinalIgnoreCase),
            ["资源包索引.txt"] = x => x.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)
                || x.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                || x.EndsWith(".pck", StringComparison.OrdinalIgnoreCase),
            ["全部关键文件.txt"] = _ => true
        };
        foreach (var group in groups)
        {
            var content = string.Join(Environment.NewLine, keyFiles.Where(group.Value));
            await File.WriteAllTextAsync(Path.Combine(keyDir, group.Key), content, new UTF8Encoding(false), cancellationToken);
        }
    }

    internal static string BuildChineseReport(AnalysisResult result)
    {
        var recommendation = BuildRecommendation(result.Engine);
        var builder = new StringBuilder();
        builder.AppendLine("APK 下载与资源分析报告");
        builder.AppendLine("========================");
        builder.AppendLine($"包名：{result.PackageName}");
        builder.AppendLine($"下载源：{result.Source}");
        builder.AppendLine($"引擎：{result.Engine}");
        builder.AppendLine($"脚本后端：{result.ScriptingBackend}");
        builder.AppendLine($"APK 数量：{result.Splits.Count}");
        builder.AppendLine($"解压文件：{result.ExtractedFiles}（{FormatBytes(result.ExtractedBytes)}）");
        builder.AppendLine();
        builder.AppendLine("分包：");
        foreach (var split in result.Splits) builder.AppendLine($"- [{split.Kind}] {split.FileName} ({FormatBytes(split.Size)})");
        builder.AppendLine();
        builder.AppendLine("建议：");
        builder.AppendLine(recommendation);
        builder.AppendLine("安装包内不一定包含游戏启动后下载的 Addressables、OBB 或热更新资源；必要时还需采集设备上的 Android/data/<包名>/files。 ");
        builder.AppendLine();
        builder.AppendLine("关键文件：");
        foreach (var file in result.KeyFiles.Take(500)) builder.AppendLine($"- {file}");
        if (result.KeyFiles.Count > 500) builder.AppendLine($"... 其余 {result.KeyFiles.Count - 500} 项见 KeyFiles 索引和 analysis.json");
        return builder.ToString();
    }

    internal static string BuildRecommendation(GameEngine engine) => engine switch
    {
        GameEngine.Unity => "已生成 AssetRipper_Input。将整个目录交给 AssetRipper，以便同时读取 Base、ABI split 和 Asset Pack。",
        GameEngine.Godot => "检测到 Godot。AssetRipper 面向 Unity，不适用于此样本；优先检查 .pck、.scn、.res 和 assets/.godot。",
        GameEngine.Unreal => "检测到 Unreal Engine。AssetRipper 面向 Unity，不适用于此样本；优先检查 .pak、.uasset 和 .umap。",
        _ => "未识别明确引擎。请从扩展名统计和关键文件索引继续判断。"
    };

    internal static string NormalizeSplitName(string packageName, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Equals(packageName, StringComparison.OrdinalIgnoreCase)) return "base";
        if (stem.StartsWith(packageName + ".", StringComparison.OrdinalIgnoreCase)) stem = stem[(packageName.Length + 1)..];
        return SanitizeFileName(stem);
    }

    internal static string ClassifySplit(string packageName, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Equals(packageName, StringComparison.OrdinalIgnoreCase)) return "Base APK";
        if (stem.Contains("assetpack", StringComparison.OrdinalIgnoreCase)) return "Asset Pack";
        if (stem.Contains("config.arm", StringComparison.OrdinalIgnoreCase) || stem.Contains("config.x86", StringComparison.OrdinalIgnoreCase)) return "ABI";
        if (stem.Contains("dpi", StringComparison.OrdinalIgnoreCase)) return "屏幕密度";
        if (stem.Contains("config.", StringComparison.OrdinalIgnoreCase)) return "语言/配置";
        return "附加分包";
    }

    internal static string MakeUniqueName(string preferred, HashSet<string> used)
    {
        var result = preferred;
        var index = 2;
        while (!used.Add(result)) result = $"{preferred}_{index++}";
        return result;
    }

    internal static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "split" : value;
    }

    internal static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}
