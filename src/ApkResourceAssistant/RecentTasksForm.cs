namespace GooglePlayApkDownloader;

internal sealed class RecentTasksForm : Form
{
    private readonly RecentTaskService _service;
    private readonly string? _outputRoot;
    private readonly TextBox _search = UiTheme.TextInput();
    private readonly ComboBox _engineFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None
    };
    private readonly Label _status = UiTheme.Caption("正在读取最近任务…");
    private readonly Button _continue = UiTheme.PrimaryButton("继续任务");
    private readonly Button _open = UiTheme.SecondaryButton("打开目录");
    private readonly Button _remove = UiTheme.SecondaryButton("移除记录");
    private readonly Button _refresh = UiTheme.SecondaryButton("刷新扫描");
    private IReadOnlyList<RecentTaskEntry> _tasks = [];

    public RecentTaskEntry? SelectedTask { get; private set; }

    public RecentTasksForm(RecentTaskService service, string? outputRoot)
    {
        _service = service;
        _outputRoot = outputRoot;
        Text = "最近任务";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 500);
        MinimumSize = new Size(700, 420);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = UiTheme.Window;
        Padding = new Padding(18);

        BuildLayout();
        WireEvents();
        Shown += async (_, _) => await LoadTasksAsync(false);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiTheme.Window };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Controls.Add(root);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(UiTheme.Heading("最近任务", 16F));
        header.Controls.Add(UiTheme.Caption("双击任务可继续；移除记录不会删除任何 APK 或恢复文件。"));
        root.Controls.Add(header, 0, 0);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        filters.Controls.Add(new Label { Text = "搜索", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "包名、版本或路径";
        filters.Controls.Add(_search, 1, 0);
        filters.Controls.Add(new Label { Text = "引擎", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(12, 0, 0, 0) }, 2, 0);
        _engineFilter.Dock = DockStyle.Fill;
        _engineFilter.Items.AddRange(["全部", "Unity", "Godot", "Unreal", "未知"]);
        _engineFilter.SelectedIndex = 0;
        filters.Controls.Add(_engineFilter, 3, 0);
        root.Controls.Add(filters, 0, 1);

        ConfigureGrid();
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0) };
        card.Controls.Add(_grid);
        root.Controls.Add(card, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_status, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _continue.Width = 108;
        _open.Width = 94;
        _remove.Width = 94;
        _refresh.Width = 94;
        foreach (var button in new[] { _continue, _open, _remove, _refresh }) button.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(_continue);
        actions.Controls.Add(_open);
        actions.Controls.Add(_remove);
        actions.Controls.Add(_refresh);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 3);
    }

    private void ConfigureGrid()
    {
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = UiTheme.PrimarySoft;
        _grid.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        _grid.RowTemplate.Height = 34;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Package", HeaderText = "包名", DataPropertyName = "PackageName", FillWeight = 26, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Engine", HeaderText = "引擎 / 版本", FillWeight = 20, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stage", HeaderText = "阶段", Width = 108 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Updated", HeaderText = "更新时间", Width = 132 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "任务目录", FillWeight = 34, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => ApplyFilter();
        _engineFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ContinueSelected(); };
        _continue.Click += (_, _) => ContinueSelected();
        _open.Click += (_, _) => OpenSelected();
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        _refresh.Click += async (_, _) => await LoadTasksAsync(true);
    }

    private async Task LoadTasksAsync(bool refresh)
    {
        SetBusy(true);
        try
        {
            _tasks = refresh
                ? await _service.RefreshFromOutputRootAsync(_outputRoot)
                : await _service.LoadAsync();
            if (!refresh && _tasks.Count == 0 && Directory.Exists(_outputRoot))
                _tasks = await _service.RefreshFromOutputRootAsync(_outputRoot);
            ApplyFilter();
        }
        catch (Exception ex) { _status.Text = "读取最近任务失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        var engine = _engineFilter.SelectedIndex switch
        {
            1 => GameEngine.Unity,
            2 => GameEngine.Godot,
            3 => GameEngine.Unreal,
            4 => GameEngine.Unknown,
            _ => (GameEngine?)null
        };
        var filtered = _tasks.Where(item => engine == null || item.Engine == engine)
            .Where(item => query.Length == 0
                           || item.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || (item.EngineVersion?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                           || item.JobRoot.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _grid.Rows.Clear();
        foreach (var task in filtered)
        {
            var index = _grid.Rows.Add(task.PackageName,
                $"{EngineName(task.Engine)} {task.EngineVersion ?? string.Empty}".Trim(),
                StageName(task), task.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), task.JobRoot);
            _grid.Rows[index].Tag = task;
        }
        _status.Text = $"共 {filtered.Length} 条任务，索引最多保留 30 条。";
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        UpdateButtons();
    }

    private RecentTaskEntry? CurrentTask => _grid.SelectedRows.Count == 1
        ? _grid.SelectedRows[0].Tag as RecentTaskEntry : null;

    private void ContinueSelected()
    {
        var task = CurrentTask;
        if (task == null) return;
        SelectedTask = task;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenSelected()
    {
        var task = CurrentTask;
        if (task == null || !Directory.Exists(task.JobRoot)) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(task.JobRoot);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) { _status.Text = "打开目录失败：" + ex.Message; }
    }

    private async Task RemoveSelectedAsync()
    {
        var task = CurrentTask;
        if (task == null) return;
        await _service.RemoveAsync(task.JobRoot);
        _tasks = _tasks.Where(item => !item.JobRoot.Equals(task.JobRoot, StringComparison.OrdinalIgnoreCase)).ToArray();
        ApplyFilter();
    }

    private void UpdateButtons()
    {
        var selected = CurrentTask != null;
        _continue.Enabled = selected;
        _open.Enabled = selected;
        _remove.Enabled = selected;
    }

    private void SetBusy(bool busy)
    {
        _refresh.Enabled = !busy;
        _search.Enabled = !busy;
        _engineFilter.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private static string StageName(RecentTaskEntry task)
    {
        if (!string.IsNullOrWhiteSpace(task.RecoveryDirectory)) return "已恢复";
        if (task.Status == WorkflowTaskStatus.Failed) return "失败";
        if (task.Status == WorkflowTaskStatus.Cancelled) return "已取消";
        if (task.Engine != GameEngine.Unknown || task.Stage is WorkflowStage.ReadyForAssetRipper or WorkflowStage.Completed) return "已分析";
        if (task.Stage == WorkflowStage.Downloaded) return "已下载";
        return task.Stage.ToString();
    }

    private static string EngineName(GameEngine engine) => engine switch
    {
        GameEngine.Unity => "Unity",
        GameEngine.Godot => "Godot",
        GameEngine.Unreal => "Unreal",
        _ => "未知"
    };
}
