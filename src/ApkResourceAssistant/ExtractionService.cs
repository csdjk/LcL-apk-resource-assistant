using System.IO.Compression;

namespace GooglePlayApkDownloader;

internal sealed class ExtractionService
{
    private const long CopySafetyReserve = 64L * 1024 * 1024;
    private const long ExtractionSafetyReserve = 256L * 1024 * 1024;
    private static readonly EnumerationOptions ApkEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };
    private static readonly object JobDirectoryLock = new();

    public async Task<PreparedApkTask> PrepareExternalApksAsync(
        string packageName,
        string source,
        string destinationRoot,
        IEnumerable<string> selectedPaths,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedPaths?.ToArray() ?? throw new ArgumentNullException(nameof(selectedPaths));
        var apkFiles = await Task.Run(() => ResolveApkInputs(selected, cancellationToken), cancellationToken);
        if (apkFiles.Count == 0) throw new InvalidOperationException("所选文件或目录中没有找到 APK 文件。");

        packageName = NormalizePackageName(packageName);
        source = string.IsNullOrWhiteSpace(source) ? "本地 APK" : source.Trim();
        var jobRoot = CreateUniqueJobDirectory(destinationRoot, packageName);
        var originalDir = Path.Combine(jobRoot, "Original_APKs");
        Directory.CreateDirectory(originalDir);
        var manifest = new TaskManifest
        {
            PackageName = packageName,
            Source = source,
            Mode = WorkflowMode.ExtractAnalyze,
            Status = WorkflowTaskStatus.Running,
            CurrentStage = WorkflowStage.Created,
            CompletedStages = [WorkflowStage.Created],
            JobRoot = jobRoot,
            OriginalApksDirectory = originalDir,
            SourceFiles = [],
            ImportedFromFiles = apkFiles,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await TaskManifestStore.SaveAsync(manifest, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.CopyingApks,
                cancellationToken: cancellationToken);
            progress?.Report(new WorkflowProgress(WorkflowStage.CopyingApks, "正在准备本地 APK", 0, 0, apkFiles.Count));

            var copyBytes = SumFileSizes(apkFiles);
            EnsureFreeSpace(jobRoot, checked(copyBytes + CopySafetyReserve));
            var copied = new List<string>(apkFiles.Count);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < apkFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = apkFiles[index];
                var fileName = MakeUniqueFileName(Path.GetFileName(sourcePath), usedNames);
                var destination = Path.Combine(originalDir, fileName);
                await CopyFileAsync(sourcePath, destination, cancellationToken);
                copied.Add(destination);
                var percent = (int)Math.Round((index + 1) * 100d / apkFiles.Count);
                progress?.Report(new WorkflowProgress(WorkflowStage.CopyingApks,
                    $"复制 {Path.GetFileName(sourcePath)}", percent, index + 1, apkFiles.Count));
            }

            var completedAfterCopy = manifest.CompletedStages.ToList();
            if (!completedAfterCopy.Contains(WorkflowStage.CopyingApks)) completedAfterCopy.Add(WorkflowStage.CopyingApks);
            manifest = manifest with { SourceFiles = copied, ImportedFromFiles = apkFiles, CompletedStages = completedAfterCopy };
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.Downloaded,
                completedStage: WorkflowStage.Downloaded, cancellationToken: cancellationToken);
            return new PreparedApkTask(packageName, source, jobRoot, originalDir, copied, false, manifest);
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

    public async Task<PreparedApkTask> PrepareDownloadedTaskAsync(
        string packageName,
        string source,
        string jobRoot,
        bool createNewIfAlreadyProcessed = true,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        jobRoot = NormalizeTaskRoot(jobRoot);
        var originalDir = Path.Combine(jobRoot, "Original_APKs");
        var apkFiles = Directory.Exists(originalDir)
            ? Directory.EnumerateFiles(originalDir, "*.apk", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (apkFiles.Count == 0) throw new InvalidOperationException("任务的 Original_APKs 中没有找到 APK 文件。");

        var existingManifest = await TaskManifestStore.TryLoadAsync(jobRoot, cancellationToken);
        var hasPriorOutput = File.Exists(Path.Combine(jobRoot, "analysis.json"))
            || DirectoryHasContent(Path.Combine(jobRoot, "AssetRipper_Input"))
            || existingManifest?.CompletedStages.Contains(WorkflowStage.AnalyzingFiles) == true
            || existingManifest?.CurrentStage is WorkflowStage.ReadyForAssetRipper or WorkflowStage.Completed;

        if (hasPriorOutput)
        {
            if (!createNewIfAlreadyProcessed)
                throw new InvalidOperationException("该任务已经生成分析结果。请创建新任务，避免覆盖原结果。");
            var destinationRoot = InferDestinationRoot(jobRoot, packageName);
            return await PrepareExternalApksAsync(packageName, source, destinationRoot, apkFiles, progress, cancellationToken);
        }

        packageName = NormalizePackageName(packageName);
        source = string.IsNullOrWhiteSpace(source) ? "已下载 APK" : source.Trim();
        var manifest = existingManifest ?? new TaskManifest
        {
            PackageName = packageName,
            Source = source,
            Mode = WorkflowMode.ExtractAnalyze,
            Status = WorkflowTaskStatus.Running,
            CurrentStage = WorkflowStage.Downloaded,
            CompletedStages = [WorkflowStage.Downloaded],
            JobRoot = jobRoot,
            OriginalApksDirectory = originalDir,
            SourceFiles = apkFiles,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        manifest = manifest with
        {
            PackageName = packageName,
            Source = source,
            Mode = WorkflowMode.ExtractAnalyze,
            Status = WorkflowTaskStatus.Running,
            JobRoot = jobRoot,
            OriginalApksDirectory = originalDir,
            SourceFiles = apkFiles,
            LastError = null
        };
        manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.Downloaded,
            completedStage: WorkflowStage.Downloaded, cancellationToken: cancellationToken);
        progress?.Report(new WorkflowProgress(WorkflowStage.Downloaded, "已复用下载任务中的 Original_APKs", 100, apkFiles.Count, apkFiles.Count));
        return new PreparedApkTask(packageName, source, jobRoot, originalDir, apkFiles, true, manifest);
    }

    public async Task<ExtractionResult> ExtractAsync(
        PreparedApkTask prepared,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = prepared.Manifest;
        try
        {
            var inputDir = Path.Combine(prepared.JobRoot, "AssetRipper_Input");
            var keyDir = Path.Combine(prepared.JobRoot, "KeyFiles");
            if (DirectoryHasContent(inputDir))
                throw new InvalidOperationException("目标任务已经包含解压结果；为保护旧结果，请创建新的时间戳任务。");

            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.ValidatingApks,
                cancellationToken: cancellationToken);
            progress?.Report(new WorkflowProgress(WorkflowStage.ValidatingApks, "校验 APK 与磁盘空间", 0));
            var expandedBytes = ValidateAndMeasureApks(prepared.ApkFiles, cancellationToken);
            EnsureFreeSpace(prepared.JobRoot, checked(expandedBytes + ExtractionSafetyReserve));
            progress?.Report(new WorkflowProgress(WorkflowStage.ValidatingApks,
                $"校验完成，预计解压 {AnalysisPipeline.FormatBytes(expandedBytes)}", 100));

            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(keyDir);
            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.ExtractingApks,
                completedStage: WorkflowStage.ValidatingApks, inputDirectory: inputDir, cancellationToken: cancellationToken);

            var splitInfos = new List<SplitInfo>();
            long extractedBytes = 0;
            var extractedFiles = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < prepared.ApkFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var apk = prepared.ApkFiles[index];
                var splitName = AnalysisPipeline.MakeUniqueName(
                    AnalysisPipeline.NormalizeSplitName(prepared.PackageName, Path.GetFileName(apk)), usedNames);
                var splitDir = Path.Combine(inputDir, splitName);
                var startPercent = (int)Math.Round(index * 100d / prepared.ApkFiles.Count);
                progress?.Report(new WorkflowProgress(WorkflowStage.ExtractingApks,
                    $"解压 {Path.GetFileName(apk)}", startPercent, index, prepared.ApkFiles.Count));
                var bytesBeforeApk = extractedBytes;
                var lastReportedPercent = startPercent;
                var stats = await AnalysisPipeline.ExtractApkSafelyAsync(apk, splitDir, (apkBytes, _) =>
                {
                    var percent = expandedBytes == 0
                        ? startPercent
                        : (int)Math.Clamp(Math.Round((bytesBeforeApk + apkBytes) * 100d / expandedBytes), 0, 99);
                    if (percent <= lastReportedPercent) return;
                    lastReportedPercent = percent;
                    progress?.Report(new WorkflowProgress(WorkflowStage.ExtractingApks,
                        $"解压 {Path.GetFileName(apk)}", percent, index, prepared.ApkFiles.Count));
                }, cancellationToken);
                extractedBytes = checked(extractedBytes + stats.Bytes);
                extractedFiles = checked(extractedFiles + stats.Files);
                splitInfos.Add(new SplitInfo(Path.GetFileName(apk),
                    AnalysisPipeline.ClassifySplit(prepared.PackageName, Path.GetFileName(apk)), new FileInfo(apk).Length));
                var percent = (int)Math.Round((index + 1) * 100d / prepared.ApkFiles.Count);
                progress?.Report(new WorkflowProgress(WorkflowStage.ExtractingApks,
                    $"已解压 {Path.GetFileName(apk)}", percent, index + 1, prepared.ApkFiles.Count));
            }

            manifest = await TaskManifestStore.TransitionAsync(manifest, WorkflowStage.ExtractingApks,
                completedStage: WorkflowStage.ExtractingApks, inputDirectory: inputDir, cancellationToken: cancellationToken);
            return new ExtractionResult(prepared.PackageName, prepared.Source, prepared.JobRoot,
                prepared.OriginalApksDirectory, inputDir, keyDir, splitInfos, extractedBytes, extractedFiles, manifest);
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

    internal static IReadOnlyList<string> ResolveApkInputs(
        IEnumerable<string> selectedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = Path.GetFullPath(raw.Trim());
            if (File.Exists(path))
            {
                if (Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                continue;
            }
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"找不到文件或目录：{path}");

            var originalApks = Path.Combine(path, "Original_APKs");
            var searchRoot = Directory.Exists(originalApks) ? originalApks : path;
            foreach (var apk in Directory.EnumerateFiles(searchRoot, "*.apk", ApkEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(Path.GetFullPath(apk));
            }
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string CreateUniqueJobDirectory(string destinationRoot, string packageName)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot)) throw new ArgumentException("请选择任务保存目录。", nameof(destinationRoot));
        var root = Path.GetFullPath(destinationRoot);
        var packageRoot = Path.Combine(root, AnalysisPipeline.SanitizeFileName(NormalizePackageName(packageName)));
        lock (JobDirectoryLock)
        {
            Directory.CreateDirectory(packageRoot);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var candidate = Path.Combine(packageRoot, timestamp);
            var suffix = 2;
            while (Directory.Exists(candidate)) candidate = Path.Combine(packageRoot, $"{timestamp}-{suffix++}");
            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private static string NormalizeTaskRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("任务目录为空。", nameof(path));
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"找不到任务目录：{full}");
        if (Path.GetFileName(full).Equals("Original_APKs", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(full)?.FullName ?? throw new InvalidOperationException("Original_APKs 缺少父任务目录。");
        return full;
    }

    private static string NormalizePackageName(string packageName)
        => string.IsNullOrWhiteSpace(packageName) ? "local-apk" : packageName.Trim();

    private static string InferDestinationRoot(string jobRoot, string packageName)
    {
        var parent = Directory.GetParent(jobRoot);
        if (parent == null) return jobRoot;
        var safePackage = AnalysisPipeline.SanitizeFileName(NormalizePackageName(packageName));
        if (parent.Name.Equals(safePackage, StringComparison.OrdinalIgnoreCase) && parent.Parent != null)
            return parent.Parent.FullName;
        return parent.FullName;
    }

    private static bool DirectoryHasContent(string directory)
        => Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();

    private static long ValidateAndMeasureApks(IReadOnlyList<string> apks, CancellationToken cancellationToken)
    {
        if (apks.Count == 0) throw new InvalidOperationException("没有可解压的 APK 文件。");
        long total = 0;
        foreach (var apk in apks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ZipFile.OpenRead(apk);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total = checked(total + entry.Length);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"APK 损坏或不是有效 ZIP：{Path.GetFileName(apk)}", ex);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"APK 声明的解压大小异常：{Path.GetFileName(apk)}", ex);
            }
        }
        return total;
    }

    private static long SumFileSizes(IEnumerable<string> files)
    {
        long total = 0;
        foreach (var file in files) total = checked(total + new FileInfo(file).Length);
        return total;
    }

    private static void EnsureFreeSpace(string destination, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("无法确定目标磁盘。");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"磁盘空间不足。预计至少需要 {AnalysisPipeline.FormatBytes(requiredBytes)}，当前可用 {AnalysisPipeline.FormatBytes(drive.AvailableFreeSpace)}。");
    }

    private static string MakeUniqueFileName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 2;
        string candidate;
        do { candidate = $"{stem}-{index++}{extension}"; } while (!used.Add(candidate));
        return candidate;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }
}
