namespace GooglePlayApkDownloader;

internal enum WorkflowMode
{
    Download,
    ExtractAnalyze,
    OpenInAssetRipper
}

internal enum WorkflowTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

internal enum WorkflowStage
{
    Created,
    Downloading,
    Downloaded,
    CopyingApks,
    ValidatingApks,
    ExtractingApks,
    AnalyzingFiles,
    ReadyForAssetRipper,
    Completed
}

internal sealed record WorkflowProgress(
    WorkflowStage Stage,
    string Message,
    int? Percent = null,
    int Current = 0,
    int Total = 0);

internal sealed record TaskManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string PackageName { get; init; } = "local-apk";
    public string Source { get; init; } = "本地 APK";
    public WorkflowMode Mode { get; init; } = WorkflowMode.ExtractAnalyze;
    public WorkflowTaskStatus Status { get; init; } = WorkflowTaskStatus.Pending;
    public WorkflowStage CurrentStage { get; init; } = WorkflowStage.Created;
    public IReadOnlyList<WorkflowStage> CompletedStages { get; init; } = [];
    public string JobRoot { get; init; } = "";
    public string OriginalApksDirectory { get; init; } = "";
    public string? InputDirectory { get; init; }
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    public IReadOnlyList<string> ImportedFromFiles { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? LastError { get; init; }
}

internal sealed record PreparedApkTask(
    string PackageName,
    string Source,
    string JobRoot,
    string OriginalApksDirectory,
    IReadOnlyList<string> ApkFiles,
    bool ReusedDownloadedTask,
    TaskManifest Manifest);

internal sealed record ExtractionResult(
    string PackageName,
    string Source,
    string JobRoot,
    string OriginalApksDirectory,
    string InputDirectory,
    string KeyFilesDirectory,
    IReadOnlyList<SplitInfo> Splits,
    long ExtractedBytes,
    int ExtractedFiles,
    TaskManifest Manifest);

internal sealed record DirectoryAnalysis(
    string SelectedPath,
    string InputDirectory,
    string? TaskRoot,
    string? AnalysisJsonPath,
    string? PackageName,
    string? Source,
    GameEngine Engine,
    string ScriptingBackend,
    IReadOnlyList<string> KeyFiles,
    IReadOnlyDictionary<string, int> ExtensionCounts,
    long TotalBytes,
    int FileCount,
    bool IsExistingTask,
    string Recommendation);

internal sealed record ResolvedAnalysisDirectory(
    string SelectedPath,
    string InputDirectory,
    string? TaskRoot,
    string? AnalysisJsonPath,
    string? PackageName,
    string? Source,
    bool IsExistingTask);
