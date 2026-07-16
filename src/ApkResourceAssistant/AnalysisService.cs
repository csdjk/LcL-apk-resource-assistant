using System.Text.Json;

namespace GooglePlayApkDownloader;

internal sealed class AnalysisService
{
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public async Task<AnalysisResult> AnalyzeExtractionAsync(
        ExtractionResult extraction,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = extraction.Manifest;
        try
        {
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.AnalyzingFiles,
                inputDirectory: extraction.InputDirectory, cancellationToken: cancellationToken);
            progress?.Report(new WorkflowProgress(WorkflowStage.AnalyzingFiles, "识别游戏引擎和关键文件", 0));
            var scan = await ScanCoreAsync(extraction.InputDirectory, progress, cancellationToken);
            var reportPath = Path.Combine(extraction.JobRoot, "分析说明.txt");
            var jsonPath = Path.Combine(extraction.JobRoot, "analysis.json");
            var result = new AnalysisResult(
                extraction.PackageName,
                extraction.Source,
                extraction.JobRoot,
                extraction.OriginalApksDirectory,
                extraction.InputDirectory,
                reportPath,
                jsonPath,
                scan.Engine,
                scan.ScriptingBackend,
                extraction.Splits,
                scan.KeyFiles,
                scan.ExtensionCounts,
                extraction.ExtractedBytes,
                extraction.ExtractedFiles,
                BuildEngineInventory(extraction.InputDirectory, scan.RelativeFiles, extraction.JobRoot));

            progress?.Report(new WorkflowProgress(WorkflowStage.AnalyzingFiles, "生成分析报告", 90));
            await AnalysisPipeline.WriteAnalysisArtifactsAsync(result, cancellationToken);
            manifest = manifest with { SchemaVersion = 2, Engine = result.Engine };
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.ReadyForAssetRipper,
                completedStage: WorkflowStage.AnalyzingFiles, inputDirectory: extraction.InputDirectory,
                cancellationToken: cancellationToken);
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.Completed,
                WorkflowTaskStatus.Completed, WorkflowStage.Completed, extraction.InputDirectory,
                cancellationToken: cancellationToken);
            progress?.Report(new WorkflowProgress(WorkflowStage.Completed,
                $"分析完成：{result.Engine} / {result.ScriptingBackend}", 100));
            return result;
        }
        catch (OperationCanceledException)
        {
            await TaskManifestStore.TryRecordTerminalStateAsync(manifest, true, null);
            throw;
        }
        catch (Exception ex)
        {
            await TaskManifestStore.TryRecordTerminalStateAsync(manifest, false, ex);
            throw;
        }
    }

    public async Task<DirectoryAnalysis> AnalyzeExistingDirectoryAsync(
        string selectedPath,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveInputDirectoryAsync(selectedPath, cancellationToken);
        progress?.Report(new WorkflowProgress(WorkflowStage.AnalyzingFiles, "只读扫描现有目录", 0));
        var scan = await ScanCoreAsync(resolved.InputDirectory, progress, cancellationToken);
        progress?.Report(new WorkflowProgress(WorkflowStage.Completed,
            $"目录识别完成：{scan.Engine} / {scan.ScriptingBackend}", 100));
        return new DirectoryAnalysis(
            resolved.SelectedPath,
            resolved.InputDirectory,
            resolved.TaskRoot,
            resolved.AnalysisJsonPath,
            resolved.PackageName,
            resolved.Source,
            scan.Engine,
            scan.ScriptingBackend,
            scan.KeyFiles,
            scan.ExtensionCounts,
            scan.TotalBytes,
            scan.FileCount,
            resolved.IsExistingTask,
            AnalysisPipeline.BuildRecommendation(scan.Engine),
            BuildEngineInventory(resolved.InputDirectory, scan.RelativeFiles, resolved.TaskRoot));
    }

    public async Task<ResolvedAnalysisDirectory> ResolveInputDirectoryAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) throw new ArgumentException("请选择已解压目录或旧任务目录。", nameof(selectedPath));
        var selected = Path.GetFullPath(selectedPath.Trim());
        string? explicitlySelectedMetadata = null;
        if (File.Exists(selected))
        {
            var name = Path.GetFileName(selected);
            if (!name.Equals("analysis.json", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("task.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择目录、analysis.json 或 task.json。");
            explicitlySelectedMetadata = selected;
            selected = Path.GetDirectoryName(selected)!;
        }
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException($"找不到目录：{selected}");

        cancellationToken.ThrowIfCancellationRequested();
        var isInputDirectory = Path.GetFileName(selected).Equals("AssetRipper_Input", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(selected).Equals("Extracted", StringComparison.OrdinalIgnoreCase);
        var taskRoot = isInputDirectory ? Directory.GetParent(selected)?.FullName : selected;
        var inputDirectory = isInputDirectory ? selected : Path.Combine(selected, "Extracted");
        if (!Directory.Exists(inputDirectory) && !isInputDirectory)
            inputDirectory = Path.Combine(selected, "AssetRipper_Input");
        var taskJson = taskRoot == null ? null : Path.Combine(taskRoot, "task.json");
        var analysisJson = taskRoot == null ? null : Path.Combine(taskRoot, "analysis.json");
        if (explicitlySelectedMetadata != null)
        {
            if (Path.GetFileName(explicitlySelectedMetadata).Equals("analysis.json", StringComparison.OrdinalIgnoreCase))
                analysisJson = explicitlySelectedMetadata;
            else
                taskJson = explicitlySelectedMetadata;
        }

        var taskMetadata = File.Exists(taskJson) ? await ReadMetadataAsync(taskJson!, cancellationToken) : default;
        var analysisMetadata = File.Exists(analysisJson) ? await ReadMetadataAsync(analysisJson!, cancellationToken) : default;
        if (!Directory.Exists(inputDirectory))
        {
            var configuredInput = taskMetadata.InputDirectory ?? analysisMetadata.InputDirectory;
            if (!string.IsNullOrWhiteSpace(configuredInput))
            {
                var candidate = Path.IsPathRooted(configuredInput)
                    ? Path.GetFullPath(configuredInput)
                    : Path.GetFullPath(Path.Combine(taskRoot ?? selected, configuredInput));
                if (Directory.Exists(candidate)) inputDirectory = candidate;
            }
        }
        if (!Directory.Exists(inputDirectory)) inputDirectory = selected;

        var hasTaskMetadata = File.Exists(taskJson) || File.Exists(analysisJson);
        var resolvedTaskRoot = hasTaskMetadata || isInputDirectory ? taskRoot : null;
        return new ResolvedAnalysisDirectory(
            Path.GetFullPath(selectedPath.Trim()),
            Path.GetFullPath(inputDirectory),
            resolvedTaskRoot,
            File.Exists(analysisJson) ? analysisJson : null,
            taskMetadata.PackageName ?? analysisMetadata.PackageName,
            taskMetadata.Source ?? analysisMetadata.Source,
            hasTaskMetadata || isInputDirectory);
    }

    private static async Task<ScanResult> ScanCoreAsync(
        string inputDirectory,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        var relativeFiles = new List<string>();
        long totalBytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(inputDirectory, "*", ScanOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            relativeFiles.Add(Path.GetRelativePath(inputDirectory, file).Replace('\\', '/'));
            var length = new FileInfo(file).Length;
            totalBytes = totalBytes > long.MaxValue - length ? long.MaxValue : totalBytes + length;
            count++;
            if (count % 250 == 0)
            {
                progress?.Report(new WorkflowProgress(WorkflowStage.AnalyzingFiles, $"已扫描 {count:N0} 个文件", null, count, 0));
                await Task.Yield();
            }
        }
        var engine = AnalysisPipeline.DetectEngine(relativeFiles);
        var backend = AnalysisPipeline.DetectScriptingBackend(relativeFiles);
        var keyFiles = AnalysisPipeline.FindKeyFiles(relativeFiles);
        var extensions = AnalysisPipeline.CountExtensions(relativeFiles);
        return new ScanResult(engine, backend, keyFiles, extensions, totalBytes, count, relativeFiles);
    }

    private static EngineAssetInventory BuildEngineInventory(string root, IReadOnlyList<string> relativeFiles, string? taskRoot)
    {
        string[] Select(params string[] extensions) => relativeFiles
            .Where(path => extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var godot = Select(".pck").ToList();
        foreach (var marker in relativeFiles.Where(path => path.EndsWith("project.godot", StringComparison.OrdinalIgnoreCase)
                     || path.EndsWith("project.binary", StringComparison.OrdinalIgnoreCase)))
        {
            var directory = Path.GetDirectoryName(Path.Combine(root, marker.Replace('/', Path.DirectorySeparatorChar)));
            if (directory != null && !godot.Contains(directory, StringComparer.OrdinalIgnoreCase)) godot.Add(directory);
        }
        var godotApks = taskRoot == null ? [] : Directory.Exists(Path.Combine(taskRoot, "Original_APKs"))
            ? Directory.EnumerateFiles(Path.Combine(taskRoot, "Original_APKs"), "*.apk", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        return new EngineAssetInventory(godot, Select(".pak"), Select(".utoc"), Select(".ucas"))
        {
            GodotApks = godotApks
        };
    }

    private static async Task<Metadata> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            return new Metadata(
                ReadString(root, "InputDirectory"),
                ReadString(root, "PackageName"),
                ReadString(root, "Source"));
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }

    private readonly record struct ScanResult(
        GameEngine Engine,
        string ScriptingBackend,
        IReadOnlyList<string> KeyFiles,
        IReadOnlyDictionary<string, int> ExtensionCounts,
        long TotalBytes,
        int FileCount,
        IReadOnlyList<string> RelativeFiles);

    private readonly record struct Metadata(string? InputDirectory, string? PackageName, string? Source);
}
