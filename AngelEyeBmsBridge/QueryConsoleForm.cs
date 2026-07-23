using System.Globalization;
using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>Read-only TeleBet operations console backed exclusively by Worker GET APIs.</summary>
public sealed class QueryConsoleForm : Form
{
    private readonly TeleBetQueryClient _client = new();
    private readonly QueryConsoleState _state = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    private readonly Label _sourceBanner = new() { Text = "來源尚未確認", AutoSize = true, Font = new Font("Microsoft JhengHei", 11F, FontStyle.Bold), ForeColor = Color.Gold, Margin = new Padding(12, 6, 0, 0) };
    private readonly Label _connectionBanner = new()
    {
        Text = "尚未連線",
        AutoSize = false,
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(210, 220, 230),
        TextAlign = ContentAlignment.MiddleRight,
        Padding = new Padding(0, 0, 2, 0)
    };
    private readonly DataGridView _dashboard = CreateGrid();
    private readonly DataGridView _rounds = CreateGrid();
    private readonly DataGridView _timeline = CreateGrid();
    private readonly DataGridView _exceptions = CreateGrid();
    private readonly DataGridView _events = CreateGrid();
    private readonly DataGridView _outbox = CreateGrid();
    private readonly DataGridView _recoveries = CreateGrid();
    private readonly TextBox _roundDetail = CreateReadOnlyTextBox();
    private readonly TextBox _payload = CreateReadOnlyTextBox();
    private readonly DateTimePicker _fromDate = new() { Format = DateTimePickerFormat.Short, Width = 115 };
    private readonly DateTimePicker _toDate = new() { Format = DateTimePickerFormat.Short, Width = 115 };
    private readonly ComboBox _deskFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox _stateFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _shoeFilter = new() { Width = 125 };
    private readonly TextBox _roundFilter = new() { Width = 70 };
    private readonly Button _loadMoreRounds = new() { Text = "載入更多", Width = 95, Enabled = false };
    private readonly List<QueryRoundData> _roundRows = [];
    private string? _roundCursor;
    private bool _refreshing;
    private bool _closing;

    public QueryConsoleForm() : this(autoStartQueries: true)
    {
    }

    /// <summary>Creates the console; tests and offline renderers can disable automatic network queries.</summary>
    public QueryConsoleForm(bool autoStartQueries)
    {
        Text = "ANGEL 電投營運查詢台";
        Font = new Font("Microsoft JhengHei", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1180, 760);
        BackColor = Color.FromArgb(240, 244, 248);

        foreach (QueryServerProfile profile in QueryServerProfile.Defaults)
        {
            _profile.Items.Add(profile);
        }
        _profile.DisplayMember = nameof(QueryServerProfile.Name);
        _profile.SelectedIndex = 0;
        _deskFilter.Items.AddRange(["全部", "901", "902", "903"]);
        _deskFilter.SelectedIndex = 0;
        _stateFilter.Items.AddRange(["全部", "Started", "Dealing", "Settled"]);
        _stateFilter.SelectedIndex = 0;
        _fromDate.Value = DateTime.Today.AddDays(-1);
        _toDate.Value = DateTime.Today;

        BuildLayout();
        ConfigureGrids();
        _rounds.SelectionChanged += async (_, _) => await LoadSelectedRoundDetailAsync();
        _events.SelectionChanged += (_, _) => ShowSelectedPayload();
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        if (autoStartQueries)
        {
            Shown += async (_, _) =>
            {
                await RefreshStatusAsync();
                await SearchRoundsAsync(reset: true);
                _refreshTimer.Start();
            };
        }
        FormClosing += (_, _) =>
        {
            _closing = true;
            _refreshTimer.Stop();
            _client.Dispose();
        };
    }

    private QueryServerProfile SelectedProfile => _profile.SelectedItem as QueryServerProfile ?? QueryServerProfile.Defaults[0];

    private void BuildLayout()
    {
        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(25, 42, 56),
            Padding = new Padding(16, 10, 16, 8)
        };
        Label title = new()
        {
            Text = "ANGEL 電投營運查詢台",
            AutoSize = true,
            Font = new Font("Microsoft JhengHei", 16F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(16, 8)
        };
        TableLayoutPanel headerStatus = new()
        {
            Dock = DockStyle.Right,
            Width = 760,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(0)
        };
        headerStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        headerStatus.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        FlowLayoutPanel headerActions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0),
            BackColor = Color.Transparent
        };
        Label profileLabel = new() { Text = "查詢來源", AutoSize = true, ForeColor = Color.White, Margin = new Padding(0, 8, 6, 0) };
        Button refresh = new() { Text = "立即刷新", Width = 95, Height = 30 };
        refresh.Click += async (_, _) => await RefreshAllAsync();
        _profile.SelectedIndexChanged += async (_, _) =>
        {
            if (Visible)
            {
                await RefreshAllAsync();
            }
        };
        headerActions.Controls.Add(profileLabel);
        headerActions.Controls.Add(_profile);
        headerActions.Controls.Add(refresh);
        headerActions.Controls.Add(_sourceBanner);
        headerStatus.Controls.Add(headerActions, 0, 0);
        headerStatus.Controls.Add(_connectionBanner, 0, 1);
        header.Controls.Add(title);
        header.Controls.Add(headerStatus);
        Controls.Add(header);

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold),
            Padding = new Point(16, 7)
        };
        tabs.TabPages.Add(BuildDashboardTab());
        tabs.TabPages.Add(BuildRoundsTab());
        tabs.TabPages.Add(BuildExceptionsTab());
        tabs.TabPages.Add(BuildTechnicalTab());
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedIndex == 2)
            {
                await LoadExceptionsAsync();
            }
        };
        Controls.Add(tabs);
        tabs.BringToFront();
    }

    private TabPage BuildDashboardTab()
    {
        TabPage tab = NewTab("總覽");
        Panel note = new()
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(232, 245, 233)
        };
        note.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "本頁顯示 Worker 本機觀測與 BMS endpoint 接受狀態，不代表 BMS 後續流程最終入庫。",
            ForeColor = Color.FromArgb(32, 95, 55),
            TextAlign = ContentAlignment.MiddleLeft
        });
        tab.Controls.Add(_dashboard);
        tab.Controls.Add(note);
        return tab;
    }

    private TabPage BuildRoundsTab()
    {
        TabPage tab = NewTab("牌局查詢");
        FlowLayoutPanel filters = new()
        {
            Dock = DockStyle.Top,
            Height = 47,
            Padding = new Padding(8, 8, 8, 4),
            BackColor = Color.White,
            WrapContents = false
        };
        filters.Controls.Add(Field("開始日", _fromDate));
        filters.Controls.Add(Field("結束日", _toDate));
        filters.Controls.Add(Field("桌台", _deskFilter));
        filters.Controls.Add(Field("靴號", _shoeFilter));
        filters.Controls.Add(Field("局號", _roundFilter));
        filters.Controls.Add(Field("狀態", _stateFilter));
        Button search = new() { Text = "查詢", Width = 85, Height = 28 };
        search.Click += async (_, _) => await SearchRoundsAsync(reset: true);
        _loadMoreRounds.Click += async (_, _) => await SearchRoundsAsync(reset: false);
        filters.Controls.Add(search);
        filters.Controls.Add(_loadMoreRounds);

        SplitContainer content = new()
        {
            Dock = DockStyle.Fill,
            Size = new Size(1100, 650),
            Orientation = Orientation.Horizontal,
            SplitterDistance = 330,
            Panel1MinSize = 220,
            Panel2MinSize = 240
        };
        content.Panel1.Controls.Add(_rounds);
        SplitContainer detail = new() { Dock = DockStyle.Fill, Size = new Size(1100, 300), SplitterDistance = 430, Panel1MinSize = 330, Panel2MinSize = 450 };
        GroupBox roundBox = new() { Dock = DockStyle.Fill, Text = " 單局牌面與資料來源 ", Padding = new Padding(8) };
        roundBox.Controls.Add(_roundDetail);
        GroupBox timelineBox = new() { Dock = DockStyle.Fill, Text = " 事件時間線 ", Padding = new Padding(8) };
        timelineBox.Controls.Add(_timeline);
        detail.Panel1.Controls.Add(roundBox);
        detail.Panel2.Controls.Add(timelineBox);
        content.Panel2.Controls.Add(detail);
        tab.Controls.Add(content);
        tab.Controls.Add(filters);
        return tab;
    }

    private TabPage BuildExceptionsTab()
    {
        TabPage tab = NewTab("異常中心");
        FlowLayoutPanel bar = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), BackColor = Color.White };
        Button refresh = new() { Text = "刷新異常", Width = 100, Height = 28 };
        refresh.Click += async (_, _) => await LoadExceptionsAsync();
        bar.Controls.Add(refresh);
        bar.Controls.Add(new Label
        {
            Text = "只呈現 Worker 本機證據；找不到 GameResult 不代表牌盒一定未結算。",
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(14, 6, 0, 0)
        });
        tab.Controls.Add(_exceptions);
        tab.Controls.Add(bar);
        return tab;
    }

    private TabPage BuildTechnicalTab()
    {
        TabPage tab = NewTab("技術資料（唯讀）");
        Label safety = new()
        {
            Dock = DockStyle.Top,
            Height = 38,
            Text = "此區只有 GET 查詢；沒有重送、Lock、Unlock、清錯、傳送開關、設定或主備切換。",
            ForeColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        TabControl technicalTabs = new() { Dock = DockStyle.Fill };
        technicalTabs.TabPages.Add(BuildEventsTechnicalTab());
        technicalTabs.TabPages.Add(BuildSimpleTechnicalTab("Outbox", _outbox, async () => await LoadOutboxAsync()));
        technicalTabs.TabPages.Add(BuildSimpleTechnicalTab("Recoveries", _recoveries, async () => await LoadRecoveriesAsync()));
        tab.Controls.Add(technicalTabs);
        tab.Controls.Add(safety);
        return tab;
    }

    private TabPage BuildEventsTechnicalTab()
    {
        TabPage tab = NewTab("Events / Payload");
        Button load = new() { Text = "載入最近事件", Dock = DockStyle.Top, Height = 32 };
        load.Click += async (_, _) => await LoadEventsAsync();
        SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
        split.Panel1.Controls.Add(_events);
        GroupBox payloadBox = new() { Dock = DockStyle.Fill, Text = " 已去敏 Payload ", Padding = new Padding(8) };
        payloadBox.Controls.Add(_payload);
        split.Panel2.Controls.Add(payloadBox);
        tab.Controls.Add(split);
        tab.Controls.Add(load);
        return tab;
    }

    private static TabPage BuildSimpleTechnicalTab(string title, DataGridView grid, Func<Task> loadAction)
    {
        TabPage tab = NewTab(title);
        Button load = new() { Text = $"載入 {title}", Dock = DockStyle.Top, Height = 32 };
        load.Click += async (_, _) => await loadAction();
        tab.Controls.Add(grid);
        tab.Controls.Add(load);
        return tab;
    }

    private void ConfigureGrids()
    {
        AddColumns(_dashboard, ["桌台", "連線", "最後事件", "靴號", "局號", "狀態", "BMS傳送", "待送", "失敗"]);
        AddColumns(_rounds, ["桌台", "靴號", "局號", "開始", "結算", "牌局狀態", "傳送證據", "重試", "錯誤"]);
        AddColumns(_timeline, ["時間", "事件", "Event ID", "狀態", "HTTP", "重試", "訊息"]);
        AddColumns(_exceptions, ["等級", "類型", "桌台", "靴／局", "時間", "原因／建議", "下次重試"]);
        AddColumns(_events, ["Event ID", "時間", "類型", "桌台", "靴號", "局號", "狀態", "HTTP", "重試", "錯誤"]);
        AddColumns(_outbox, ["Event ID", "時間", "類型", "桌台", "靴／局", "狀態", "HTTP", "重試", "下次重試", "錯誤"]);
        AddColumns(_recoveries, ["Command ID", "類型", "桌台", "靴／局", "結果", "收到時間", "最近觀測", "下次重試", "訊息"]);
    }

    private async Task RefreshAllAsync()
    {
        await RefreshStatusAsync();
        await SearchRoundsAsync(reset: true);
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing || _closing)
        {
            return;
        }
        _refreshing = true;
        try
        {
            QueryApiEnvelope<QueryStatusData> response = await _client.GetStatusAsync(SelectedProfile.BaseUri);
            _state.MarkSuccess(response.Data, DateTimeOffset.UtcNow);
            bool verified = QueryConsoleProjection.IsMetadataVerified(response.Meta, SelectedProfile);
            _sourceBanner.Text = verified
                ? $"{response.Meta.InstanceName} · {response.Meta.Environment} · {response.Meta.Role}"
                : "來源未確認";
            _sourceBanner.ForeColor = verified ? EnvironmentColor(response.Meta.Environment) : Color.Gold;
            _connectionBanner.Text = $"更新 {DateTime.Now:HH:mm:ss}";
            _connectionBanner.ForeColor = Color.FromArgb(190, 235, 205);
            RenderDashboard(response.Data);
        }
        catch (Exception ex)
        {
            _state.MarkFailure(ex.Message);
            string stale = _state.LastSuccessfulUtc.HasValue
                ? $"；保留 {_state.LastSuccessfulUtc.Value.LocalDateTime:HH:mm:ss} 資料"
                : string.Empty;
            _connectionBanner.Text = $"離線{stale}";
            _connectionBanner.ForeColor = Color.FromArgb(255, 160, 150);
            if (_state.LastStatus == null)
            {
                _sourceBanner.Text = "來源未確認";
                _sourceBanner.ForeColor = Color.Gold;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderDashboard(QueryStatusData status)
    {
        _dashboard.Rows.Clear();
        foreach (QueryEndpointData endpoint in status.Endpoints)
        {
            int rowIndex = _dashboard.Rows.Add(
                endpoint.SourceDataCode,
                QueryConsoleProjection.ConnectionState(endpoint),
                endpoint.LastEventUtc.HasValue ? $"{FormatTime(endpoint.LastEventUtc.Value)}  {endpoint.LastEvent}" : "尚無事件",
                endpoint.Shoe,
                endpoint.Round,
                endpoint.Status,
                endpoint.BmsTransmitEnabled ? "啟用" : "關閉",
                endpoint.PendingCount,
                endpoint.FailedCount);
            if (endpoint.Enabled && (!endpoint.Connected || endpoint.FailedCount > 0))
            {
                _dashboard.Rows[rowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 235, 235);
            }
        }
    }

    private async Task SearchRoundsAsync(bool reset)
    {
        try
        {
            QueryRoundFilters filters = ReadRoundFilters();
            string? cursor = reset ? null : _roundCursor;
            QueryApiEnvelope<List<QueryRoundData>> response = await _client.GetRoundsAsync(SelectedProfile.BaseUri, filters, cursor);
            if (reset)
            {
                _roundRows.Clear();
                _rounds.Rows.Clear();
            }
            IReadOnlyList<QueryRoundData> merged = QueryConsoleProjection.MergeRounds(_roundRows, response.Data);
            IEnumerable<QueryRoundData> added = reset ? merged : merged.Skip(_roundRows.Count);
            foreach (QueryRoundData round in added)
            {
                _roundRows.Add(round);
                int rowIndex = _rounds.Rows.Add(
                    round.SourceDataCode,
                    round.Shoe,
                    round.Round,
                    FormatTime(round.StartedUtc),
                    FormatTime(round.SettledUtc),
                    QueryDisplayText.RoundState(round),
                    QueryDisplayText.Delivery(round.DeliveryStatus),
                    round.RetryCount,
                    round.LastError ?? string.Empty);
                _rounds.Rows[rowIndex].Tag = round;
                if (!round.IsComplete || string.Equals(round.DeliveryStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    _rounds.Rows[rowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 225);
                }
            }
            _roundCursor = response.NextCursor;
            _loadMoreRounds.Enabled = !string.IsNullOrWhiteSpace(_roundCursor);
        }
        catch (Exception ex)
        {
            _connectionBanner.Text = $"牌局查詢失敗；篩選條件與既有資料已保留（{ex.Message}）";
            _connectionBanner.ForeColor = Color.FromArgb(255, 190, 120);
        }
    }

    private QueryRoundFilters ReadRoundFilters()
    {
        if (_toDate.Value.Date < _fromDate.Value.Date)
        {
            throw new InvalidOperationException("結束日不可早於開始日。");
        }
        return new QueryRoundFilters(
            _deskFilter.SelectedIndex <= 0 ? string.Empty : _deskFilter.Text,
            new DateTimeOffset(DateTime.SpecifyKind(_fromDate.Value.Date, DateTimeKind.Local)).ToUniversalTime(),
            new DateTimeOffset(DateTime.SpecifyKind(_toDate.Value.Date.AddDays(1), DateTimeKind.Local)).ToUniversalTime(),
            ParseOptionalLong(_shoeFilter.Text, "靴號"),
            ParseOptionalLong(_roundFilter.Text, "局號"),
            _stateFilter.SelectedIndex <= 0 ? string.Empty : _stateFilter.Text);
    }

    private async Task LoadSelectedRoundDetailAsync()
    {
        if (_rounds.CurrentRow?.Tag is not QueryRoundData selected || _closing)
        {
            return;
        }
        try
        {
            QueryApiEnvelope<QueryRoundDetailData> response = await _client.GetRoundDetailAsync(
                SelectedProfile.BaseUri, selected.SourceDataCode, selected.Shoe, selected.Round);
            QueryRoundData round = response.Data.Round;
            _roundDetail.Text = string.Join(Environment.NewLine,
            [
                $"資料來源：{response.Meta.InstanceName} / {response.Meta.Environment} / {response.Meta.Role}",
                $"桌台：{round.SourceDataCode}",
                $"靴／局：{round.Shoe} / {round.Round}",
                $"狀態：{QueryDisplayText.RoundState(round)}",
                $"開始：{FormatTime(round.StartedUtc)}",
                $"結算：{FormatTime(round.SettledUtc)}",
                $"傳送：{QueryDisplayText.Delivery(round.DeliveryStatus)}",
                $"牌面：{PrettyJson(round.CardsJson)}",
                $"結果：{PrettyJson(round.ResultJson)}",
                string.IsNullOrWhiteSpace(round.LastError) ? string.Empty : $"最後錯誤：{round.LastError}"
            ]);
            _timeline.Rows.Clear();
            foreach (QueryEventData item in response.Data.Timeline)
            {
                _timeline.Rows.Add(
                    FormatTime(item.OccurredUtc), item.Type, item.EventId,
                    QueryDisplayText.Delivery(item.Status), item.HttpStatus?.ToString() ?? "-",
                    item.RetryCount, item.LastError ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            _roundDetail.Text = $"單局明細載入失敗：{ex.Message}";
        }
    }

    private async Task LoadExceptionsAsync()
    {
        try
        {
            QueryRoundFilters filters = new("", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(1), null, null, "", 200);
            Task<QueryApiEnvelope<List<QueryRoundData>>> roundsTask = _client.GetRoundsAsync(SelectedProfile.BaseUri, filters);
            Task<QueryApiEnvelope<List<QueryEventData>>> failedTask = _client.GetOutboxAsync(SelectedProfile.BaseUri, new QueryEventFilters(Status: "Failed", PageSize: 200));
            Task<QueryApiEnvelope<List<QueryEventData>>> pendingTask = _client.GetOutboxAsync(SelectedProfile.BaseUri, new QueryEventFilters(Status: "Pending", PageSize: 200));
            Task<QueryApiEnvelope<List<QueryRecoveryData>>> recoveriesTask = _client.GetRecoveriesAsync(SelectedProfile.BaseUri);
            await Task.WhenAll(roundsTask, failedTask, pendingTask, recoveriesTask);

            IReadOnlyList<QueryExceptionItem> rows = QueryConsoleProjection.BuildExceptions(
                roundsTask.Result.Data, failedTask.Result.Data, pendingTask.Result.Data, recoveriesTask.Result.Data, DateTimeOffset.UtcNow);

            _exceptions.Rows.Clear();
            foreach (QueryExceptionItem row in rows)
            {
                int index = _exceptions.Rows.Add(row.Level, row.Type, row.Desk, row.Round, FormatTime(row.Time), row.Message, FormatTime(row.NextRetry));
                _exceptions.Rows[index].DefaultCellStyle.BackColor = row.Level == "錯誤"
                    ? Color.FromArgb(255, 225, 225)
                    : Color.FromArgb(255, 246, 220);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"異常資料載入失敗：{ex.Message}", "查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task LoadEventsAsync()
    {
        try
        {
            QueryApiEnvelope<List<QueryEventData>> response = await _client.GetEventsAsync(
                SelectedProfile.BaseUri, new QueryEventFilters(PageSize: 200, IncludePayload: true));
            FillEventGrid(_events, response.Data, includeNextRetry: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Events 查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task LoadOutboxAsync()
    {
        try
        {
            QueryApiEnvelope<List<QueryEventData>> response = await _client.GetOutboxAsync(
                SelectedProfile.BaseUri, new QueryEventFilters(PageSize: 200));
            _outbox.Rows.Clear();
            foreach (QueryEventData item in response.Data)
            {
                _outbox.Rows.Add(item.EventId, FormatTime(item.OccurredUtc), item.Type, item.SourceDataCode,
                    $"{item.Shoe}/{item.Round}", QueryDisplayText.Delivery(item.Status), item.HttpStatus?.ToString() ?? "-",
                    item.RetryCount, FormatTime(item.NextRetryUtc), item.LastError ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Outbox 查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task LoadRecoveriesAsync()
    {
        try
        {
            QueryApiEnvelope<List<QueryRecoveryData>> response = await _client.GetRecoveriesAsync(SelectedProfile.BaseUri);
            _recoveries.Rows.Clear();
            foreach (QueryRecoveryData item in response.Data)
            {
                _recoveries.Rows.Add(item.CommandId, item.CommandType, item.SourceDataCode, $"{item.Shoe}/{item.Round}",
                    item.Result, FormatTime(item.ReceivedUtc), FormatTime(item.LastObservedUtc), FormatTime(item.NextRetryUtc), item.Message ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Recoveries 查詢失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void FillEventGrid(DataGridView grid, IReadOnlyList<QueryEventData> events, bool includeNextRetry)
    {
        grid.Rows.Clear();
        foreach (QueryEventData item in events)
        {
            int index = grid.Rows.Add(item.EventId, FormatTime(item.OccurredUtc), item.Type, item.SourceDataCode,
                item.Shoe, item.Round, QueryDisplayText.Delivery(item.Status), item.HttpStatus?.ToString() ?? "-",
                item.RetryCount, item.LastError ?? string.Empty);
            grid.Rows[index].Tag = item;
        }
    }

    private void ShowSelectedPayload()
    {
        if (_events.CurrentRow?.Tag is QueryEventData item)
        {
            _payload.Text = PrettyJson(item.PayloadJson);
        }
    }

    private static TabPage NewTab(string text) => new(text) { BackColor = Color.FromArgb(240, 244, 248), Padding = new Padding(8) };

    private static Control Field(string label, Control input)
    {
        FlowLayoutPanel panel = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 10, 0) };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        panel.Controls.Add(input);
        return panel;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        EnableHeadersVisualStyles = false,
        ColumnHeadersHeight = 34
    };

    private static TextBox CreateReadOnlyTextBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        BackColor = Color.White,
        Font = new Font("Consolas", 9.5F)
    };

    private static void AddColumns(DataGridView grid, IEnumerable<string> headers)
    {
        grid.Columns.Clear();
        int index = 0;
        foreach (string header in headers)
        {
            grid.Columns.Add($"c{index++}", header);
        }
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(41, 128, 185);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);
    }

    private static long? ParseOptionalLong(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value >= 0
            ? value
            : throw new InvalidOperationException($"{label}必須是非負整數。");
    }

    private static string FormatTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string FormatTime(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
        ? FormatTime(parsed)
        : "-";
    private static DateTimeOffset ParseUtc(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
        ? parsed.ToUniversalTime()
        : DateTimeOffset.MinValue;
    private static Color EnvironmentColor(string environment) => string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)
        ? Color.FromArgb(255, 190, 120)
        : Color.FromArgb(150, 230, 255);

    private static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "歷史資料不完整／尚無本機證據";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
