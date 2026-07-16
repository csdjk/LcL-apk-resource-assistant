using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GooglePlayApkDownloader;

internal sealed class MainForm : Form
{
    private enum NextAction
    {
        None,
        ExtractDownloadedTask,
        OpenWithEngineTool
    }

    private readonly AppSettings _settings;
    private readonly DownloadService _downloadService;
    private readonly WorkflowCoordinator _workflow = new();
    private readonly AssetRipperLauncher _assetRipperLauncher = new();
    private readonly EngineRecoveryService _engineRecoveryService = new();

    private readonly TableLayoutPanel _root = new();
    private readonly Panel _workflowHost = new();
    private readonly ModeCard[] _modeCards =
    [
        new(0, "下载 APK", "从 APKPure 或 Google Play 获取 APK / Split APK"),
        new(1, "解压与分析", "选择本地 APK、文件夹或以前的下载任务"),
        new(2, "引擎恢复", "Unity / Godot / Unreal 自动选择专用工具")
    ];

    private readonly ComboBox _downloadSource = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _downloadPackage = UiTheme.TextInput("https://play.google.com/store/apps/details?id=com.oakever.meowdoku&hl=zh");
    private readonly TextBox _downloadOutput = UiTheme.TextInput();
    private readonly CheckBox _downloadSplits = new() { Text = "下载 Split APK（游戏资源分析推荐）", AutoSize = true };
    private readonly Label _credentialStatus = UiTheme.Caption(string.Empty);
    private readonly Button _downloadBrowse = UiTheme.SecondaryButton("浏览…");
    private readonly Button _downloadRun = UiTheme.PrimaryButton("开始下载");

    private readonly TextBox _extractSelection = UiTheme.TextInput(readOnly: true);
    private readonly TextBox _extractOutput = UiTheme.TextInput();
    private readonly Button _extractSelectFiles = UiTheme.SecondaryButton("选择 APK…");
    private readonly Button _extractSelectFolder = UiTheme.SecondaryButton("选择文件夹…");
    private readonly Button _extractBrowseOutput = UiTheme.SecondaryButton("浏览…");
    private readonly Button _extractRun = UiTheme.PrimaryButton("开始解压与分析");

    private readonly TextBox _assetSelection = UiTheme.TextInput(readOnly: true);
    private readonly Label _assetConfiguration = UiTheme.Caption(string.Empty);
    private readonly Button _assetSelectDirectory = UiTheme.SecondaryButton("选择目录…");
    private readonly Button _assetLaunchOnly = UiTheme.SecondaryButton("仅启动 AssetRipper");
    private readonly Button _assetRun = UiTheme.PrimaryButton("识别并开始恢复");
    private readonly TextBox _engineKey = UiTheme.TextInput();
    private readonly CheckBox _unrealExtract = new() { Text = "UE：检查并解包 PAK / IoStore（推荐）", AutoSize = true, Checked = true };

    private readonly Label _resultTitle = UiTheme.Heading("等待开始", 12F);
    private readonly Label _resultBadge = new() { AutoSize = true, Padding = new Padding(9, 4, 9, 4) };
    private readonly RichTextBox _resultDetails = new()
    {
        BorderStyle = BorderStyle.None,
        ReadOnly = true,
        BackColor = Color.White,
        ForeColor = UiTheme.Text,
        DetectUrls = false,
        TabStop = false,
        ScrollBars = RichTextBoxScrollBars.Vertical
    };
    private readonly Button _resultOpen = UiTheme.SecondaryButton("打开目录");
    private readonly Button _resultCopy = UiTheme.SecondaryButton("复制路径");
    private readonly Button _resultNext = UiTheme.PrimaryButton("继续下一步");

    private readonly Label _stage = UiTheme.Caption("当前阶段：下载 APK · 就绪");
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100 };
    private readonly Button _cancel = UiTheme.SecondaryButton("取消");
    private readonly Button _logToggle = UiTheme.LinkButton("展开运行日志 ︾");
    private readonly Button _settingsButton = UiTheme.SecondaryButton("⚙  设置", 38);
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9F)
    };

    private readonly List<string> _extractPaths = [];
    private readonly List<string> _startupMessages = [];
    private WorkflowMode _mode;
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    private bool _logExpanded;
    private Rectangle? _logCollapsedBounds;
    private bool _closeWhenIdle;
    private bool _reuseDownloadedTask;
    private DownloadResult? _downloadResult;
    private string? _assetPath;
    private GameEngine _assetEngine = GameEngine.Unknown;
    private string? _openPath;
    private string? _copyPath;
    private NextAction _nextAction;

    public MainForm()
    {
        Text = "APK Resource Assistant";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 800);
        MinimumSize = new Size(900, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiTheme.Window;
        Padding = new Padding(24, 18, 24, 18);
        AllowDrop = true;

        _settings = SettingsStore.Load(message => _startupMessages.Add(message));
        _downloadService = new DownloadService(log: AppendLog);
        InitializeValues();
        BuildLayout();
        WireEvents();
        SelectMode((WorkflowMode)Math.Clamp(_settings.LastMode, 0, 2), false);
        UpdateSettingsSummary();
        ShowModeHint();
        foreach (var message in _startupMessages) AppendLog(message);

        Shown += (_, _) => AppendLog("APK Resource Assistant v4.0.0 已就绪。可从任意阶段开始。");
        FormClosing += MainFormClosing;
        FormClosed += (_, _) =>
        {
            _operationCts?.Dispose();
            _downloadService.Dispose();
            _assetRipperLauncher.Dispose();
            _engineRecoveryService.Dispose();
        };
    }

    private void InitializeValues()
    {
        _downloadSource.Items.AddRange(["APKPure（免登录）", "Google Play（一键匿名）", "Google Play（个人账号）"]);
        _downloadSource.SelectedIndex = Math.Clamp(_settings.Source, 0, 2);
        var defaultOutput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "APK");
        var output = string.IsNullOrWhiteSpace(_settings.OutputDir) ? defaultOutput : _settings.OutputDir;
        _downloadOutput.Text = output;
        _extractOutput.Text = output;
        _downloadSplits.Checked = _settings.Split;
        _extractSelection.Text = "尚未选择。支持 APK 文件、APK 文件夹、Original_APKs 或旧任务目录。";
        _assetSelection.Text = "尚未选择。支持已有解压目录、旧任务目录或 AssetRipper_Input。";
        _engineKey.UseSystemPasswordChar = true;
        _cancel.Enabled = false;
        _resultOpen.Enabled = false;
        _resultCopy.Enabled = false;
        SetNextEnabled(false);
    }

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.RowCount = 6;
        _root.BackColor = UiTheme.Window;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 272));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        Controls.Add(_root);

        _root.Controls.Add(BuildHeader(), 0, 0);
        _root.Controls.Add(BuildModeCards(), 0, 1);

        _workflowHost.Dock = DockStyle.Fill;
        _workflowHost.BackColor = UiTheme.Window;
        _workflowHost.Margin = new Padding(0, 8, 0, 8);
        _workflowHost.Controls.Add(BuildDownloadPanel());
        _workflowHost.Controls.Add(BuildExtractPanel());
        _workflowHost.Controls.Add(BuildAssetPanel());
        _root.Controls.Add(_workflowHost, 0, 2);

        _root.Controls.Add(BuildResultCard(), 0, 3);
        _root.Controls.Add(BuildStatusBar(), 0, 4);
        _root.Controls.Add(BuildLogCard(), 0, 5);
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = UiTheme.Window };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        var text = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = UiTheme.Window,
            Margin = new Padding(0)
        };
        text.Controls.Add(UiTheme.Heading("APK Resource Assistant", 17F));
        text.Controls.Add(UiTheme.Caption("下载、解压、分析各自独立；完成后由你决定是否进入下一步。"));
        panel.Controls.Add(text, 0, 0);
        _settingsButton.Dock = DockStyle.Top;
        _settingsButton.Margin = new Padding(0, 4, 0, 0);
        _settingsButton.Click += (_, _) => ShowSettings();
        panel.Controls.Add(_settingsButton, 1, 0);
        return panel;
    }

    private Control BuildModeCards()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = UiTheme.Window, Margin = new Padding(0) };
        for (var i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        for (var index = 0; index < _modeCards.Length; index++)
        {
            var card = _modeCards[index];
            card.Dock = DockStyle.Fill;
            card.Margin = index switch
            {
                0 => new Padding(0, 4, 7, 4),
                1 => new Padding(7, 4, 7, 4),
                _ => new Padding(7, 4, 0, 4)
            };
            card.AccessibleName = card.TitleText;
            table.Controls.Add(card, index, 0);
        }
        return table;
    }

    private Control BuildDownloadPanel()
    {
        var card = NewWorkflowCard();
        var table = NewWorkflowTable(7, [46, 42, 42, 42, 32, 38]);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        card.Controls.Add(table);
        AddPanelHeader(table, "下载 APK", "只下载并保留 Original_APKs；不会自动解压。", 3);
        AddField(table, 1, "商店链接/包名", _downloadPackage, 2);
        AddField(table, 2, "下载来源", _downloadSource, 2);
        AddField(table, 3, "保存根目录", _downloadOutput, 1);
        _downloadBrowse.Dock = DockStyle.Fill;
        _downloadBrowse.Margin = new Padding(8, 3, 0, 3);
        table.Controls.Add(_downloadBrowse, 2, 3);
        table.Controls.Add(_downloadSplits, 1, 4);
        table.SetColumnSpan(_downloadSplits, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        _credentialStatus.Dock = DockStyle.Fill;
        _credentialStatus.TextAlign = ContentAlignment.MiddleLeft;
        _downloadRun.Dock = DockStyle.Fill;
        _downloadRun.Margin = new Padding(8, 0, 0, 0);
        footer.Controls.Add(_credentialStatus, 0, 0);
        footer.Controls.Add(_downloadRun, 1, 0);
        table.Controls.Add(footer, 1, 5);
        table.SetColumnSpan(footer, 2);
        card.Tag = WorkflowMode.Download;
        return card;
    }

    private Control BuildExtractPanel()
    {
        var card = NewWorkflowCard();
        var table = NewWorkflowTable(6, [48, 44, 44, 40, 42]);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        card.Controls.Add(table);
        AddPanelHeader(table, "解压与分析", "外部 APK 会复制到新任务；下载完成后继续则复用同一任务。", 4);
        AddField(table, 1, "APK / 任务", _extractSelection, 1);
        _extractSelectFiles.Dock = DockStyle.Fill;
        _extractSelectFolder.Dock = DockStyle.Fill;
        _extractSelectFiles.Margin = new Padding(8, 3, 0, 3);
        _extractSelectFolder.Margin = new Padding(8, 3, 0, 3);
        table.Controls.Add(_extractSelectFiles, 2, 1);
        table.Controls.Add(_extractSelectFolder, 3, 1);
        AddField(table, 2, "新任务保存到", _extractOutput, 2);
        _extractBrowseOutput.Dock = DockStyle.Fill;
        _extractBrowseOutput.Margin = new Padding(8, 3, 0, 3);
        table.Controls.Add(_extractBrowseOutput, 3, 2);
        var hint = UiTheme.Caption("可多选 Split APK，也可直接拖放文件/目录。安全解压后识别 Unity、Godot、Unreal。原始文件不会被修改。");
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(hint, 1, 3);
        table.SetColumnSpan(hint, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        _extractRun.Width = 180;
        actions.Controls.Add(_extractRun);
        table.Controls.Add(actions, 1, 4);
        table.SetColumnSpan(actions, 3);
        card.Tag = WorkflowMode.ExtractAnalyze;
        return card;
    }

    private Control BuildAssetPanel()
    {
        var card = NewWorkflowCard();
        var table = NewWorkflowTable(7, [44, 40, 34, 40, 28, 32]);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        card.Controls.Add(table);
        AddPanelHeader(table, "引擎恢复", "Unity 使用 AssetRipper；Godot 使用 GDRETools；Unreal 使用 repak、retoc 与 FModel。", 3);
        AddField(table, 1, "解压目录", _assetSelection, 1);
        _assetSelectDirectory.Dock = DockStyle.Fill;
        _assetSelectDirectory.Margin = new Padding(8, 3, 0, 3);
        table.Controls.Add(_assetSelectDirectory, 2, 1);
        var configLabel = new Label { Text = "程序配置", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text };
        table.Controls.Add(configLabel, 0, 2);
        _assetConfiguration.Dock = DockStyle.Fill;
        _assetConfiguration.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(_assetConfiguration, 1, 2);
        table.SetColumnSpan(_assetConfiguration, 2);
        var keyLabel = new Label { Text = "临时密钥", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text };
        table.Controls.Add(keyLabel, 0, 3);
        _engineKey.Dock = DockStyle.Fill;
        _engineKey.PlaceholderText = "可选：64 位十六进制 AES/PCK 密钥；只用于本次运行";
        table.Controls.Add(_engineKey, 1, 3);
        table.SetColumnSpan(_engineKey, 2);
        table.Controls.Add(_unrealExtract, 1, 4);
        table.SetColumnSpan(_unrealExtract, 2);
        var hint = UiTheme.Caption("工具按固定版本自动下载并校验 SHA-256。Unity 的 AssetRipper 路径仍在设置中配置。");
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(hint, 1, 5);
        table.SetColumnSpan(hint, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        _assetRun.Width = 210;
        _assetLaunchOnly.Width = 112;
        _assetLaunchOnly.Margin = new Padding(0, 0, 10, 0);
        actions.Controls.Add(_assetRun);
        actions.Controls.Add(_assetLaunchOnly);
        table.Controls.Add(actions, 1, 6);
        table.SetColumnSpan(actions, 2);
        card.Tag = WorkflowMode.RecoverResources;
        return card;
    }

    private static CardPanel NewWorkflowCard() => new() { Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 14), Margin = new Padding(0) };

    private static TableLayoutPanel NewWorkflowTable(int rowCount, IReadOnlyList<int> fixedRows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = rowCount, Margin = new Padding(0) };
        foreach (var height in fixedRows) table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddPanelHeader(TableLayoutPanel table, string title, string subtitle, int span)
    {
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        header.Controls.Add(UiTheme.Heading(title, 12F));
        header.Controls.Add(UiTheme.Caption(subtitle));
        table.Controls.Add(header, 0, 0);
        table.SetColumnSpan(header, span);
    }

    private static void AddField(TableLayoutPanel table, int row, string label, Control input, int span)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text }, 0, row);
        input.Dock = DockStyle.Fill;
        table.Controls.Add(input, 1, row);
        if (span > 1) table.SetColumnSpan(input, span);
    }

    private Control BuildResultCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 8), Padding = new Padding(18, 14, 18, 12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.Controls.Add(table);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        _resultTitle.Margin = new Padding(0, 3, 12, 0);
        _resultBadge.Margin = new Padding(0);
        header.Controls.Add(_resultTitle);
        header.Controls.Add(_resultBadge);
        table.Controls.Add(header, 0, 0);
        _resultDetails.Dock = DockStyle.Fill;
        _resultDetails.Margin = new Padding(0, 3, 0, 4);
        table.Controls.Add(_resultDetails, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        _resultNext.Width = 190;
        _resultOpen.Width = 100;
        _resultCopy.Width = 100;
        _resultOpen.Margin = new Padding(8, 0, 0, 0);
        _resultCopy.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(_resultNext);
        actions.Controls.Add(_resultCopy);
        actions.Controls.Add(_resultOpen);
        table.Controls.Add(actions, 0, 2);
        return card;
    }

    private Control BuildStatusBar()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = UiTheme.Window, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 9, 12, 9);
        _cancel.Dock = DockStyle.Fill;
        _cancel.Margin = new Padding(0, 3, 10, 3);
        _logToggle.Dock = DockStyle.Fill;
        panel.Controls.Add(_progress, 0, 0);
        panel.Controls.Add(_cancel, 1, 0);
        panel.Controls.Add(_logToggle, 2, 0);
        _stage.Dock = DockStyle.Fill;
        _stage.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_stage, 0, 1);
        panel.SetColumnSpan(_stage, 3);
        return panel;
    }

    private Control BuildLogCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0), Padding = new Padding(12) };
        _log.Dock = DockStyle.Fill;
        card.Controls.Add(_log);
        return card;
    }

    private void WireEvents()
    {
        foreach (var card in _modeCards)
            card.Click += (_, _) => SelectMode((WorkflowMode)card.ModeIndex);

        _downloadSource.SelectedIndexChanged += (_, _) =>
        {
            _settings.Source = Math.Max(0, _downloadSource.SelectedIndex);
            UpdateSettingsSummary();
            TrySaveSettings();
        };
        _downloadSplits.CheckedChanged += (_, _) =>
        {
            _settings.Split = _downloadSplits.Checked;
            TrySaveSettings();
        };
        _downloadBrowse.Click += (_, _) => BrowseOutput(_downloadOutput);
        _extractBrowseOutput.Click += (_, _) => BrowseOutput(_extractOutput);
        _downloadRun.Click += async (_, _) => await DownloadAsync();
        _extractSelectFiles.Click += (_, _) => SelectApkFiles();
        _extractSelectFolder.Click += (_, _) => SelectExtractFolder();
        _extractRun.Click += async (_, _) => await ExtractAndAnalyzeAsync();
        _assetSelectDirectory.Click += (_, _) => SelectAssetDirectory();
        _assetRun.Click += async (_, _) => await AnalyzeAndOpenAssetRipperAsync();
        _assetLaunchOnly.Click += (_, _) => LaunchAssetRipperOnly();
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _operationCts?.Cancel();
            SetStage("正在取消当前阶段…", null);
        };
        _logToggle.Click += (_, _) => ToggleLog();
        _resultOpen.Click += (_, _) =>
        {
            if (_openPath != null && !TryOpenPath(_openPath))
                SetStage("资源管理器启动失败；可复制路径后手工打开。", 100);
        };
        _resultCopy.Click += (_, _) => CopyCurrentPath();
        _resultNext.Click += (_, _) => ContinueToNextStage();
        DragEnter += MainDragEnter;
        DragDrop += MainDragDrop;
    }

    private void SelectMode(WorkflowMode mode, bool persist = true)
    {
        if (_busy) return;
        _mode = mode;
        for (var i = 0; i < _modeCards.Length; i++) _modeCards[i].Selected = i == (int)mode;
        foreach (Control control in _workflowHost.Controls)
            control.Visible = control.Tag is WorkflowMode controlMode && controlMode == mode;
        if (persist)
        {
            _settings.LastMode = (int)mode;
            TrySaveSettings();
            ShowModeHint();
        }
        SetStage($"当前阶段：{ModeTitle(mode)} · 就绪", 0, false);
    }

    private void ShowModeHint()
    {
        _openPath = null;
        _copyPath = null;
        _nextAction = NextAction.None;
        _resultOpen.Enabled = false;
        _resultCopy.Enabled = false;
        SetNextEnabled(false);
        var (title, details) = _mode switch
        {
            WorkflowMode.Download => ("等待下载", "填写商店链接或包名，选择下载源与保存目录。下载结束只生成 Original_APKs。"),
            WorkflowMode.ExtractAnalyze => ("等待解压与分析", "选择本地 APK、APK 文件夹或以前的下载任务。外部源文件保持原样。"),
            _ => ("等待选择目录", "选择已有解压目录或旧任务目录，识别后自动选择 Unity、Godot 或 Unreal 恢复工具。")
        };
        SetResult(title, "就绪", UiTheme.PrimarySoft, UiTheme.Primary, details);
    }

    private async Task DownloadAsync()
    {
        await RunOperationAsync(async cancellationToken =>
        {
            SaveMainSettings();
            var source = (DownloadSource)Math.Clamp(_downloadSource.SelectedIndex, 0, 2);
            var request = new DownloadRequest
            {
                PackageOrUrl = _downloadPackage.Text,
                OutputRoot = _downloadOutput.Text,
                Source = source,
                DownloadSplitApks = _downloadSplits.Checked,
                GoogleEmail = _settings.Email,
                GoogleToken = _settings.Token
            };
            SetResult("正在下载", "进行中", UiTheme.PrimarySoft, UiTheme.Primary, "正在准备下载任务，请在底部查看当前阶段。");
            SetStage("正在准备下载引擎和任务目录…", null);
            var result = await _downloadService.DownloadAsync(request, cancellationToken);
            _downloadResult = result;
            _reuseDownloadedTask = true;
            _extractPaths.Clear();
            _extractSelection.Text = $"下载任务：{result.JobDirectory}";
            ShowDownloadResult(result);
        });
    }

    private void ShowDownloadResult(DownloadResult result)
    {
        var details = new StringBuilder();
        details.AppendLine($"包名：{result.PackageName}    来源：{result.SourceLabel}    APK：{result.ApkFiles.Count} 个");
        details.AppendLine($"任务目录：{result.JobDirectory}");
        foreach (var apk in result.ApkFiles.Take(6))
            details.AppendLine($"• {Path.GetFileName(apk)}  ({AnalysisPipeline.FormatBytes(new FileInfo(apk).Length)})");
        if (result.ApkFiles.Count > 6) details.AppendLine($"• 其余 {result.ApkFiles.Count - 6} 个 APK…");
        SetResult("下载完成", "已下载", Color.FromArgb(236, 253, 245), UiTheme.Success, details.ToString().TrimEnd());
        ConfigureResultActions(result.JobDirectory, result.OriginalApksDirectory, NextAction.ExtractDownloadedTask, "继续解压与分析");
        SetStage($"下载完成：{result.ApkFiles.Count} 个 APK。等待你选择下一步。", 100);
    }

    private async Task ExtractAndAnalyzeAsync()
    {
        await RunOperationAsync(async cancellationToken =>
        {
            SaveMainSettings();
            SetResult("正在解压与分析", "进行中", UiTheme.PrimarySoft, UiTheme.Primary, "将依次校验、解压、识别引擎并整理关键文件。");
            var workflowProgress = new Progress<WorkflowProgress>(ReportWorkflowProgress);
            AnalysisResult result;
            if (_reuseDownloadedTask && _downloadResult != null)
            {
                result = await _workflow.ContinueDownloadedTaskAsync(
                    _downloadResult.PackageName,
                    _downloadResult.SourceLabel,
                    _downloadResult.JobDirectory,
                    workflowProgress,
                    cancellationToken);
            }
            else
            {
                if (_extractPaths.Count == 0) throw new InvalidOperationException("请先选择一个或多个 APK，或选择含 APK 的文件夹。可直接拖放到窗口。");
                var packageName = await InferPackageNameAsync(_extractPaths, cancellationToken);
                result = await _workflow.ExtractAndAnalyzeExternalAsync(
                    packageName,
                    "本地 APK",
                    _extractOutput.Text,
                    _extractPaths,
                    workflowProgress,
                    cancellationToken);
            }
            _reuseDownloadedTask = false;
            ShowAnalysisResult(result);
        });
    }

    private void ShowAnalysisResult(AnalysisResult result)
    {
        _assetPath = result.InputDirectory;
        _assetEngine = result.Engine;
        _assetSelection.Text = result.InputDirectory;
        var recommendation = AnalysisPipeline.BuildRecommendation(result.Engine);
        var details = new StringBuilder();
        details.AppendLine($"引擎：{EngineName(result.Engine)}    脚本后端：{result.ScriptingBackend}    APK：{result.Splits.Count} 个");
        details.AppendLine($"解压：{result.ExtractedFiles:N0} 个文件 / {AnalysisPipeline.FormatBytes(result.ExtractedBytes)}    关键文件：{result.KeyFiles.Count} 个");
        details.AppendLine($"分析目录：{result.InputDirectory}");
        details.Append(recommendation);
        SetResult("解压与分析完成", EngineName(result.Engine), Color.FromArgb(236, 253, 245), UiTheme.Success, details.ToString());
        var canContinue = true;
        ConfigureResultActions(result.JobRoot, result.InputDirectory,
            canContinue ? NextAction.OpenWithEngineTool : NextAction.None,
            result.Engine switch
            {
                GameEngine.Unity => "继续用 AssetRipper 打开",
                GameEngine.Godot => "继续恢复 Godot 工程",
                GameEngine.Unreal => "继续处理 Unreal 资源",
                _ => "继续识别恢复工具"
            });
        SetStage($"分析完成：{EngineName(result.Engine)} / {result.ScriptingBackend}。", 100);
    }

    private async Task AnalyzeAndOpenAssetRipperAsync()
    {
        await RunOperationAsync(async cancellationToken =>
        {
            if (string.IsNullOrWhiteSpace(_assetPath)) throw new InvalidOperationException("请先选择已有解压目录或以前的任务目录。");
            SetResult("正在识别目录", "只读扫描", UiTheme.PrimarySoft, UiTheme.Primary, "扫描不会复制、写入或修改所选目录。");
            var workflowProgress = new Progress<WorkflowProgress>(ReportWorkflowProgress);
            var analysis = await _workflow.ScanExistingDirectoryAsync(_assetPath, workflowProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _assetPath = analysis.InputDirectory;
            _assetSelection.Text = analysis.InputDirectory;
            _assetEngine = analysis.Engine;

            if (analysis.Engine is GameEngine.Godot or GameEngine.Unreal)
            {
                try
                {
                    var recovery = await _engineRecoveryService.RecoverAsync(
                        new EngineRecoveryRequest(analysis.InputDirectory, _engineKey.Text, _unrealExtract.Checked),
                        workflowProgress, cancellationToken);
                    ShowEngineRecoveryResult(recovery);
                }
                finally { _engineKey.Clear(); }
                return;
            }
            if (analysis.Engine == GameEngine.Unknown)
            {
                var answer = MessageBox.Show(this,
                    "没有识别出 Unity 特征。仍要把该目录交给 AssetRipper 吗？",
                    "未知游戏引擎",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    ShowDirectoryAnalysis(analysis, false);
                    return;
                }
            }
            if (!EnsureAssetRipperConfigured())
            {
                ShowDirectoryAnalysis(analysis, true);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var launchProgress = new Progress<AssetRipperLaunchProgress>(p => SetStage(p.Message, null));
            var launch = await _assetRipperLauncher.LaunchAsync(
                _settings.AssetRipperPath!,
                analysis.InputDirectory,
                launchProgress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ShowAssetRipperResult(analysis, launch);
            if (launch.ShouldCopyInputPath && !TryCopyPath(analysis.InputDirectory))
                AppendLog("兼容回退：剪贴板暂时不可用，输入路径已保留在结果卡片中。");
            if (launch.ShouldOpenInputDirectory && !TryOpenPath(analysis.InputDirectory))
                AppendLog("兼容回退：资源管理器启动失败，请从结果卡片复制输入路径。");
        });
    }

    private void ShowDirectoryAnalysis(DirectoryAnalysis analysis, bool needsConfiguration)
    {
        var details = new StringBuilder();
        details.AppendLine($"引擎：{EngineName(analysis.Engine)}    脚本后端：{analysis.ScriptingBackend}");
        details.AppendLine($"文件：{analysis.FileCount:N0} 个 / {AnalysisPipeline.FormatBytes(analysis.TotalBytes)}    关键文件：{analysis.KeyFiles.Count} 个");
        details.AppendLine($"输入目录：{analysis.InputDirectory}");
        details.AppendLine(analysis.Recommendation);
        if (needsConfiguration) details.Append("请先在右上角“设置”中选择 AssetRipper.exe，再次执行本阶段。");
        SetResult("目录识别完成", EngineName(analysis.Engine),
            analysis.Engine is GameEngine.Godot or GameEngine.Unreal ? Color.FromArgb(255, 247, 237) : UiTheme.PrimarySoft,
            analysis.Engine is GameEngine.Godot or GameEngine.Unreal ? UiTheme.Warning : UiTheme.Primary,
            details.ToString().TrimEnd());
        ConfigureResultActions(analysis.TaskRoot ?? analysis.InputDirectory, analysis.InputDirectory, NextAction.None, string.Empty);
        SetStage($"目录识别完成：{EngineName(analysis.Engine)}。", 100);
    }

    private void ShowAssetRipperResult(DirectoryAnalysis analysis, AssetRipperLaunchResult launch)
    {
        var details = new StringBuilder();
        details.AppendLine($"引擎：{EngineName(analysis.Engine)}    脚本后端：{analysis.ScriptingBackend}");
        details.AppendLine($"输入目录：{analysis.InputDirectory}");
        details.AppendLine(launch.Message);
        if (!string.IsNullOrWhiteSpace(launch.AutomaticLoadError)) details.AppendLine($"自动载入详情：{launch.AutomaticLoadError}");
        if (launch.Port.HasValue) details.Append($"本机端口：{launch.Port}    进程：{launch.ProcessId}");
        SetResult("AssetRipper 已启动", launch.AutoLoaded ? "已自动载入" : "兼容回退",
            launch.AutoLoaded ? Color.FromArgb(236, 253, 245) : Color.FromArgb(255, 247, 237),
            launch.AutoLoaded ? UiTheme.Success : UiTheme.Warning,
            details.ToString().TrimEnd());
        ConfigureResultActions(analysis.TaskRoot ?? analysis.InputDirectory, analysis.InputDirectory, NextAction.None, string.Empty);
        SetStage(launch.Message, 100);
    }

    private void ShowEngineRecoveryResult(EngineRecoveryResult recovery)
    {
        var details = new StringBuilder();
        details.AppendLine($"引擎：{EngineName(recovery.Engine)}    工具：{recovery.ToolName} {recovery.ToolVersion}");
        details.AppendLine($"输入：{recovery.InputDirectory}");
        details.AppendLine($"输出：{recovery.OutputDirectory}");
        if (recovery.ProcessedContainers > 0)
            details.AppendLine($"容器：{recovery.ProcessedContainers} 个    失败：{recovery.FailedContainers} 个");
        details.Append(recovery.Message);
        SetResult("引擎恢复阶段完成", EngineName(recovery.Engine),
            recovery.FailedContainers == 0 ? Color.FromArgb(236, 253, 245) : Color.FromArgb(255, 247, 237),
            recovery.FailedContainers == 0 ? UiTheme.Success : UiTheme.Warning, details.ToString());
        ConfigureResultActions(recovery.OutputDirectory, recovery.InputDirectory, NextAction.None, string.Empty);
        if (recovery.Engine == GameEngine.Unreal) TryCopyPath(recovery.InputDirectory);
        SetStage(recovery.Message, 100);
    }

    private void ConfigureResultActions(string openPath, string copyPath, NextAction nextAction, string nextText)
    {
        _openPath = openPath;
        _copyPath = copyPath;
        _nextAction = nextAction;
        ApplyResultActionState();
        if (!string.IsNullOrWhiteSpace(nextText)) _resultNext.Text = nextText;
    }

    private void ClearResultActions()
    {
        _openPath = null;
        _copyPath = null;
        _nextAction = NextAction.None;
        ApplyResultActionState();
    }

    private void ApplyResultActionState()
    {
        _resultOpen.Enabled = !_busy && !string.IsNullOrWhiteSpace(_openPath) && Directory.Exists(_openPath);
        _resultCopy.Enabled = !_busy && !string.IsNullOrWhiteSpace(_copyPath);
        SetNextEnabled(!_busy && _nextAction != NextAction.None);
    }

    private void ContinueToNextStage()
    {
        if (_busy) return;
        switch (_nextAction)
        {
            case NextAction.ExtractDownloadedTask:
                _reuseDownloadedTask = _downloadResult != null;
                if (_downloadResult != null) _extractSelection.Text = $"下载任务：{_downloadResult.JobDirectory}";
                SelectMode(WorkflowMode.ExtractAnalyze);
                break;
            case NextAction.OpenWithEngineTool:
                if (!string.IsNullOrWhiteSpace(_copyPath))
                {
                    _assetPath = _copyPath;
                    _assetSelection.Text = _copyPath;
                }
                SelectMode(WorkflowMode.RecoverResources);
                break;
        }
    }

    private void SelectApkFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择一个或多个 APK / Split APK",
            Filter = "Android APK (*.apk)|*.apk|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetExternalExtractPaths(dialog.FileNames);
    }

    private void SelectExtractFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择含 APK 的文件夹、Original_APKs 或以前的任务目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetExternalExtractPaths([dialog.SelectedPath]);
    }

    private void SetExternalExtractPaths(IEnumerable<string> paths)
    {
        _extractPaths.Clear();
        _extractPaths.AddRange(paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        _reuseDownloadedTask = false;
        _downloadResult = null;
        _extractSelection.Text = _extractPaths.Count == 1
            ? _extractPaths[0]
            : $"已选择 {_extractPaths.Count} 个 APK：{string.Join("；", _extractPaths.Take(3).Select(Path.GetFileName))}";
        SelectMode(WorkflowMode.ExtractAnalyze);
    }

    private void SelectAssetDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择现有解压目录、旧任务目录或 AssetRipper_Input", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _assetPath = Path.GetFullPath(dialog.SelectedPath);
        _assetSelection.Text = _assetPath;
    }

    private void BrowseOutput(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择任务保存根目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dialog.SelectedPath;
        _downloadOutput.Text = dialog.SelectedPath;
        _extractOutput.Text = dialog.SelectedPath;
        _settings.OutputDir = dialog.SelectedPath;
        TrySaveSettings();
    }

    private void MainDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void MainDragDrop(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var apkInputs = paths.Where(path => File.Exists(path) && Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var directories = paths.Where(Directory.Exists).ToList();
        if (_mode == WorkflowMode.RecoverResources && apkInputs.Count == 0 && directories.Count == 1)
        {
            _assetPath = Path.GetFullPath(directories[0]);
            _assetSelection.Text = _assetPath;
            AppendLog("已通过拖放选择现有解压目录。");
            return;
        }
        if (apkInputs.Count > 0 || directories.Count > 0)
        {
            SetExternalExtractPaths(apkInputs.Concat(directories));
            AppendLog($"已通过拖放选择 {paths.Length} 个输入。");
            return;
        }
    }

    private async Task<string> InferPackageNameAsync(IReadOnlyList<string> selectedPaths, CancellationToken cancellationToken)
    {
        foreach (var selected in selectedPaths.Where(Directory.Exists))
        {
            var taskRoot = Path.GetFileName(selected).Equals("Original_APKs", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(selected)?.FullName
                : selected;
            if (taskRoot == null) continue;
            var manifest = await TaskManifestStore.TryLoadAsync(taskRoot, cancellationToken);
            if (!string.IsNullOrWhiteSpace(manifest?.PackageName)) return manifest.PackageName;
        }
        var files = await Task.Run(() => ExtractionService.ResolveApkInputs(selectedPaths, cancellationToken), cancellationToken);
        var fromFileName = files.Select(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(name => IsLikelyPackageName(name));
        if (fromFileName != null) return fromFileName;

        foreach (var path in selectedPaths)
        {
            var current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
            for (var depth = 0; current != null && depth < 3; depth++, current = current.Parent)
                if (IsLikelyPackageName(current.Name)) return current.Name;
        }
        return "local-apk";
    }

    private static bool IsLikelyPackageName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    private void ShowSettings()
    {
        if (_busy) return;
        using var form = new SettingsForm(
            _settings,
            async cancellationToken =>
            {
                var value = await _downloadService.RequestAnonymousCredentialsAsync(cancellationToken);
                return (value.Email, value.Token);
            },
            (email, oauth, cancellationToken) => _downloadService.ExchangeOAuthAsync(email, oauth, cancellationToken));
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            TrySaveSettings();
            UpdateSettingsSummary();
            AppendLog("设置已保存。");
        }
    }

    private bool EnsureAssetRipperConfigured()
    {
        if (IsAssetRipperExecutable(_settings.AssetRipperPath)) return true;
        ShowSettings();
        return IsAssetRipperExecutable(_settings.AssetRipperPath);
    }

    private void LaunchAssetRipperOnly()
    {
        try
        {
            if (!EnsureAssetRipperConfigured()) return;
            Process.Start(new ProcessStartInfo(_settings.AssetRipperPath!) { UseShellExecute = true });
            AppendLog("AssetRipper 已普通启动，尚未载入目录。");
            SetStage("AssetRipper 已启动。", 100);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void UpdateSettingsSummary()
    {
        var supportsSplitOption = _downloadSource.SelectedIndex > 0;
        _downloadSplits.Enabled = !_busy && supportsSplitOption;
        _downloadSplits.Text = supportsSplitOption
            ? "下载 Split APK（游戏资源分析推荐）"
            : "Split APK 由 APKPure 当前提供的包决定";
        _credentialStatus.Text = _downloadSource.SelectedIndex switch
        {
            0 => "无需账号；遇到 403 可切换 Google Play。",
            1 => "下载时自动申请匿名临时凭据。",
            _ when !string.IsNullOrWhiteSpace(_settings.Email) && !string.IsNullOrWhiteSpace(_settings.Token) => $"个人账号已配置：{MaskEmail(_settings.Email!)}",
            _ => "个人账号未配置，请打开右上角“设置”。"
        };
        _credentialStatus.ForeColor = _downloadSource.SelectedIndex == 2 && string.IsNullOrWhiteSpace(_settings.Token)
            ? UiTheme.Warning
            : UiTheme.Muted;
        _assetConfiguration.Text = IsAssetRipperExecutable(_settings.AssetRipperPath)
            ? $"Unity：{Path.GetFileName(_settings.AssetRipperPath)}；Godot/UE 工具按需自动准备"
            : "Unity 尚未配置 AssetRipper；Godot/UE 工具按需自动准备。";
        _assetConfiguration.ForeColor = IsAssetRipperExecutable(_settings.AssetRipperPath) ? UiTheme.Success : UiTheme.Warning;
    }

    private void SaveMainSettings()
    {
        _settings.OutputDir = _mode == WorkflowMode.ExtractAnalyze ? _extractOutput.Text.Trim() : _downloadOutput.Text.Trim();
        _settings.Split = _downloadSplits.Checked;
        _settings.Source = Math.Max(0, _downloadSource.SelectedIndex);
        _settings.LastMode = (int)_mode;
        _downloadOutput.Text = _settings.OutputDir;
        _extractOutput.Text = _settings.OutputDir;
        TrySaveSettings();
    }

    private void MainFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveMainSettings();
        if (!_busy) return;

        e.Cancel = true;
        if (_closeWhenIdle) return;
        _closeWhenIdle = true;
        _operationCts?.Cancel();
        _cancel.Enabled = false;
        SetStage("正在安全停止当前阶段，完成任务状态写入后关闭…", null);
    }

    private void TrySaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { AppendLog("保存设置失败：" + ex.Message); }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _operationCts = new CancellationTokenSource();
        ClearResultActions();
        SetBusy(true);
        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException) when (_operationCts.IsCancellationRequested)
        {
            ClearResultActions();
            SetResult("操作已取消", "已取消", Color.FromArgb(248, 250, 252), UiTheme.Muted,
                "当前阶段已停止。已完成并写入磁盘的原始 APK 或任务文件会保留。可调整输入后重新开始。");
            SetStage("当前阶段已取消。", 0);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _engineKey.Clear();
            SetBusy(false);
            _operationCts.Dispose();
            _operationCts = null;
            if (_closeWhenIdle && !IsDisposed) BeginInvoke(Close);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var card in _modeCards) card.Enabled = !busy;
        _settingsButton.Enabled = !busy;
        _downloadPackage.Enabled = !busy;
        _downloadSource.Enabled = !busy;
        _downloadOutput.Enabled = !busy;
        _downloadRun.Enabled = !busy;
        _downloadBrowse.Enabled = !busy;
        _extractOutput.Enabled = !busy;
        _extractSelectFiles.Enabled = !busy;
        _extractSelectFolder.Enabled = !busy;
        _extractBrowseOutput.Enabled = !busy;
        _extractRun.Enabled = !busy;
        _assetSelectDirectory.Enabled = !busy;
        _assetLaunchOnly.Enabled = !busy;
        _assetRun.Enabled = !busy;
        _engineKey.Enabled = !busy;
        _unrealExtract.Enabled = !busy;
        _cancel.Enabled = busy;
        AllowDrop = !busy;
        UseWaitCursor = busy;
        UpdateSettingsSummary();
        ApplyResultActionState();
        if (!busy && _progress.Style == ProgressBarStyle.Marquee)
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 0;
        }
    }

    private void ReportWorkflowProgress(WorkflowProgress value)
    {
        var detail = value.Total > 0 ? $" ({value.Current}/{value.Total})" : string.Empty;
        SetStage(value.Message + detail, value.Percent);
    }

    private void SetStage(string message, int? percent, bool writeLog = true)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStage(message, percent, writeLog));
            return;
        }
        _stage.Text = message;
        if (percent.HasValue)
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = Math.Clamp(percent.Value, 0, 100);
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
        }
        if (writeLog) AppendLog(message);
    }

    private void ShowError(Exception exception)
    {
        AppendLog("错误：" + exception);
        SetResult("本阶段未完成", "错误", Color.FromArgb(254, 242, 242), UiTheme.Danger,
            exception.Message + "\n\n已生成的任务目录和原始 APK 会保留，可检查运行日志后重试。 ");
        _openPath = null;
        _copyPath = null;
        _nextAction = NextAction.None;
        _resultOpen.Enabled = false;
        _resultCopy.Enabled = false;
        SetNextEnabled(false);
        SetStage("本阶段发生错误。展开运行日志可查看详情。", 0);
    }

    private void SetResult(string title, string badge, Color badgeBackground, Color badgeForeground, string details)
    {
        _resultTitle.Text = title;
        _resultBadge.Text = badge;
        _resultBadge.BackColor = badgeBackground;
        _resultBadge.ForeColor = badgeForeground;
        _resultDetails.Text = details;
    }

    private void ToggleLog()
    {
        _logExpanded = !_logExpanded;
        if (_logExpanded && WindowState == FormWindowState.Normal)
        {
            _logCollapsedBounds = Bounds;
            var workingArea = Screen.FromControl(this).WorkingArea;
            var desiredHeight = Height + 118;
            if (desiredHeight <= workingArea.Height)
            {
                var expandedTop = Math.Min(Top, workingArea.Bottom - desiredHeight);
                expandedTop = Math.Max(workingArea.Top, expandedTop);
                Bounds = new Rectangle(Left, expandedTop, Width, desiredHeight);
            }
        }
        _root.RowStyles[5].SizeType = SizeType.Absolute;
        _root.RowStyles[5].Height = _logExpanded ? 118 : 0;
        _logToggle.Text = _logExpanded ? "收起运行日志 ︽" : "展开运行日志 ︾";
        if (!_logExpanded && _logCollapsedBounds.HasValue && WindowState == FormWindowState.Normal)
        {
            Bounds = _logCollapsedBounds.Value;
            _logCollapsedBounds = null;
        }
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}\r\n");
    }

    private void SetNextEnabled(bool enabled)
    {
        _resultNext.Enabled = enabled;
        _resultNext.BackColor = enabled ? UiTheme.Primary : Color.FromArgb(226, 232, 240);
        _resultNext.ForeColor = enabled ? Color.White : UiTheme.Muted;
    }

    private void CopyCurrentPath()
    {
        if (string.IsNullOrWhiteSpace(_copyPath)) return;
        if (TryCopyPath(_copyPath))
            SetStage("路径已复制到剪贴板。", 100);
        else
            SetStage("剪贴板暂时被占用；路径仍显示在结果卡片中。", 100);
    }

    private static bool TryCopyPath(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(path);
                return true;
            }
            catch when (attempt < 2)
            {
                Thread.Sleep(30);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static bool TryOpenPath(string path)
    {
        var target = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return false;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(target);
            Process.Start(start);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAssetRipperExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static string ModeTitle(WorkflowMode mode) => mode switch
    {
        WorkflowMode.Download => "下载 APK",
        WorkflowMode.ExtractAnalyze => "解压与分析",
        _ => "引擎恢复"
    };

    private static string EngineName(GameEngine engine) => engine switch
    {
        GameEngine.Unity => "Unity",
        GameEngine.Godot => "Godot",
        GameEngine.Unreal => "Unreal",
        _ => "未知"
    };

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***" + (at >= 0 ? email[at..] : string.Empty);
        return email[..1] + "***" + email[(at - 1)..];
    }
}
