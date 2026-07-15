namespace GooglePlayApkDownloader;

internal sealed class WorkflowCoordinator
{
    private readonly ExtractionService _extractionService;
    private readonly AnalysisService _analysisService;

    public WorkflowCoordinator()
        : this(new ExtractionService(), new AnalysisService())
    {
    }

    internal WorkflowCoordinator(ExtractionService extractionService, AnalysisService analysisService)
    {
        _extractionService = extractionService;
        _analysisService = analysisService;
    }

    public async Task<AnalysisResult> ExtractAndAnalyzeExternalAsync(
        string packageName,
        string source,
        string destinationRoot,
        IEnumerable<string> selectedPaths,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = await _extractionService.PrepareExternalApksAsync(
            packageName, source, destinationRoot, selectedPaths, progress, cancellationToken);
        var extraction = await _extractionService.ExtractAsync(prepared, progress, cancellationToken);
        return await _analysisService.AnalyzeExtractionAsync(extraction, progress, cancellationToken);
    }

    public async Task<AnalysisResult> ContinueDownloadedTaskAsync(
        string packageName,
        string source,
        string jobRoot,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = await _extractionService.PrepareDownloadedTaskAsync(
            packageName, source, jobRoot, true, progress, cancellationToken);
        var extraction = await _extractionService.ExtractAsync(prepared, progress, cancellationToken);
        return await _analysisService.AnalyzeExtractionAsync(extraction, progress, cancellationToken);
    }

    internal async Task<AnalysisResult> ContinueDownloadedTaskInPlaceAsync(
        string packageName,
        string source,
        string jobRoot,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = await _extractionService.PrepareDownloadedTaskAsync(
            packageName, source, jobRoot, false, progress, cancellationToken);
        var extraction = await _extractionService.ExtractAsync(prepared, progress, cancellationToken);
        return await _analysisService.AnalyzeExtractionAsync(extraction, progress, cancellationToken);
    }

    public Task<DirectoryAnalysis> ScanExistingDirectoryAsync(
        string selectedPath,
        IProgress<WorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _analysisService.AnalyzeExistingDirectoryAsync(selectedPath, progress, cancellationToken);
}
