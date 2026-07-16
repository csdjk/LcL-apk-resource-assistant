namespace GooglePlayApkDownloader;

internal enum WorkflowMode
{
    Download,
    ExtractAnalyze,
    RecoverResources,
    [Obsolete("Use RecoverResources. Retained for v3 task.json compatibility.")]
    OpenInAssetRipper = RecoverResources
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
    Completed,
    PreparingEngineInput,
    DownloadingTools,
    RecoveringGodot,
    InspectingUnreal,
    ExtractingUnreal,
    ReadyForEngineTool
}

internal sealed record WorkflowProgress(
    WorkflowStage Stage,
    string Message,
    int? Percent = null,
    int Current = 0,
    int Total = 0);

internal sealed record TaskManifest
{
    public int SchemaVersion { get; init; } = 2;
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
    public GameEngine? Engine { get; init; }
    public string? RecoveryDirectory { get; init; }
    public string? RecoveryTool { get; init; }
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
    string Recommendation,
    EngineAssetInventory? EngineAssets = null);

internal sealed record EngineAssetInventory(
    IReadOnlyList<string> GodotPackages,
    IReadOnlyList<string> UnrealPakFiles,
    IReadOnlyList<string> UnrealUtocFiles,
    IReadOnlyList<string> UnrealUcasFiles)
{
    public IReadOnlyList<string> GodotApks { get; init; } = [];
    public static EngineAssetInventory Empty { get; } = new([], [], [], []);
}

internal sealed record EngineRecoveryRequest(
    string SelectedPath,
    string? TemporaryKey = null,
    bool ExtractUnrealContainers = true);

internal enum EngineRecoveryOutcome
{
    Completed,
    ToolLaunched,
    ManualFallback
}

internal sealed record EngineRecoveryResult(
    GameEngine Engine,
    EngineRecoveryOutcome Outcome,
    string InputDirectory,
    string OutputDirectory,
    string ToolName,
    string ToolVersion,
    string Message,
    string? LogPath,
    int ProcessedContainers = 0,
    int FailedContainers = 0,
    bool ToolLaunched = false);

internal sealed record ResolvedAnalysisDirectory(
    string SelectedPath,
    string InputDirectory,
    string? TaskRoot,
    string? AnalysisJsonPath,
    string? PackageName,
    string? Source,
    bool IsExistingTask);
