using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Windows.Forms;

namespace AngelEyeBmsBridge;

/// <summary>
/// Main WinForms surface for configuring ANGEL shoe endpoints, testing local serial events, and posting events to BMS.
/// </summary>
public partial class Form1 : Form
{
    private readonly BmsApiClient _bmsApiClient = new();
    private readonly ToolTip _fieldToolTip = new()
    {
        AutoPopDelay = 8000,
        InitialDelay = 450,
        ReshowDelay = 100,
        ShowAlways = true
    };
    private readonly BridgeSettings _settings;
    private readonly List<ShoeEndpoint> _endpoints = [];
    private readonly Random _random = new();
    private readonly System.Windows.Forms.Timer _autoRunTimer = new();
    private readonly System.Windows.Forms.Timer _outboxStatusTimer = new();
    private readonly System.Windows.Forms.Timer _nextRoundCountdownTimer = new();
    private BridgeEventJournal? _eventJournal;
    private long _eventSequence;
    private readonly Dictionary<ShoeEndpoint, BaccaratAutoRunSession> _autoRunSessions = [];
    private readonly Dictionary<ShoeEndpoint, BridgeOutboxStatus> _outboxStatuses = [];
    private readonly Dictionary<ShoeEndpoint, PendingNextRoundCountdown> _pendingNextRoundCountdowns = [];
    private readonly HashSet<ShoeEndpoint> _cutCardAutoLockedEndpoints = [];
    private readonly object _cutCardAutoLockGate = new();
    private readonly HashSet<ShoeEndpoint> _resultHoldAutoLockedEndpoints = [];
    private readonly object _resultHoldAutoLockGate = new();
    private readonly HashSet<string> _handledBmsCommandIds = [];
    private bool _autoRunTickInProgress;
    private bool _outboxStatusRefreshInProgress;

    private TextBox? txtBmsUrl;
    private TextBox? txtBmsToken;
    private NumericUpDown? numTotalBetTime;
    private Button? btnApiEdit;
    private Button? btnApiApply;
    private Button? btnApiCancel;
    private Label? lblSelectedPrimary;
    private Label? lblSelectedGameState;
    private Label? lblSelectedSource;
    private Label? lblSelectedOutboxDetail;
    private Label? lblBmsOutboxStatus;
    private Button? btnCopyToken;
    private Button? btnJwtSettings;
    private Button? btnConnectionModeSettings;
    private Label? lblConnectionModeStatus;

    private DataGridView? dgvEndpoints;
    private GroupBox? endpointListGroup;
    private TableLayoutPanel? endpointListLayout;
    private FlowLayoutPanel? endpointListToolbar;
    private ToggleSwitch? chkEndpointEnabled;
    private ToggleSwitch? chkEndpointMock;
    private TextBox? txtDeskName;
    private TextBox? txtSourceDataId;
    private TextBox? txtSourceDataCode;
    private ToggleSwitch? chkEndpointBmsTransmit;
    private TextBox? txtShoeId;
    private TextBox? txtCurrentShoe;
    private TextBox? txtCurrentRound;
    private TableLayoutPanel? connectionDetailPanel;
    private RowStyle? connectionDetailRowStyle;
    private FlowLayoutPanel? comPortRowPanel;
    private TableLayoutPanel? moxaRowsPanel;
    private ComboBox? cbComPort;
    private TextBox? txtMoxaHost;
    private NumericUpDown? numMoxaPort;
    private ComboBox? cbCancelData;
    private ToggleSwitch? chkAutoCutAfterRounds;
    private NumericUpDown? numAutoCutAfterRounds;
    private Button? btnPhysicalLock;
    private Button? btnPhysicalUnlock;
    private Button? btnPhysicalCancelError;
    private Button? btnPhysicalConfirmGameProcess;
    private Button? btnPhysicalCommandAuthorization;
    private Label? lblPhysicalCommandHint;
    private Button? btnEndpointConnect;
    private Button? btnEndpointDisconnect;
    private Label? lblEndpointConnectionHint;
    private Button? btnAutoRun;
    private Button? btnSimCard;
    private Button? btnSimResult;
    private Button? btnSimCutCard;
    private Button? btnMockReset;
    private Button? btnPauseLog;
    private Button? btnClearLog;
    private Button? btnCloseLog;
    private Label? lblLogScope;
    private bool _logPaused;
    private bool _loadingEndpointEditor;
    private bool _apiSettingsEditMode;
    private bool _endpointSettingsEditMode;
    private string _selectedConnectionMode = ShoeConnectionMode.ComPort;
    private Control? mockTestActions;
    private Label? mockTestHint;
    private BaccaratPreviewPanel? previewPanel;
    private ListView? lvLogs;
    private SplitContainer? mainSplit;
    private readonly List<Button> _endpointEditButtons = [];
    private readonly List<Button> _endpointApplyButtons = [];
    private readonly List<Button> _endpointCancelButtons = [];
    private readonly HashSet<string> _publishedStartGames = [];
    private readonly List<UiLogEntry> _uiLogEntries = [];
    private ShoeEndpoint? _logEndpointFilter;

    private const int ProtectedMainContentHeight = 220;
    private const int MinimumEventLogHeight = 90;
    private const int MinimumCardPreviewHeight = 260;
    private const int AutoRunDealDelaySeconds = 2;
    private const int ResultToNextRoundDelaySeconds = 3;
    private const int AutoRunSettleHoldSeconds = ResultToNextRoundDelaySeconds;
    private const int MaxUiLogRows = 1000;
    private const int OutboxPendingWarningThreshold = 50;
    private const string AngelLogTag = "【ANGEL】";
    private const string PhysicalCommandAuthorizationPassword = "a84268426";
    private const string CancelGeneralOption = "00 一般錯誤清除";
    private const string CancelOverdrawUseOption = "01 Overdraw 繼續使用";
    private const string CancelOverdrawDiscardOption = "02 Overdraw 棄牌改用下一張";
    private const string EndpointIdToolTip = "端點ID 是本機內部代號，不代表實體牌盒序號；桌台對應以來源桌碼為主。";
    private const string SourceDataCodeToolTip = "BMS 會依來源桌碼判斷資料屬於哪一張桌；同一台 Bridge 不可重複。";
    private const string SourceDataIdToolTip = "來源資料ID供系統查核使用；可留空，但若填寫不可重複。";
    private const string ConnectionModeToolTip = "整台 Bridge 共用同一種連線方式：Windows COM Port，或直接連 MOXA TCP。若廠商已映射成 COM，建議用 COM Port。";
    private const string ComPortToolTip = "COM Port 是這張桌目前接的序列埠；使用 COM 模式時必須從本機偵測到的清單選擇，Mock 可留空。";
    private const string MoxaHostToolTip = "MOXA IP 是廠商提供的 NPort / MOXA 設備位址；只有連線方式選 MOXA TCP 時需要填。";
    private const string MoxaPortToolTip = "TCP Port 是 MOXA 的序列埠服務 port，常見預設為 4001；請以廠商設定為準。";
    private const string PhysicalCommandAuthorizationToolTip = "正式模式預設不對實體牌盒送命令；輸入工程密碼後才可啟用鎖定、清錯等工程操作。";
    private static readonly TimeSpan OutboxOfflineWarningThreshold = TimeSpan.FromMinutes(1);

    private enum BaccaratAutoRunState
    {
        Idle,
        Countdown,
        DrawPlayer1,
        DrawBanker1,
        DrawPlayer2,
        DrawBanker2,
        DrawPlayer3,
        DrawBanker3,
        Settle,
        ResultHold
    }

    private sealed class BaccaratAutoRunSession
    {
        public BaccaratAutoRunState State { get; set; } = BaccaratAutoRunState.Countdown;
        public DateTimeOffset? CountdownStartedAtUtc { get; set; }
        public DateTimeOffset? NextActionAtUtc { get; set; }
        public DateTimeOffset? ResultStartedAtUtc { get; set; }
        public int CountdownSeconds { get; set; }
        public int IdleTicks { get; set; }
        public int CompletedRounds { get; set; }
        public int CutAfterRounds { get; init; }
    }

    private sealed record ButtonVisualStyle(Color BackColor, Color ForeColor, Color BorderColor);

    private sealed record UiLogEntry(DateTime Timestamp, ShoeEndpoint? Endpoint, string Type, string Message);

    private sealed record DuplicateEndpointField(string Label, string Value, ShoeEndpoint Endpoint);

    private sealed record PendingNextRoundCountdown(long SettledShoe, long SettledRound, DateTimeOffset DueAtUtc);

    private readonly record struct ResultHoldCountdown(bool Active, int TotalMilliseconds, int RemainingMilliseconds);

    private ShoeEndpoint? SelectedEndpoint
    {
        get
        {
            if (dgvEndpoints?.CurrentRow?.Tag is ShoeEndpoint endpoint)
            {
                return endpoint;
            }

            return null;
        }
    }

    /// <summary>
    /// Creates the bridge UI, loads persisted settings, and wires endpoint and API dispatch events.
    /// </summary>
    public Form1()
    {
        InitializeComponent();
        _settings = BridgeSettings.Load();
        _selectedConnectionMode = ShoeConnectionMode.Normalize(_settings.ConnectionMode);
        foreach (ShoeEndpointSettings endpointSettings in _settings.Shoes)
        {
            AddEndpoint(endpointSettings);
        }

        BuildUI();
        ConfigureEventJournal();
        _autoRunTimer.Interval = 250;
        _autoRunTimer.Tick += AutoRunTimer_Tick;
        _outboxStatusTimer.Interval = 2000;
        _outboxStatusTimer.Tick += OutboxStatusTimer_Tick;
        _outboxStatusTimer.Start();
        _nextRoundCountdownTimer.Interval = 250;
        _nextRoundCountdownTimer.Tick += NextRoundCountdownTimer_Tick;
        RegisterBmsEvents();
        _ = EnsureBmsDispatcherStartedAsync(showErrors: false);
        _ = RefreshOutboxStatusesAsync();
        RefreshComPorts();
        RefreshEndpointGrid();
        SelectFirstEndpoint();
        SetEndpointSettingsEditMode(false);
    }

    private void BuildUI()
    {
        Font = new Font("Microsoft JhengHei", 10F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(240, 244, 248);

        Panel headerPanel = new()
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(28, 40, 51),
            Padding = new Padding(15, 10, 15, 8)
        };
        Label title = new()
        {
            Text = "Xtasys橋接應用",
            AutoSize = true,
            Font = new Font("Microsoft JhengHei", 15F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(15, 10)
        };
        Label warning = new()
        {
            Text = "硬體警告：直通線連接時，牌盒端 DB9 公頭第 8、9 腳必須 NC 斷開，避免毀損牌盒或電腦串口。",
            AutoSize = true,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(241, 148, 138),
            Location = new Point(15, 43)
        };
        headerPanel.Controls.Add(title);
        headerPanel.Controls.Add(warning);
        Controls.Add(headerPanel);

        // 建立主工作區 Wrapper 以提供內縮外距 (Padding)
        Panel mainWrapper = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        Controls.Add(mainWrapper);
        mainWrapper.BringToFront();

        TableLayoutPanel topLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        topLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108F));
        topLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        
        topLayout.Controls.Add(BuildServerPanel(), 0, 0);
        topLayout.Controls.Add(BuildEndpointWorkspace(), 0, 1);
        mainWrapper.Controls.Add(topLayout);

        FormClosing += Form1_FormClosing;
        Shown += Form1_Shown;
    }

    private Control BuildServerPanel()
    {
        Panel surface = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1F));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        TableLayoutPanel apiSection = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        apiSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        apiSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Label apiTitle = new()
        {
            Dock = DockStyle.Fill,
            Text = "BMS 事件 API",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(25, 42, 56),
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };

        TableLayoutPanel apiEndpointLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 0),
            Margin = new Padding(0)
        };
        apiEndpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
        apiEndpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        apiEndpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
        apiEndpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
        apiEndpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
        apiEndpointLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        TableLayoutPanel tokenSection = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(12, 0, 12, 0),
            Padding = new Padding(0)
        };
        tokenSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        tokenSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Label tokenTitle = new()
        {
            Dock = DockStyle.Fill,
            Text = "Token",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(25, 42, 56),
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };

        TableLayoutPanel tokenLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 0),
            Margin = new Padding(0)
        };
        tokenLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tokenLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98F));
        tokenLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
        tokenLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        TableLayoutPanel connectionSection = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(12, 0, 12, 0),
            Padding = new Padding(0)
        };
        connectionSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        connectionSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Label connectionTitle = new()
        {
            Dock = DockStyle.Fill,
            Text = "牌盒連線",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(25, 42, 56),
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold),
        };

        TableLayoutPanel connectionLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0)
        };
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
        connectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        TableLayoutPanel outboxSection = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(12, 0, 0, 0),
            Padding = new Padding(0)
        };
        outboxSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        outboxSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Label outboxTitle = new()
        {
            Dock = DockStyle.Fill,
            Text = "傳送狀態",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(25, 42, 56),
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };

        Panel divider1 = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(218, 225, 231),
            Margin = new Padding(8, 4, 0, 4)
        };
        Panel divider2 = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(218, 225, 231),
            Margin = new Padding(0, 4, 0, 4)
        };
        Panel divider3 = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(218, 225, 231),
            Margin = new Padding(0, 4, 0, 4)
        };

        txtBmsUrl = new TextBox { Text = _settings.BmsUrl, Dock = DockStyle.Fill, ReadOnly = true };
        txtBmsToken = new TextBox { Text = _settings.BmsToken, Dock = DockStyle.Fill, UseSystemPasswordChar = true, ReadOnly = true };
        btnJwtSettings = new Button { Text = "JWT設定", Width = 88, Height = 28, Anchor = AnchorStyles.Left };
        btnCopyToken = new Button { Text = "複製", Width = 76, Height = 28, Anchor = AnchorStyles.Left };
        btnConnectionModeSettings = new Button { Text = "設定", Width = 70, Height = 28, Anchor = AnchorStyles.Left };
        btnApiEdit = new Button { Text = "編輯", Width = 68, Height = 28, Anchor = AnchorStyles.Left };
        btnApiApply = new Button { Text = "套用", Width = 68, Height = 28, Anchor = AnchorStyles.Left };
        btnApiCancel = new Button { Text = "取消", Width = 68, Height = 28, Anchor = AnchorStyles.Left };
        StyleButton(btnJwtSettings, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        StyleButton(btnCopyToken, Color.FromArgb(234, 242, 248), Color.FromArgb(27, 79, 114), Color.FromArgb(169, 204, 227));
        StyleButton(btnConnectionModeSettings, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        StyleButton(btnApiEdit, Color.FromArgb(234, 242, 248), Color.FromArgb(27, 79, 114), Color.FromArgb(169, 204, 227));
        StyleButton(btnApiApply, Color.FromArgb(232, 248, 245), Color.FromArgb(14, 98, 81), Color.FromArgb(163, 228, 215));
        StyleButton(btnApiCancel, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        btnJwtSettings.Click += BtnJwtSettings_Click;
        btnCopyToken.Click += BtnCopyToken_Click;
        btnConnectionModeSettings.Click += BtnConnectionModeSettings_Click;
        btnApiEdit.Click += (_, _) => SetApiSettingsEditMode(true);
        btnApiApply.Click += async (_, _) => await ApplyApiSettingsAsync();
        btnApiCancel.Click += (_, _) => CancelApiSettingsEdit();

        lblBmsOutboxStatus = new Label
        {
            Dock = DockStyle.Fill,
            Text = "BMS待送: 0",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 0, 0),
            ForeColor = Color.ForestGreen,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold)
        };
        lblConnectionModeStatus = new Label
        {
            Dock = DockStyle.Fill,
            Text = FormatConnectionModeText(_settings.ConnectionMode),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 0, 0),
            ForeColor = Color.FromArgb(40, 55, 70),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold)
        };
        Label connectionModeLabel = new()
        {
            Dock = DockStyle.Fill,
            Text = "方式",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(40, 55, 70),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Regular)
        };
        SetFieldToolTip(connectionModeLabel, ConnectionModeToolTip);
        SetFieldToolTip(lblConnectionModeStatus, ConnectionModeToolTip);
        SetFieldToolTip(btnConnectionModeSettings, ConnectionModeToolTip);

        apiEndpointLayout.Controls.Add(CreateTopFieldLabel("路徑:"), 0, 0);
        apiEndpointLayout.Controls.Add(txtBmsUrl, 1, 0);
        apiEndpointLayout.Controls.Add(btnApiEdit, 2, 0);
        apiEndpointLayout.Controls.Add(btnApiApply, 3, 0);
        apiEndpointLayout.Controls.Add(btnApiCancel, 4, 0);
        tokenLayout.Controls.Add(txtBmsToken, 0, 0);
        tokenLayout.Controls.Add(btnJwtSettings, 1, 0);
        tokenLayout.Controls.Add(btnCopyToken, 2, 0);

        connectionLayout.Controls.Add(connectionModeLabel, 0, 0);
        connectionLayout.Controls.Add(lblConnectionModeStatus, 1, 0);
        connectionLayout.Controls.Add(btnConnectionModeSettings, 2, 0);
        apiSection.Controls.Add(apiTitle, 0, 0);
        apiSection.Controls.Add(apiEndpointLayout, 0, 1);
        tokenSection.Controls.Add(tokenTitle, 0, 0);
        tokenSection.Controls.Add(tokenLayout, 0, 1);
        connectionSection.Controls.Add(connectionTitle, 0, 0);
        connectionSection.Controls.Add(connectionLayout, 0, 1);
        outboxSection.Controls.Add(outboxTitle, 0, 0);
        outboxSection.Controls.Add(lblBmsOutboxStatus, 0, 1);
        shell.Controls.Add(apiSection, 0, 0);
        shell.Controls.Add(divider1, 1, 0);
        shell.Controls.Add(tokenSection, 2, 0);
        shell.Controls.Add(divider2, 3, 0);
        shell.Controls.Add(connectionSection, 4, 0);
        shell.Controls.Add(divider3, 5, 0);
        shell.Controls.Add(outboxSection, 6, 0);
        surface.Controls.Add(shell);
        TryRefreshGeneratedToken(showError: false);
        UpdateApiUiState();
        return surface;
    }

    private Control BuildEndpointWorkspace()
    {
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 7,
            BackColor = Color.FromArgb(189, 195, 199) // 顯眼的分割線背景顏色
        };
        split.Panel1.BackColor = Color.FromArgb(240, 244, 248); // 恢復左側面板背景色
        split.Panel2.BackColor = Color.FromArgb(240, 244, 248); // 恢復右側面板背景色
        split.Resize += (_, _) =>
        {
            const int panel1Min = 520;
            const int panel2Min = 560;
            if (split.Width < panel1Min + panel2Min + split.SplitterWidth)
            {
                return;
            }

            int desiredDetailWidth = split.Width >= 1500 ? 760 : 560;
            int maxDistance = split.Width - panel2Min;
            int desiredDistance = split.Width - desiredDetailWidth;
            split.SplitterDistance = Math.Min(Math.Max(desiredDistance, panel1Min), maxDistance);
        };

        split.Panel1.Controls.Add(BuildEndpointListWithLogPanel());
        split.Panel2.Controls.Add(BuildSelectedEndpointPanel());
        return split;
    }

    private Control BuildEndpointListWithLogPanel()
    {
        mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 7,
            BackColor = Color.FromArgb(189, 195, 199)
        };
        mainSplit.Panel1.BackColor = Color.FromArgb(240, 244, 248);
        mainSplit.Panel2.BackColor = Color.FromArgb(240, 244, 248);
        mainSplit.Resize += (_, _) => ApplyMainSplitterBounds();
        mainSplit.SplitterMoving += MainSplit_SplitterMoving;
        mainSplit.SplitterMoved += (_, _) => ApplyMainSplitterBounds();

        mainSplit.Panel1.Controls.Add(BuildEndpointListPanel());

        Control logPanel = BuildLogPanel();
        logPanel.Dock = DockStyle.Fill;
        mainSplit.Panel2.Controls.Add(logPanel);
        mainSplit.Panel2Collapsed = true;

        return mainSplit;
    }

    private Control BuildEndpointListPanel()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = " 桌台 / 端點清單 ",
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };
        endpointListGroup = group;

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            RowCount = 2,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        endpointListLayout = layout;

        dgvEndpoints = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(220, 224, 230),
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            BackgroundColor = Color.White,
            RowTemplate = { Height = 28 }
        };

        // Alternating row styling (zebra)
        dgvEndpoints.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 250);
        
        // Header styling
        dgvEndpoints.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(41, 128, 185); // Modern blue
        dgvEndpoints.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        dgvEndpoints.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);
        dgvEndpoints.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        dgvEndpoints.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        dgvEndpoints.ColumnHeadersHeight = 32;

        // Default cell styling
        dgvEndpoints.DefaultCellStyle.SelectionBackColor = Color.FromArgb(52, 152, 219);
        dgvEndpoints.DefaultCellStyle.SelectionForeColor = Color.White;
        dgvEndpoints.DefaultCellStyle.BackColor = Color.White;
        dgvEndpoints.DefaultCellStyle.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Regular);
        dgvEndpoints.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

        dgvEndpoints.Columns.Add("enabled", "啟用");
        dgvEndpoints.Columns.Add("bmsTransmit", "傳送");
        dgvEndpoints.Columns.Add("deskName", "桌台");
        dgvEndpoints.Columns.Add("sourceCode", "來源桌碼");
        dgvEndpoints.Columns.Add("deviceId", "端點ID");
        dgvEndpoints.Columns.Add("shoe", "靴號");
        dgvEndpoints.Columns.Add("round", "局號");
        dgvEndpoints.Columns.Add("comPort", "連線");
        dgvEndpoints.Columns.Add("status", "狀態");
        dgvEndpoints.Columns.Add("error", "錯誤");
        dgvEndpoints.Columns.Add("outbox", "BMS待送");
        DataGridViewButtonColumn eventsColumn = new()
        {
            Name = "events",
            HeaderText = "事件",
            Text = "查看",
            UseColumnTextForButtonValue = true
        };
        dgvEndpoints.Columns.Add(eventsColumn);

        // 確保靴號（12 位整數）和狀態欄不被壓縮到看不清
        dgvEndpoints.Columns["enabled"]!.MinimumWidth = 38;
        dgvEndpoints.Columns["bmsTransmit"]!.MinimumWidth = 48;
        dgvEndpoints.Columns["deskName"]!.MinimumWidth = 120;
        dgvEndpoints.Columns["sourceCode"]!.MinimumWidth = 110;
        dgvEndpoints.Columns["deviceId"]!.MinimumWidth = 70;
        dgvEndpoints.Columns["shoe"]!.MinimumWidth = 115;
        dgvEndpoints.Columns["round"]!.MinimumWidth = 42;
        dgvEndpoints.Columns["comPort"]!.MinimumWidth = 48;
        dgvEndpoints.Columns["status"]!.MinimumWidth = 80;
        dgvEndpoints.Columns["error"]!.MinimumWidth = 90;
        dgvEndpoints.Columns["outbox"]!.MinimumWidth = 95;
        dgvEndpoints.Columns["events"]!.MinimumWidth = 70;
        dgvEndpoints.Columns["sourceCode"]!.ToolTipText = SourceDataCodeToolTip;
        dgvEndpoints.Columns["sourceCode"]!.HeaderCell.ToolTipText = SourceDataCodeToolTip;
        dgvEndpoints.Columns["deviceId"]!.ToolTipText = EndpointIdToolTip;
        dgvEndpoints.Columns["deviceId"]!.HeaderCell.ToolTipText = EndpointIdToolTip;
        dgvEndpoints.Columns["comPort"]!.ToolTipText = ConnectionModeToolTip;
        dgvEndpoints.Columns["comPort"]!.HeaderCell.ToolTipText = ConnectionModeToolTip;

        dgvEndpoints.SelectionChanged += (_, _) => LoadSelectedEndpointIntoEditor();
        dgvEndpoints.CellContentClick += DgvEndpoints_CellContentClick;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };
        endpointListToolbar = buttons;
        Button btnAdd = new() { Text = "新增", Width = 70, Height = 28 };
        Button btnRemove = new() { Text = "移除", Width = 70, Height = 28 };
        Button btnRefresh = new() { Text = "刷新 COM", Width = 90, Height = 28 };

        StyleButton(btnAdd, Color.FromArgb(232, 248, 245), Color.FromArgb(14, 98, 81), Color.FromArgb(163, 228, 215));
        StyleButton(btnRemove, Color.FromArgb(253, 242, 233), Color.FromArgb(126, 81, 9), Color.FromArgb(245, 203, 167));
        StyleButton(btnRefresh, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));

        btnAdd.Click += (_, _) => AddNewEndpoint();
        btnRemove.Click += (_, _) => RemoveSelectedEndpoint();
        btnRefresh.Click += (_, _) => RefreshComPorts();

        buttons.Controls.Add(btnAdd);
        buttons.Controls.Add(btnRemove);
        buttons.Controls.Add(btnRefresh);

        layout.Controls.Add(dgvEndpoints, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildSelectedEndpointPanel()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = " 選取桌台 ",
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        layout.Controls.Add(BuildSelectedStatusPanel(), 0, 0);

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold),
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(132, 36),
            Padding = new Point(12, 6)
        };
        tabs.DrawItem += EndpointTabs_DrawItem;
        tabs.TabPages.Add(BuildLivePreviewTab());
        tabs.TabPages.Add(BuildConnectionSettingsTab());
        tabs.TabPages.Add(BuildBmsMappingTab());
        layout.Controls.Add(tabs, 0, 1);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildSelectedStatusPanel()
    {
        Panel frame = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10, 8, 10, 8)
        };

        TableLayoutPanel lines = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.White,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        lines.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        lines.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        lines.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        lines.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        lines.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

        lblSelectedPrimary = CreateSelectedStatusLine("未選取桌台", FontStyle.Bold, Color.FromArgb(35, 55, 72));
        lblSelectedGameState = CreateSelectedStatusLine(string.Empty, FontStyle.Regular, Color.FromArgb(60, 75, 90));
        lblSelectedSource = CreateSelectedStatusLine(string.Empty, FontStyle.Regular, Color.FromArgb(60, 75, 90));
        lblSelectedOutboxDetail = CreateSelectedStatusLine(string.Empty, FontStyle.Bold, Color.FromArgb(40, 55, 70));

        lines.Controls.Add(lblSelectedPrimary, 0, 0);
        lines.Controls.Add(lblSelectedGameState, 0, 1);
        lines.Controls.Add(lblSelectedSource, 0, 2);
        lines.Controls.Add(lblSelectedOutboxDetail, 0, 3);

        frame.Controls.Add(lines);
        return frame;
    }

    private static Label CreateSelectedStatusLine(string text, FontStyle style, Color color)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = color,
            Font = new Font("Microsoft JhengHei", 9F, style),
            Margin = new Padding(0)
        };
    }

    private static void EndpointTabs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
        {
            return;
        }

        bool selected = e.Index == tabs.SelectedIndex;
        Rectangle bounds = e.Bounds;
        bounds.Inflate(-2, -2);

        Color backColor = selected ? Color.FromArgb(41, 128, 185) : Color.FromArgb(226, 234, 241);
        Color borderColor = selected ? Color.FromArgb(27, 79, 114) : Color.FromArgb(188, 202, 214);
        Color textColor = selected ? Color.White : Color.FromArgb(35, 55, 72);

        using SolidBrush background = new(backColor);
        using Pen border = new(borderColor);
        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawRectangle(border, bounds);

        TextRenderer.DrawText(
            e.Graphics,
            tabs.TabPages[e.Index].Text,
            tabs.Font,
            bounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private TabPage BuildLivePreviewTab()
    {
        TabPage tab = new("即時牌面")
        {
            BackColor = Color.FromArgb(240, 244, 248),
            Padding = new Padding(8)
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 514F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Control actions = BuildEndpointActionPanel();
        actions.Dock = DockStyle.Fill;
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(BuildLivePreviewPanel(), 0, 1);

        tab.Controls.Add(layout);
        return tab;
    }

    private Control BuildLivePreviewPanel()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = " 即時牌面 ",
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold),
            Padding = new Padding(8),
            Margin = new Padding(0, 8, 0, 0)
        };

        previewPanel = new BaccaratPreviewPanel
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, MinimumCardPreviewHeight)
        };

        group.Controls.Add(previewPanel);
        return group;
    }

    private TabPage BuildConnectionSettingsTab()
    {
        TabPage tab = new("連線設定")
        {
            BackColor = Color.FromArgb(240, 244, 248),
            Padding = new Padding(8),
            AutoScroll = true
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(240, 244, 248)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 398F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
        layout.Controls.Add(BuildEndpointEditorPanel(), 0, 0);
        layout.Controls.Add(BuildSettingsSpacer(), 0, 1);
        layout.Controls.Add(BuildConnectionActionPanel(), 0, 2);

        tab.Controls.Add(layout);
        return tab;
    }

    private static Control BuildSettingsSpacer()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(240, 244, 248),
            Margin = new Padding(0)
        };
    }

    private TabPage BuildBmsMappingTab()
    {
        TabPage tab = new("BMS 對應")
        {
            BackColor = Color.FromArgb(240, 244, 248),
            Padding = new Padding(8)
        };

        TableLayoutPanel mapping = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(8),
            BackColor = Color.White
        };
        mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
        mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
        mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
        mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

        txtSourceDataId = new TextBox { Dock = DockStyle.Fill };
        txtSourceDataCode = new TextBox { Dock = DockStyle.Fill };
        chkEndpointBmsTransmit = new ToggleSwitch
        {
            Text = "自動傳送",
            Width = 130,
            Height = 28,
            Anchor = AnchorStyles.Left
        };

        Button btnCopySourceId = new() { Text = "複製", Dock = DockStyle.Fill };
        Button btnCopySourceCode = new() { Text = "複製", Dock = DockStyle.Fill };
        Button btnEdit = CreateEndpointEditButton();
        Button btnApply = CreateApplyButton();
        Button btnCancel = CreateEndpointCancelButton();
        StyleButton(btnCopySourceId, Color.FromArgb(234, 242, 248), Color.FromArgb(27, 79, 114), Color.FromArgb(169, 204, 227));
        StyleButton(btnCopySourceCode, Color.FromArgb(234, 242, 248), Color.FromArgb(27, 79, 114), Color.FromArgb(169, 204, 227));
        btnCopySourceId.Click += (_, _) => CopyTextBoxValue(txtSourceDataId, "來源資料ID");
        btnCopySourceCode.Click += (_, _) => CopyTextBoxValue(txtSourceDataCode, "來源桌碼");
        chkEndpointBmsTransmit.CheckedChanged += (_, _) => ToggleSelectedBmsTransmission();

        Control sourceDataCodeLabel = CreateFieldLabelWithHelp("來源桌碼", SourceDataCodeToolTip);
        SetFieldToolTip(txtSourceDataCode, SourceDataCodeToolTip);
        mapping.Controls.Add(sourceDataCodeLabel, 0, 0);
        mapping.Controls.Add(txtSourceDataCode, 1, 0);
        mapping.Controls.Add(btnCopySourceCode, 2, 0);
        Control sourceDataIdLabel = CreateFieldLabelWithHelp("來源資料ID", SourceDataIdToolTip);
        SetFieldToolTip(txtSourceDataId, SourceDataIdToolTip);
        mapping.Controls.Add(sourceDataIdLabel, 0, 1);
        mapping.Controls.Add(txtSourceDataId, 1, 1);
        mapping.Controls.Add(btnCopySourceId, 2, 1);
        AddActionRow(mapping, 2, "BMS 傳送", chkEndpointBmsTransmit);
        AddActionRow(mapping, 3, "設定管理", btnEdit, btnApply, btnCancel);

        Label hint = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "請確認來源桌碼對應正確桌台；來源資料ID供系統查核使用。",
            ForeColor = Color.FromArgb(90, 105, 120),
            Padding = new Padding(8, 10, 8, 0)
        };
        tab.Controls.Add(hint);
        tab.Controls.Add(mapping);
        mapping.BringToFront();
        return tab;
    }

    private Control BuildEndpointEditorPanel()
    {
        TableLayoutPanel editor = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 8,
            Padding = new Padding(8),
            BackColor = Color.White
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (int i = 0; i < 4; i++)
        {
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        }
        connectionDetailRowStyle = new RowStyle(SizeType.Absolute, 34F);
        editor.RowStyles.Add(connectionDetailRowStyle);
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

        chkEndpointEnabled = new ToggleSwitch
        {
            Text = "",
            Width = 64,
            Height = 28,
            Anchor = AnchorStyles.Left
        };
        txtDeskName = new TextBox { Dock = DockStyle.Fill };
        txtShoeId = new TextBox { Dock = DockStyle.Fill };
        txtCurrentShoe = new TextBox { Dock = DockStyle.Fill };
        txtCurrentRound = new TextBox { Dock = DockStyle.Fill };
        cbComPort = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        txtMoxaHost = new TextBox { Dock = DockStyle.Fill };
        numMoxaPort = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = 4001,
            Width = 90,
            Anchor = AnchorStyles.Left
        };
        numTotalBetTime = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 120,
            Width = 80,
            Anchor = AnchorStyles.Left
        };
        connectionDetailPanel = BuildConnectionDetailPanel();

        AddEditorRow(editor, 0, "桌台名稱", txtDeskName);
        AddEditorRow(editor, 1, "端點ID", txtShoeId, EndpointIdToolTip);
        AddEditorRow(editor, 2, "BMS靴號", txtCurrentShoe);
        AddEditorRow(editor, 3, "局號", txtCurrentRound);
        AddEditorRow(editor, 4, "連線資料", connectionDetailPanel, ConnectionModeToolTip);
        AddEditorRow(editor, 5, "下注秒數", numTotalBetTime);
        AddEditorRow(editor, 6, "啟用", chkEndpointEnabled);

        Button btnEdit = CreateEndpointEditButton();
        Button btnApply = CreateApplyButton();
        Button btnCancel = CreateEndpointCancelButton();
        AddActionRow(editor, 7, "設定管理", btnEdit, btnApply, btnCancel);

        return editor;
    }

    private TableLayoutPanel BuildConnectionDetailPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        comPortRowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 3, 0, 0)
        };
        Control comLabel = CreateInlineFieldLabel("COM Port", ComPortToolTip);
        cbComPort!.Width = 420;
        comPortRowPanel.Controls.Add(comLabel);
        comPortRowPanel.Controls.Add(cbComPort);

        moxaRowsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        moxaRowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        moxaRowsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        moxaRowsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        moxaRowsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        moxaRowsPanel.Controls.Add(CreateInlineFieldLabel("MOXA IP", MoxaHostToolTip), 0, 0);
        moxaRowsPanel.Controls.Add(txtMoxaHost!, 1, 0);
        moxaRowsPanel.Controls.Add(CreateInlineFieldLabel("TCP Port", MoxaPortToolTip), 0, 1);
        moxaRowsPanel.Controls.Add(numMoxaPort!, 1, 1);

        panel.Controls.Add(comPortRowPanel, 0, 0);
        panel.Controls.Add(moxaRowsPanel, 0, 0);
        return panel;
    }

    private void AddEditorRow(TableLayoutPanel editor, int row, string label, Control input, string? tooltip = null)
    {
        Control fieldLabel = CreateFieldLabelWithHelp(label, tooltip);
        SetFieldToolTip(input, tooltip);
        editor.Controls.Add(fieldLabel, 0, row);
        editor.Controls.Add(input, 1, row);
        editor.SetColumnSpan(input, 3);
    }

    private void AddActionRow(TableLayoutPanel table, int row, string label, params Control[] controls)
    {
        AddActionRow(table, row, label, null, controls);
    }

    private void AddActionRow(TableLayoutPanel table, int row, string label, string? tooltip, params Control[] controls)
    {
        Control fieldLabel = CreateFieldLabelWithHelp(label, tooltip);
        table.Controls.Add(fieldLabel, 0, row);
        FlowLayoutPanel line = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        foreach (Control control in controls)
        {
            SetFieldToolTip(control, tooltip);
            if (control is Button)
            {
                control.Margin = new Padding(0, 0, 8, 0);
            }

            line.Controls.Add(control);
        }

        table.Controls.Add(line, 1, row);
        table.SetColumnSpan(line, 3);
    }

    private Control BuildConnectionActionPanel()
    {
        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 4),
            BackColor = Color.FromArgb(248, 251, 253)
        };

        btnEndpointConnect = new Button { Text = "連線", Width = 70, Height = 28 };
        btnEndpointDisconnect = new Button { Text = "斷線", Width = 70, Height = 28 };
        StyleButton(btnEndpointConnect, Color.FromArgb(212, 239, 223), Color.FromArgb(27, 94, 32), Color.FromArgb(169, 223, 191));
        StyleButton(btnEndpointDisconnect, Color.FromArgb(249, 213, 213), Color.FromArgb(183, 28, 28), Color.FromArgb(245, 183, 183));
        btnEndpointConnect.Click += (_, _) => ConnectSelectedEndpoint();
        btnEndpointDisconnect.Click += (_, _) => DisconnectSelectedEndpoint();
        lblEndpointConnectionHint = new Label
        {
            AutoSize = true,
            Height = 28,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Color.FromArgb(90, 105, 120)
        };

        actions.Controls.Add(CreateSectionLabel("端點連線", 90));
        actions.Controls.Add(btnEndpointConnect);
        actions.Controls.Add(btnEndpointDisconnect);
        actions.Controls.Add(lblEndpointConnectionHint);

        return actions;
    }

    private Button CreateEndpointEditButton()
    {
        Button btnEdit = new() { Text = "編輯設定", Width = 92, Height = 30 };
        StyleButton(btnEdit, Color.FromArgb(234, 242, 248), Color.FromArgb(27, 79, 114), Color.FromArgb(169, 204, 227));
        btnEdit.Click += (_, _) => SetEndpointSettingsEditMode(true);
        _endpointEditButtons.Add(btnEdit);
        return btnEdit;
    }

    private Button CreateApplyButton()
    {
        Button btnApply = new() { Text = "套用", Width = 86, Height = 30 };
        StyleButton(btnApply, Color.FromArgb(232, 248, 245), Color.FromArgb(14, 98, 81), Color.FromArgb(163, 228, 215));
        btnApply.Click += (_, _) =>
        {
            if (ApplySelectedEndpointEditor())
            {
                SetEndpointSettingsEditMode(false);
            }
        };
        _endpointApplyButtons.Add(btnApply);
        return btnApply;
    }

    private Button CreateEndpointCancelButton()
    {
        Button btnCancel = new() { Text = "取消", Width = 86, Height = 30 };
        StyleButton(btnCancel, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        btnCancel.Click += (_, _) => CancelEndpointSettingsEdit();
        _endpointCancelButtons.Add(btnCancel);
        return btnCancel;
    }

    private void SetFieldToolTip(Control? control, string? tooltip)
    {
        if (control == null || string.IsNullOrWhiteSpace(tooltip))
        {
            return;
        }

        _fieldToolTip.SetToolTip(control, tooltip);
    }

    private Control CreateFieldLabelWithHelp(string text, string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return CreateFieldLabel(text);
        }

        FlowLayoutPanel labelPanel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 7, 0, 0)
        };

        Label label = new()
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 55, 70),
            Margin = new Padding(0)
        };

        Label help = new()
        {
            Text = "?",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei", 9F, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            Cursor = Cursors.Help,
            Margin = new Padding(2, 0, 0, 0)
        };

        SetFieldToolTip(labelPanel, tooltip);
        SetFieldToolTip(label, tooltip);
        SetFieldToolTip(help, tooltip);
        labelPanel.Controls.Add(label);
        labelPanel.Controls.Add(help);
        return labelPanel;
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 55, 70)
        };
    }

    private static Label CreateSectionLabel(string text, int width)
    {
        return new Label
        {
            Text = text,
            Width = width,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(40, 55, 70),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold)
        };
    }

    private Control CreateInlineFieldLabel(string text, string? tooltip)
    {
        FlowLayoutPanel labelPanel = new()
        {
            Width = 88,
            Height = 28,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 5, 0, 0)
        };

        Label label = new()
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 55, 70),
            Margin = new Padding(0)
        };

        labelPanel.Controls.Add(label);
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            Label help = new()
            {
                Text = "?",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft JhengHei", 9F, FontStyle.Bold),
                ForeColor = Color.Firebrick,
                Cursor = Cursors.Help,
                Margin = new Padding(2, 0, 0, 0)
            };
            SetFieldToolTip(labelPanel, tooltip);
            SetFieldToolTip(label, tooltip);
            SetFieldToolTip(help, tooltip);
            labelPanel.Controls.Add(help);
        }

        return labelPanel;
    }

    private void CopyTextBoxValue(TextBox? textBox, string fieldName)
    {
        if (textBox == null || string.IsNullOrWhiteSpace(textBox.Text))
        {
            return;
        }

        Clipboard.SetText(textBox.Text);
        AppendLog(SelectedEndpoint, "SYS", $"{fieldName} 已複製到剪貼簿。");
    }

    private void TogglePhysicalCommandAuthorization()
    {
        if (!_settings.AllowPhysicalShoeCommands)
        {
            if (!ValidatePhysicalCommandAuthorizationPassword())
            {
                return;
            }

            _settings.AllowPhysicalShoeCommands = true;
            AppendLog(SelectedEndpoint, "SYS", "工程授權已啟用，實體牌盒工程命令可送出。");
        }
        else
        {
            _settings.AllowPhysicalShoeCommands = false;
            ClearPhysicalCommandAutoLockTracking();
            AppendLog(SelectedEndpoint, "SYS", "工程授權已關閉，正式模式改為唯讀；若牌盒仍鎖定，請由牌盒本機流程處理。");
        }

        _settings.Save();
        if (SelectedEndpoint is { } endpoint)
        {
            RefreshSelectedEndpointView(endpoint);
        }
    }

    private bool ValidatePhysicalCommandAuthorizationPassword()
    {
        using Form dialog = new()
        {
            Text = "工程命令授權",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(460, 176),
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Regular)
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

        Label warning = new()
        {
            Dock = DockStyle.Fill,
            Text = "啟用後可對實體牌盒送鎖定、解鎖、清錯與流程確認等工程命令。此功能只供工程測試或現場授權需求。",
            ForeColor = Color.Firebrick
        };
        layout.Controls.Add(warning, 0, 0);
        layout.SetColumnSpan(warning, 2);

        Label passwordLabel = new()
        {
            Dock = DockStyle.Fill,
            Text = "工程密碼",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold)
        };
        TextBox passwordBox = new()
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true
        };
        layout.Controls.Add(passwordLabel, 0, 1);
        layout.Controls.Add(passwordBox, 1, 1);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        Button ok = new() { Text = "啟用", Width = 86, Height = 30, DialogResult = DialogResult.OK };
        Button cancel = new() { Text = "取消", Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
        StyleButton(ok, Color.FromArgb(253, 235, 230), Color.FromArgb(176, 58, 46), Color.FromArgb(245, 183, 177));
        StyleButton(cancel, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        if (passwordBox.Text == PhysicalCommandAuthorizationPassword)
        {
            return true;
        }

        MessageBox.Show("工程密碼錯誤，未啟用牌盒命令授權。", "授權失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        AppendLog(SelectedEndpoint, "WARN", "工程命令授權密碼錯誤。");
        return false;
    }

    private bool CanSendShoeCommand(ShoeEndpoint endpoint, string action)
    {
        if (endpoint.MockMode || _settings.AllowPhysicalShoeCommands)
        {
            return true;
        }

        AppendLog(endpoint, "WARN", $"{action}未送出：正式模式唯讀，未啟用工程授權。");
        return false;
    }

    private void ClearPhysicalCommandAutoLockTracking()
    {
        lock (_cutCardAutoLockGate)
        {
            _cutCardAutoLockedEndpoints.Clear();
        }

        lock (_resultHoldAutoLockGate)
        {
            _resultHoldAutoLockedEndpoints.Clear();
        }
    }

    private void SetEndpointSettingsEditMode(bool editMode)
    {
        _endpointSettingsEditMode = editMode;

        SetTextBoxEditable(txtDeskName, editMode);
        SetTextBoxEditable(txtShoeId, editMode);
        SetTextBoxEditable(txtCurrentShoe, editMode);
        SetTextBoxEditable(txtCurrentRound, editMode);
        SetTextBoxEditable(txtSourceDataCode, editMode);
        SetTextBoxEditable(txtSourceDataId, editMode);

        if (cbComPort != null) cbComPort.Enabled = editMode;
        SetTextBoxEditable(txtMoxaHost, editMode);
        if (numMoxaPort != null) numMoxaPort.Enabled = editMode;
        if (numTotalBetTime != null) numTotalBetTime.Enabled = editMode;
        if (chkEndpointEnabled != null) chkEndpointEnabled.Enabled = editMode;
        if (chkEndpointBmsTransmit != null) chkEndpointBmsTransmit.Enabled = editMode;

        foreach (Button button in _endpointEditButtons)
        {
            button.Visible = !editMode;
        }

        foreach (Button button in _endpointApplyButtons)
        {
            button.Visible = editMode;
        }

        foreach (Button button in _endpointCancelButtons)
        {
            button.Visible = editMode;
        }

        RefreshConnectionSettingInputs();
        RefreshEndpointConnectionControls(SelectedEndpoint);
    }

    private void CancelEndpointSettingsEdit()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint != null)
        {
            LoadSelectedEndpointIntoEditor();
        }

        SetEndpointSettingsEditMode(false);
        AppendLog(endpoint, "SYS", "端點設定編輯已取消。");
    }

    private void RefreshConnectionSettingInputs()
    {
        bool editMode = _endpointSettingsEditMode;
        bool moxaMode = IsSelectedConnectionModeMoxaTcp();

        if (connectionDetailRowStyle != null)
        {
            connectionDetailRowStyle.Height = moxaMode ? 70F : 34F;
        }

        if (comPortRowPanel != null)
        {
            comPortRowPanel.Visible = !moxaMode;
            if (!moxaMode)
            {
                comPortRowPanel.BringToFront();
            }
        }

        if (moxaRowsPanel != null)
        {
            moxaRowsPanel.Visible = moxaMode;
            if (moxaMode)
            {
                moxaRowsPanel.BringToFront();
            }
        }

        if (cbComPort != null)
        {
            cbComPort.Enabled = editMode && !moxaMode;
        }

        SetTextBoxEditable(txtMoxaHost, editMode && moxaMode);

        if (numMoxaPort != null)
        {
            numMoxaPort.Enabled = editMode && moxaMode;
        }
    }

    private bool IsSelectedConnectionModeMoxaTcp()
    {
        return string.Equals(GetSelectedConnectionMode(), ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase);
    }

    private string GetSelectedConnectionMode()
    {
        return ShoeConnectionMode.Normalize(_selectedConnectionMode);
    }

    private void SelectConnectionMode(string connectionMode)
    {
        string normalizedMode = ShoeConnectionMode.Normalize(connectionMode);
        _selectedConnectionMode = normalizedMode;
        RefreshConnectionModeUi();
        RefreshConnectionSettingInputs();
    }

    private void RefreshConnectionModeUi()
    {
        if (lblConnectionModeStatus != null)
        {
            lblConnectionModeStatus.Text = FormatConnectionModeText(_selectedConnectionMode);
            lblConnectionModeStatus.ForeColor = IsSelectedConnectionModeMoxaTcp()
                ? Color.FromArgb(125, 60, 152)
                : Color.FromArgb(40, 55, 70);
        }

        RefreshConnectionSettingInputs();
    }

    private static string FormatConnectionModeText(string connectionMode)
    {
        string normalizedMode = ShoeConnectionMode.Normalize(connectionMode);
        return string.Equals(normalizedMode, ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase)
            ? "MOXA TCP"
            : "COM Port";
    }

    private static void SetTextBoxEditable(TextBox? textBox, bool editable)
    {
        if (textBox == null)
        {
            return;
        }

        textBox.Enabled = editable;
        textBox.ReadOnly = !editable;
        textBox.TabStop = editable;
        textBox.BackColor = editable ? Color.White : Color.FromArgb(248, 250, 252);
        textBox.ForeColor = Color.FromArgb(40, 55, 70);
    }

    private static void StyleButton(Button btn, Color backColor, Color foreColor, Color borderColor)
    {
        btn.Tag = new ButtonVisualStyle(backColor, foreColor, borderColor);
        btn.BackColor = backColor;
        btn.ForeColor = foreColor;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = borderColor;
        btn.FlatAppearance.BorderSize = 1;
        btn.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);
        btn.UseVisualStyleBackColor = false;
        btn.Cursor = Cursors.Hand;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor);
    }

    private static void SetButtonEnabledVisual(Button? btn, bool enabled)
    {
        if (btn == null)
        {
            return;
        }

        btn.Enabled = enabled;
        btn.UseVisualStyleBackColor = false;
        btn.Cursor = enabled ? Cursors.Hand : Cursors.Default;

        if (enabled && btn.Tag is ButtonVisualStyle style)
        {
            btn.BackColor = style.BackColor;
            btn.ForeColor = style.ForeColor;
            btn.FlatAppearance.BorderColor = style.BorderColor;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(style.BackColor);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(style.BackColor);
            return;
        }

        Color disabledBack = Color.FromArgb(236, 239, 241);
        btn.BackColor = disabledBack;
        btn.ForeColor = Color.FromArgb(130, 140, 150);
        btn.FlatAppearance.BorderColor = Color.FromArgb(205, 212, 218);
        btn.FlatAppearance.MouseOverBackColor = disabledBack;
        btn.FlatAppearance.MouseDownBackColor = disabledBack;
    }

    private void RefreshEndpointConnectionControls(ShoeEndpoint? endpoint)
    {
        if (btnEndpointConnect == null || btnEndpointDisconnect == null || lblEndpointConnectionHint == null)
        {
            return;
        }

        if (endpoint == null)
        {
            btnEndpointConnect.Visible = true;
            btnEndpointDisconnect.Visible = false;
            SetButtonEnabledVisual(btnEndpointConnect, false);
            lblEndpointConnectionHint.Text = "請先選取桌台。";
            lblEndpointConnectionHint.ForeColor = Color.FromArgb(90, 105, 120);
            return;
        }

        bool isConnected = endpoint.IsConnected;
        bool hasComPort = !string.IsNullOrWhiteSpace(endpoint.ComPort);
        bool hasMoxaTarget = !string.IsNullOrWhiteSpace(endpoint.MoxaHost) &&
            endpoint.MoxaPort > 0 &&
            endpoint.MoxaPort <= 65535;
        bool canConnect = !_endpointSettingsEditMode &&
            endpoint.Enabled &&
            !isConnected &&
            (endpoint.MockMode ||
                (endpoint.IsMoxaTcpMode ? hasMoxaTarget : hasComPort));

        btnEndpointConnect.Visible = !isConnected;
        btnEndpointDisconnect.Visible = isConnected;
        SetButtonEnabledVisual(btnEndpointConnect, canConnect);
        SetButtonEnabledVisual(btnEndpointDisconnect, isConnected);

        if (isConnected)
        {
            lblEndpointConnectionHint.Text = "已連線；若要修改設定，請先斷線。";
            lblEndpointConnectionHint.ForeColor = Color.FromArgb(90, 105, 120);
        }
        else if (_endpointSettingsEditMode)
        {
            lblEndpointConnectionHint.Text = "請先套用設定，再進行連線。";
            lblEndpointConnectionHint.ForeColor = Color.FromArgb(90, 105, 120);
        }
        else if (!endpoint.Enabled)
        {
            lblEndpointConnectionHint.Text = "此端點未啟用，需啟用後才能連線。";
            lblEndpointConnectionHint.ForeColor = Color.Firebrick;
        }
        else if (endpoint.MockMode)
        {
            lblEndpointConnectionHint.Text = "Mock 模式不使用實體連線設定。";
            lblEndpointConnectionHint.ForeColor = Color.FromArgb(90, 105, 120);
        }
        else if (endpoint.IsMoxaTcpMode && !hasMoxaTarget)
        {
            lblEndpointConnectionHint.Text = "請先按「編輯設定」填寫 MOXA IP / TCP Port，套用後再連線。";
            lblEndpointConnectionHint.ForeColor = Color.Firebrick;
        }
        else if (!endpoint.IsMoxaTcpMode && !hasComPort)
        {
            lblEndpointConnectionHint.Text = "請先按「編輯設定」選擇 COM Port，套用後再連線。";
            lblEndpointConnectionHint.ForeColor = Color.Firebrick;
        }
        else
        {
            lblEndpointConnectionHint.Text = "設定套用後再連線。";
            lblEndpointConnectionHint.ForeColor = Color.FromArgb(90, 105, 120);
        }
    }

    private Control BuildEndpointActionPanel()
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
            BackColor = Color.White
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 204F));

        Button btnNewShoe = new() { Text = "新靴", Width = 58, Height = 28 };
        btnPhysicalLock = new Button { Text = "鎖定", Width = 58, Height = 28 };
        btnPhysicalUnlock = new Button { Text = "解鎖", Width = 58, Height = 28 };
        cbCancelData = new ComboBox { Width = 142, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList };
        cbCancelData.Items.AddRange([CancelGeneralOption, CancelOverdrawUseOption, CancelOverdrawDiscardOption]);
        cbCancelData.SelectedIndex = 0;
        btnPhysicalCancelError = new Button { Text = "清除錯誤", Width = 96, Height = 28 };
        btnPhysicalConfirmGameProcess = new Button { Text = "流程確認", Width = 96, Height = 28 };
        btnPhysicalCommandAuthorization = new Button { Text = "啟用命令", Width = 96, Height = 28 };
        
        chkEndpointMock = new ToggleSwitch { Text = "", Width = 58, Height = 28 };
        btnSimCard = new Button { Text = "模擬發牌", Width = 96, Height = 28 };
        btnSimResult = new Button { Text = "模擬結算", Width = 96, Height = 28 };
        btnSimCutCard = new Button { Text = "模擬切牌", Width = 96, Height = 28 };
        btnAutoRun = new Button { Text = "自動跑局", Width = 96, Height = 28 };
        btnMockReset = new Button { Text = "重置測試", Width = 96, Height = 28 };
        chkAutoCutAfterRounds = new ToggleSwitch { Text = "", Width = 58, Height = 28 };
        numAutoCutAfterRounds = new NumericUpDown
        {
            Width = 60,
            Height = 28,
            Minimum = 1,
            Maximum = 999,
            Value = 10,
            TextAlign = HorizontalAlignment.Center
        };
        Label autoCutHint = new()
        {
            Text = "局後切牌",
            AutoSize = true,
            Height = 28,
            Padding = new Padding(2, 6, 0, 0),
            ForeColor = Color.FromArgb(90, 105, 120)
        };

        // Apply visual styling accents
        StyleButton(btnPhysicalUnlock, Color.FromArgb(212, 239, 223), Color.FromArgb(27, 94, 32), Color.FromArgb(169, 223, 191));
        
        StyleButton(btnPhysicalLock, Color.FromArgb(249, 213, 213), Color.FromArgb(183, 28, 28), Color.FromArgb(245, 183, 183));
        StyleButton(btnNewShoe, Color.FromArgb(254, 249, 231), Color.FromArgb(125, 102, 8), Color.FromArgb(249, 231, 159));
        
        StyleButton(btnPhysicalCancelError, Color.FromArgb(235, 222, 240), Color.FromArgb(74, 20, 140), Color.FromArgb(210, 180, 222));
        StyleButton(btnPhysicalConfirmGameProcess, Color.FromArgb(232, 246, 253), Color.FromArgb(21, 67, 96), Color.FromArgb(169, 204, 227));
        StyleButton(btnPhysicalCommandAuthorization, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        
        StyleButton(btnSimCard, Color.FromArgb(214, 234, 248), Color.FromArgb(21, 67, 96), Color.FromArgb(169, 204, 227));
        StyleButton(btnSimResult, Color.FromArgb(214, 234, 248), Color.FromArgb(21, 67, 96), Color.FromArgb(169, 204, 227));
        StyleButton(btnSimCutCard, Color.FromArgb(254, 249, 231), Color.FromArgb(125, 102, 8), Color.FromArgb(249, 231, 159));
        StyleButton(btnAutoRun, Color.FromArgb(214, 234, 248), Color.FromArgb(21, 67, 96), Color.FromArgb(169, 204, 227));
        StyleButton(btnMockReset, Color.FromArgb(234, 242, 248), Color.FromArgb(40, 55, 70), Color.FromArgb(174, 182, 191));

        btnNewShoe.Click += async (_, _) => await StartNewShoeForSelectedEndpointAsync();
        btnPhysicalLock.Click += async (_, _) => await SendSelectedLockAsync(true);
        btnPhysicalUnlock.Click += async (_, _) => await SendSelectedLockAsync(false);
        btnPhysicalCancelError.Click += async (_, _) => await SendSelectedCancelErrorAsync();
        btnPhysicalConfirmGameProcess.Click += async (_, _) => await SendSelectedGameProcessConfirmationAsync();
        btnPhysicalCommandAuthorization.Click += (_, _) => TogglePhysicalCommandAuthorization();
        chkEndpointMock.CheckedChanged += (_, _) => ToggleSelectedMockMode();
        btnSimCard.Click += (_, _) => SimulateSelectedCard();
        btnSimResult.Click += (_, _) => SimulateSelectedResult();
        btnSimCutCard.Click += (_, _) => SimulateSelectedCutCard();
        btnAutoRun.Click += (_, _) => ToggleAutoRun();
        btnMockReset.Click += (_, _) => ResetSelectedMockTest();
        chkAutoCutAfterRounds.CheckedChanged += (_, _) => RefreshAutoCutAfterRoundsControls();

        GroupBox shoeGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = " 換靴確認（Bridge / BMS） ",
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.FromArgb(255, 253, 245)
        };

        TableLayoutPanel shoeRows = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0),
            BackColor = Color.FromArgb(255, 253, 245)
        };
        shoeRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        shoeRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shoeRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        shoeRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        shoeRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));

        Label shoeHint = new()
        {
            Text = "換靴完成後按；更新靴局，必要時解除切牌鎖定。",
            AutoSize = true,
            Height = 28,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Color.FromArgb(90, 105, 120)
        };
        AddActionRow(shoeRows, 0, "靴局切換", btnNewShoe, shoeHint);
        shoeGroup.Controls.Add(shoeRows);

        GroupBox maintenanceGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = " PC 對牌盒請求（非日常） ",
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.FromArgb(250, 252, 253)
        };

        TableLayoutPanel maintenanceRows = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(0),
            BackColor = Color.FromArgb(250, 252, 253)
        };
        maintenanceRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        maintenanceRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        maintenanceRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        maintenanceRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (int i = 0; i < 4; i++)
        {
            maintenanceRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        }

        lblPhysicalCommandHint = new Label
        {
            Text = "正式模式唯讀；未啟用工程授權時不會送牌盒命令。",
            AutoSize = true,
            Height = 28,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Color.FromArgb(90, 105, 120)
        };
        Label gameProcessHint = new()
        {
            Text = "請牌盒確認目前流程；只供現場測試，不會自動開局或換靴。",
            AutoSize = true,
            Height = 28,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Color.FromArgb(90, 105, 120)
        };
        AddActionRow(maintenanceRows, 0, "工程授權", PhysicalCommandAuthorizationToolTip, btnPhysicalCommandAuthorization, lblPhysicalCommandHint);
        AddActionRow(maintenanceRows, 1, "牌盒鎖定", btnPhysicalLock, btnPhysicalUnlock);
        AddActionRow(maintenanceRows, 2, "錯誤清除", cbCancelData, btnPhysicalCancelError);
        AddActionRow(maintenanceRows, 3, "流程確認", btnPhysicalConfirmGameProcess, gameProcessHint);
        maintenanceGroup.Controls.Add(maintenanceRows);

        GroupBox mockGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = " Mock 模擬測試 ",
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold),
            Padding = new Padding(8),
            Margin = new Padding(0),
            BackColor = Color.FromArgb(248, 251, 253)
        };

        TableLayoutPanel mockLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(0),
            BackColor = Color.FromArgb(248, 251, 253)
        };
        mockLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        mockLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mockLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        mockLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (int i = 0; i < 4; i++)
        {
            mockLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        }

        mockTestHint = new Label
        {
            Text = "開啟後才顯示模擬發牌、模擬結算與自動跑局。",
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 105, 120),
            Padding = new Padding(12, 6, 0, 0)
        };
        AddActionRow(mockLayout, 0, "Mock 模式", chkEndpointMock, mockTestHint);

        TableLayoutPanel mockRows = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = Color.FromArgb(248, 251, 253)
        };
        mockRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        mockRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mockRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        mockRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        for (int i = 0; i < 3; i++)
        {
            mockRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        }

        AddActionRow(mockRows, 0, "模擬事件", btnSimCard, btnSimResult);
        AddActionRow(mockRows, 1, "現場流程", btnSimCutCard);
        AddActionRow(mockRows, 2, "自動跑局", btnAutoRun, btnMockReset, chkAutoCutAfterRounds, numAutoCutAfterRounds, autoCutHint);
        mockTestActions = mockRows;

        mockLayout.Controls.Add(mockRows, 0, 1);
        mockLayout.SetColumnSpan(mockRows, 4);
        mockLayout.SetRowSpan(mockRows, 3);
        mockGroup.Controls.Add(mockLayout);

        layout.Controls.Add(shoeGroup, 0, 0);
        layout.Controls.Add(maintenanceGroup, 0, 1);
        layout.Controls.Add(mockGroup, 0, 2);
        return layout;
    }

    private Control BuildLogPanel()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = " 事件日誌 ",
            Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6),
            RowCount = 2,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0)
        };

        btnPauseLog = new Button { Text = "暫停日誌", Width = 90, Height = 28 };
        StyleButton(btnPauseLog, Color.FromArgb(254, 249, 231), Color.FromArgb(125, 102, 8), Color.FromArgb(249, 231, 159));
        btnPauseLog.Click += BtnPauseLog_Click;

        btnClearLog = new Button { Text = "清除日誌", Width = 90, Height = 28 };
        StyleButton(btnClearLog, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        btnClearLog.Click += BtnClearLog_Click;

        btnCloseLog = new Button { Text = "關閉日誌", Width = 90, Height = 28 };
        StyleButton(btnCloseLog, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
        btnCloseLog.Click += (_, _) => HideEventLogPanel();

        lblLogScope = new Label
        {
            Text = "目前事件：--",
            AutoSize = true,
            Height = 28,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Color.FromArgb(40, 55, 70),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold)
        };

        toolbar.Controls.Add(btnPauseLog);
        toolbar.Controls.Add(btnClearLog);
        toolbar.Controls.Add(btnCloseLog);
        toolbar.Controls.Add(lblLogScope);

        lvLogs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Consolas", 9F)
        };
        lvLogs.Columns.Add("Time", 95);
        lvLogs.Columns.Add("Desk", 80);
        lvLogs.Columns.Add("Device", 80);
        lvLogs.Columns.Add("Shoe", 115);
        lvLogs.Columns.Add("Round", 70);
        lvLogs.Columns.Add("Type", 70);
        lvLogs.Columns.Add("Message", 820);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(lvLogs, 0, 1);

        group.Controls.Add(layout);
        return group;
    }

    private void BtnPauseLog_Click(object? sender, EventArgs e)
    {
        if (btnPauseLog == null) return;
        _logPaused = !_logPaused;
        if (_logPaused)
        {
            btnPauseLog.Text = "繼續日誌";
            StyleButton(btnPauseLog, Color.FromArgb(253, 235, 230), Color.FromArgb(176, 58, 46), Color.FromArgb(245, 183, 177));
            AppendLog(null, "SYS", "日誌顯示已暫停。");
        }
        else
        {
            btnPauseLog.Text = "暫停日誌";
            StyleButton(btnPauseLog, Color.FromArgb(254, 249, 231), Color.FromArgb(125, 102, 8), Color.FromArgb(249, 231, 159));
            AppendLog(null, "SYS", "日誌顯示已恢復。");
        }
    }

    private void BtnClearLog_Click(object? sender, EventArgs e)
    {
        if (_logEndpointFilter == null)
        {
            _uiLogEntries.Clear();
            lvLogs?.Items.Clear();
            return;
        }

        _uiLogEntries.RemoveAll(entry => ReferenceEquals(entry.Endpoint, _logEndpointFilter));
        RenderLogEntries();
    }

    private void ShowEndpointLog(ShoeEndpoint endpoint)
    {
        _logEndpointFilter = endpoint;
        if (lblLogScope != null)
        {
            lblLogScope.Text = $"目前事件：{endpoint.DeskName} / {endpoint.ShoeId}";
        }

        if (mainSplit != null && mainSplit.Panel2Collapsed)
        {
            mainSplit.Panel2Collapsed = false;
            PositionEventLogBelowEndpointList();
        }

        RenderLogEntries();
    }

    private bool IsShowingEndpointLog(ShoeEndpoint endpoint)
    {
        return mainSplit != null &&
            !mainSplit.Panel2Collapsed &&
            ReferenceEquals(_logEndpointFilter, endpoint);
    }

    private void HideEventLogPanel()
    {
        _logEndpointFilter = null;
        if (lblLogScope != null)
        {
            lblLogScope.Text = "目前事件：--";
        }

        if (mainSplit != null)
        {
            mainSplit.Panel2Collapsed = true;
        }
    }

    private void RegisterBmsEvents()
    {
        _bmsApiClient.OnLogReceived += msg => AppendLog(null, "API", msg);
    }

    private async void OutboxStatusTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshOutboxStatusesAsync();
    }

    private Task RefreshOutboxStatusesAsync()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(async () => await RefreshOutboxStatusesOnUiAsync()));
            return Task.CompletedTask;
        }

        return RefreshOutboxStatusesOnUiAsync();
    }

    private async Task RefreshOutboxStatusesOnUiAsync()
    {
        if (_outboxStatusRefreshInProgress || _eventJournal == null)
        {
            return;
        }

        _outboxStatusRefreshInProgress = true;
        try
        {
            foreach (ShoeEndpoint endpoint in _endpoints.ToArray())
            {
                BridgeOutboxStatus status = await _eventJournal
                    .GetOutboxStatusAsync(endpoint.SourceDataCode, endpoint.DeviceId)
                    .ConfigureAwait(true);
                _outboxStatuses[endpoint] = status;
            }

            UpdateBmsOutboxStatusLabel();
            foreach (ShoeEndpoint endpoint in _endpoints)
            {
                RefreshEndpointGridRow(endpoint);
            }

            if (SelectedEndpoint is { } selected)
            {
                RefreshSelectedEndpointView(selected);
            }
        }
        catch (Exception ex)
        {
            AppendLog(null, "ERR", $"Outbox 狀態讀取失敗: {ex.Message}");
        }
        finally
        {
            _outboxStatusRefreshInProgress = false;
        }
    }

    private void UpdateBmsOutboxStatusLabel()
    {
        if (lblBmsOutboxStatus == null)
        {
            return;
        }

        int pendingCount = _outboxStatuses.Values.Sum(status => status.PendingCount);
        int failedCount = _outboxStatuses.Values.Sum(status => status.FailedCount);
        bool hasWarning = _outboxStatuses.Values.Any(IsOutboxWarning);
        if (pendingCount <= 0)
        {
            lblBmsOutboxStatus.Text = "BMS待送: 0";
            lblBmsOutboxStatus.ForeColor = Color.ForestGreen;
            return;
        }

        lblBmsOutboxStatus.Text = hasWarning
            ? $"BMS離線警示: 待送 {pendingCount} / 失敗 {failedCount}"
            : $"BMS待送: {pendingCount} / 失敗 {failedCount}";
        lblBmsOutboxStatus.ForeColor = hasWarning ? Color.Firebrick : Color.FromArgb(180, 105, 0);
    }

    private void ConfigureEventJournal()
    {
        try
        {
            _eventJournal = new BridgeEventJournal();
            AppendLog(null, "SYS", $"SQLite 事件 Journal 已啟用: {_eventJournal.DbPath}");
        }
        catch (Exception ex)
        {
            AppendLog(null, "ERR", $"SQLite 事件 Journal 啟用失敗: {ex.Message}");
        }
    }

    private void AddEndpoint(ShoeEndpointSettings settings)
    {
        ShoeEndpoint endpoint = new(settings);
        endpoint.StateChanged += Endpoint_StateChanged;
        endpoint.LogReceived += (ep, type, data) => AppendLog(ep, type, data);
        endpoint.CardDrawn += Endpoint_CardDrawn;
        endpoint.GameResultReceived += Endpoint_GameResultReceived;
        endpoint.ErrorOccurred += Endpoint_ErrorOccurred;
        endpoint.LockStatusChanged += Endpoint_LockStatusChanged;
        endpoint.ErrorCleared += Endpoint_ErrorCleared;
        endpoint.CuttingCardDrawn += Endpoint_CuttingCardDrawn;
        _endpoints.Add(endpoint);
    }

    private void Endpoint_StateChanged(ShoeEndpoint endpoint)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Endpoint_StateChanged(endpoint)));
            return;
        }

        RefreshEndpointGridRow(endpoint);
        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }
    }

    private async void Endpoint_CardDrawn(ShoeEndpoint endpoint, SerialListener.CardInfo card)
    {
        _settings.Save();
        // 'R' Retransmission 是下一局的牌，不代表新局開始，不觸發 StartGame
        if (card.EventCode != 'R' && card.Target == "Player" && card.Index == 1)
        {
            CancelPendingNextRoundCountdown(endpoint);
            if (!await ReleaseResultHoldAutoLockIfNeededAsync(endpoint))
            {
                AppendLog(endpoint, "WARN", "結算後自動鎖定尚未解除，略過下一局第一張牌。");
                return;
            }

            await PublishStartGameIfNeededAsync(endpoint);
        }

        AppendLog(endpoint, "EVENT", $"CardDrawn {endpoint.CurrentShoe}/{endpoint.CurrentRound} {card.Target} #{card.Index} {card.Suit} {card.Value}");
        if (!IsBaccaratCardForBms(card))
        {
            AppendLog(endpoint, "SYS", $"牌盒狀態更新，不送 BMS CardDrawn: {card.EventCode}/{card.Target} #{card.Index}");
            return;
        }

        await PublishBridgeEventAsync("CardDrawn", endpoint, new
        {
            target = card.Target,
            index = card.Index,
            suit = card.Suit,
            value = card.Value
        });
    }

    private static bool IsBaccaratCardForBms(SerialListener.CardInfo card)
    {
        return card.Target == "Player" || card.Target == "Banker";
    }

    private async void Endpoint_GameResultReceived(ShoeEndpoint endpoint, SerialListener.GameResult result)
    {
        _settings.Save();
        AppendLog(endpoint, "EVENT", $"GameResult {endpoint.CurrentShoe}/{endpoint.CurrentRound} {result.Result} / {result.Pair}");
        await PublishBridgeEventAsync("GameResult", endpoint, new
        {
            result = result.Result,
            pair = result.Pair
        });
        ScheduleNextRoundCountdownAfterResult(endpoint);
        _ = LockEndpointForResultHoldAsync(endpoint);
    }

    private async void Endpoint_CuttingCardDrawn(ShoeEndpoint endpoint, SerialListener.CutCardInfo cutCard)
    {
        _settings.Save();
        AppendLog(endpoint, "EVENT", $"CutCardDrawn {endpoint.CurrentShoe}/{endpoint.CurrentRound} - Shoe ending");
        CancelPendingNextRoundCountdown(endpoint);
        StopAutoRun(endpoint);

        await PublishBridgeEventAsync("CutCardDrawn", endpoint, new
        {
            shoeEnding = true,
            rawBytes = cutCard.RawBytes
        });
        await LockEndpointAfterCutCardAsync(endpoint);
    }

    private async void Endpoint_ErrorOccurred(ShoeEndpoint endpoint, SerialListener.ErrorInfo error)
    {
        CancelPendingNextRoundCountdown(endpoint);
        AppendLog(endpoint, "EVENT", $"Error [{error.ErrorCode}] {error.ErrorMessage}");
        await PublishBridgeEventAsync("Error", endpoint, new
        {
            errorCode = error.ErrorCode,
            errorMessage = error.ErrorMessage,
            inErrorMode = error.InErrorMode
        });
    }

    private async void Endpoint_LockStatusChanged(ShoeEndpoint endpoint, bool isLocked)
    {
        AppendLog(endpoint, "EVENT", isLocked ? "LockStatus Locked" : "LockStatus Unlocked");
        await PublishBridgeEventAsync("LockStatus", endpoint, new
        {
            isLocked
        });
    }

    private async void Endpoint_ErrorCleared(ShoeEndpoint endpoint, int errorCode, string errorMessage)
    {
        AppendLog(endpoint, "EVENT", $"ErrorCleared [{errorCode}] {errorMessage}");
        await PublishBridgeEventAsync("ErrorCleared", endpoint, new
        {
            errorCode,
            errorMessage
        });
    }

    private void ScheduleNextRoundCountdownAfterResult(ShoeEndpoint endpoint)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ScheduleNextRoundCountdownAfterResult(endpoint)));
            return;
        }

        if (_autoRunSessions.ContainsKey(endpoint) || endpoint.ShoeEnding)
        {
            return;
        }

        _pendingNextRoundCountdowns[endpoint] = new PendingNextRoundCountdown(
            endpoint.CurrentShoe,
            endpoint.CurrentRound,
            DateTimeOffset.UtcNow.AddSeconds(ResultToNextRoundDelaySeconds));

        if (!_nextRoundCountdownTimer.Enabled)
        {
            _nextRoundCountdownTimer.Start();
        }

        AppendLog(endpoint, "SYS", $"結算後 {ResultToNextRoundDelaySeconds} 秒自動進入下一局倒數。");
    }

    private void CancelPendingNextRoundCountdown(ShoeEndpoint? endpoint = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => CancelPendingNextRoundCountdown(endpoint)));
            return;
        }

        if (endpoint == null)
        {
            _pendingNextRoundCountdowns.Clear();
        }
        else
        {
            _pendingNextRoundCountdowns.Remove(endpoint);
        }

        if (_pendingNextRoundCountdowns.Count == 0)
        {
            _nextRoundCountdownTimer.Stop();
        }
    }

    private async void NextRoundCountdownTimer_Tick(object? sender, EventArgs e)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        foreach ((ShoeEndpoint endpoint, PendingNextRoundCountdown pending) in _pendingNextRoundCountdowns.ToList())
        {
            if (nowUtc < pending.DueAtUtc)
            {
                if (ReferenceEquals(endpoint, SelectedEndpoint))
                {
                    RefreshSelectedEndpointView(endpoint);
                }
                continue;
            }

            _pendingNextRoundCountdowns.Remove(endpoint);

            if (!_endpoints.Contains(endpoint) ||
                endpoint.CurrentShoe != pending.SettledShoe ||
                endpoint.CurrentRound != pending.SettledRound ||
                endpoint.InErrorMode ||
                endpoint.ShoeEnding)
            {
                continue;
            }

            if (!await ReleaseResultHoldAutoLockIfNeededAsync(endpoint) || !endpoint.IsConnected)
            {
                continue;
            }

            await BeginNextRoundCountdownAsync(endpoint, nowUtc, "結算後自動下一局");
        }

        if (_pendingNextRoundCountdowns.Count == 0)
        {
            _nextRoundCountdownTimer.Stop();
        }
    }

    private async Task BeginNextRoundCountdownAsync(ShoeEndpoint endpoint, DateTimeOffset startedAtUtc, string reason)
    {
        endpoint.BeginNextRoundCountdown();
        endpoint.StartBetCountdown(startedAtUtc, GetTotalBetTimeSeconds(endpoint));
        _settings.Save();
        AppendLog(endpoint, "SYS", $"{reason}: {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
        await PublishStartGameIfNeededAsync(endpoint, startedAtUtc);
        RefreshEndpointGridRow(endpoint);
        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }
    }

    private async Task<bool> PublishStartGameIfNeededAsync(ShoeEndpoint endpoint, DateTimeOffset? startTimeOverride = null)
    {
        string key = $"{endpoint.SourceDataCode}:{endpoint.CurrentShoe}:{endpoint.CurrentRound}";
        if (_publishedStartGames.Contains(key))
        {
            return true;
        }

        int totalBetTime = GetTotalBetTimeSeconds(endpoint);
        DateTimeOffset startTime = startTimeOverride ?? DateTimeOffset.UtcNow;
        bool queued = await PublishBridgeEventAsync("StartGame", endpoint, new
        {
            totalBetTime,
            startTime = startTime.ToString("o"),
            bootId = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture),
            groupId = 1
        }, rootFields =>
        {
            rootFields["totalBetTime"] = totalBetTime;
            rootFields["startTime"] = startTime.ToString("o");
        });

        if (queued)
        {
            _publishedStartGames.Add(key);
            AppendLog(endpoint, "EVENT", $"StartGame {endpoint.CurrentShoe}/{endpoint.CurrentRound} TotalBetTime={totalBetTime}");
        }

        return queued;
    }

    private int GetTotalBetTimeSeconds(ShoeEndpoint endpoint)
    {
        int seconds = ReferenceEquals(endpoint, SelectedEndpoint) && numTotalBetTime != null
            ? (int)numTotalBetTime.Value
            : endpoint.TotalBetTimeSeconds;

        return Math.Clamp(seconds, 5, 120);
    }

    private async Task<bool> PublishBridgeEventAsync(string type, ShoeEndpoint endpoint, object data, Action<Dictionary<string, object?>>? configureRoot = null)
    {
        if (!endpoint.BmsTransmitEnabled)
        {
            AppendLog(endpoint, "API", $"BMS 傳送已關閉，略過 {type} {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
            return false;
        }

        Dictionary<string, object?> payload = new()
        {
            ["type"] = type,
            ["source"] = AngelEyeProtocol.SourceName,
            ["sequence"] = Interlocked.Increment(ref _eventSequence),
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["deskName"] = endpoint.DeskName,
            ["sourceDataCode"] = endpoint.SourceDataCode,
            ["shoeId"] = endpoint.ShoeId,
            ["deviceId"] = endpoint.DeviceId,
            ["shoe"] = endpoint.CurrentShoe,
            ["round"] = endpoint.CurrentRound,
            ["roundId"] = endpoint.CurrentRoundId,
            ["shoeRound"] = endpoint.ShoeRound,
            ["state"] = ResolveEventState(type),
            ["data"] = data
        };
        configureRoot?.Invoke(payload);

        if (Guid.TryParse(endpoint.SourceDataId, out Guid sourceDataId) && sourceDataId != Guid.Empty)
        {
            payload["sourceDataId"] = endpoint.SourceDataId;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.ComPort))
        {
            payload["comPort"] = endpoint.ComPort;
        }

        payload["connectionMode"] = endpoint.ConnectionMode;
        if (endpoint.IsMoxaTcpMode)
        {
            payload["moxaHost"] = endpoint.MoxaHost;
            payload["moxaPort"] = endpoint.MoxaPort;
        }

        if (_eventJournal == null)
        {
            AppendLog(endpoint, "ERR", "SQLite 事件 Journal 未啟用，事件未送出。");
            return false;
        }

        try
        {
            long eventId = await _eventJournal.AppendAsync(payload);
            AppendLog(endpoint, "API", $"Outbox queued #{eventId} {type} {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
            _ = RefreshOutboxStatusesAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(endpoint, "ERR", $"SQLite 事件 Journal 寫入失敗，事件未送出: {ex.Message}");
            return false;
        }
    }

    private static string ResolveEventState(string type) => type switch
    {
        "StartGame" => "Countdown",
        "CardDrawn" => "Dealing",
        "GameResult" => "Settled",
        "CutCardDrawn" => "ShoeEnding",
        "Error" => "Error",
        "ErrorCleared" => "Normal",
        "LockStatus" => "Locked",
        _ => "Event"
    };

    private void RefreshEndpointGrid()
    {
        if (dgvEndpoints == null)
        {
            return;
        }

        dgvEndpoints.Rows.Clear();
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            int rowIndex = dgvEndpoints.Rows.Add();
            dgvEndpoints.Rows[rowIndex].Tag = endpoint;
            FillEndpointRow(dgvEndpoints.Rows[rowIndex], endpoint);
        }
    }

    private void RefreshEndpointGridRow(ShoeEndpoint endpoint)
    {
        if (dgvEndpoints == null)
        {
            return;
        }

        foreach (DataGridViewRow row in dgvEndpoints.Rows)
        {
            if (ReferenceEquals(row.Tag, endpoint))
            {
                FillEndpointRow(row, endpoint);
                return;
            }
        }
    }

    private void DgvEndpoints_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (dgvEndpoints == null || e.RowIndex < 0)
        {
            return;
        }

        if (dgvEndpoints.Columns[e.ColumnIndex].Name != "events")
        {
            return;
        }

        if (dgvEndpoints.Rows[e.RowIndex].Tag is not ShoeEndpoint endpoint)
        {
            return;
        }

        if (IsShowingEndpointLog(endpoint))
        {
            HideEventLogPanel();
            return;
        }

        dgvEndpoints.Rows[e.RowIndex].Selected = true;
        dgvEndpoints.CurrentCell = dgvEndpoints.Rows[e.RowIndex].Cells[e.ColumnIndex];
        ShowEndpointLog(endpoint);
    }

    private void FillEndpointRow(DataGridViewRow row, ShoeEndpoint endpoint)
    {
        row.Cells[0].Value = endpoint.Enabled ? "Y" : "N";
        row.Cells[1].Value = endpoint.BmsTransmitEnabled ? "開" : "停";
        row.Cells[1].Style.ForeColor = endpoint.BmsTransmitEnabled ? Color.ForestGreen : Color.Firebrick;
        row.Cells[1].Style.SelectionForeColor = row.Cells[1].Style.ForeColor;
        row.Cells[1].Style.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);
        row.Cells[2].Value = endpoint.DeskName;
        row.Cells[3].Value = endpoint.SourceDataCode;
        row.Cells[4].Value = endpoint.ShoeId;
        row.Cells[5].Value = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture);
        row.Cells[6].Value = endpoint.CurrentRound.ToString(CultureInfo.InvariantCulture);
        row.Cells[7].Value = endpoint.ConnectionDisplay;

        row.Cells[8].Value = endpoint.IsConnected ? "● 已連線" : "● 未連線";
        row.Cells[8].Style.ForeColor = endpoint.IsConnected ? Color.ForestGreen : Color.Firebrick;
        row.Cells[8].Style.SelectionForeColor = row.Cells[8].Style.ForeColor;
        row.Cells[8].Style.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);

        if (endpoint.InErrorMode)
        {
            row.Cells[9].Value = $"{endpoint.ErrorCode}: {endpoint.ErrorMessage}";
            row.Cells[9].Style.ForeColor = Color.Firebrick;
            row.Cells[9].Style.BackColor = Color.FromArgb(254, 237, 222);
        }
        else
        {
            row.Cells[9].Value = "--";
            row.Cells[9].Style.ForeColor = Color.Empty;
            row.Cells[9].Style.BackColor = Color.Empty;
        }
        row.Cells[9].Style.SelectionForeColor = row.Cells[9].Style.ForeColor;
        row.Cells[9].Style.Font = new Font("Microsoft JhengHei", 9.5F, endpoint.InErrorMode ? FontStyle.Bold : FontStyle.Regular);

        BridgeOutboxStatus outboxStatus = GetEndpointOutboxStatus(endpoint);
        Color outboxColor = GetOutboxStatusColor(outboxStatus);
        row.Cells[10].Value = FormatOutboxGridText(outboxStatus);
        row.Cells[10].Style.ForeColor = outboxColor;
        row.Cells[10].Style.SelectionForeColor = outboxColor;
        row.Cells[10].Style.Font = new Font("Microsoft JhengHei", 9.5F, outboxStatus.PendingCount > 0 ? FontStyle.Bold : FontStyle.Regular);
        row.Cells[10].Style.BackColor = IsOutboxWarning(outboxStatus)
            ? Color.FromArgb(255, 235, 238)
            : Color.Empty;

        if (endpoint.ShoeEnding)
        {
            row.Cells[11].Style.ForeColor = Color.DarkOrange;
            row.Cells[11].Style.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Bold);
        }
        else
        {
            row.Cells[11].Style.ForeColor = Color.Empty;
            row.Cells[11].Style.Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Regular);
        }
    }

    private BridgeOutboxStatus GetEndpointOutboxStatus(ShoeEndpoint endpoint)
    {
        return _outboxStatuses.TryGetValue(endpoint, out BridgeOutboxStatus? status)
            ? status
            : BridgeOutboxStatus.Empty;
    }

    private static string FormatOutboxGridText(BridgeOutboxStatus status)
    {
        if (status.PendingCount <= 0)
        {
            return "正常";
        }

        if (status.FailedCount > 0)
        {
            return IsOutboxWarning(status)
                ? $"離線 待{status.PendingCount}"
                : $"待{status.PendingCount}/失{status.FailedCount}";
        }

        return $"待送 {status.PendingCount}";
    }

    private static string FormatOutboxDetailText(BridgeOutboxStatus status)
    {
        if (status.PendingCount <= 0)
        {
            return "BMS待送: 0";
        }

        string retryText = status.MaxRetryCount > 0
            ? $"，最高重試 {status.MaxRetryCount}"
            : string.Empty;
        string errorText = string.IsNullOrWhiteSpace(status.LastError)
            ? string.Empty
            : $"，最後錯誤: {TrimUiText(status.LastError, 90)}";

        return IsOutboxWarning(status)
            ? $"BMS離線警示: 待送 {status.PendingCount} 筆，失敗 {status.FailedCount} 筆{retryText}{errorText}"
            : $"BMS待送: {status.PendingCount} 筆，失敗 {status.FailedCount} 筆{retryText}{errorText}";
    }

    private static Color GetOutboxStatusColor(BridgeOutboxStatus status)
    {
        if (status.PendingCount <= 0)
        {
            return Color.ForestGreen;
        }

        return IsOutboxWarning(status)
            ? Color.Firebrick
            : Color.FromArgb(180, 105, 0);
    }

    private static bool IsOutboxWarning(BridgeOutboxStatus status)
    {
        if (status.PendingCount >= OutboxPendingWarningThreshold)
        {
            return true;
        }

        return status.OldestFailedAttemptUtc.HasValue &&
            DateTime.UtcNow - status.OldestFailedAttemptUtc.Value >= OutboxOfflineWarningThreshold;
    }

    private static string TrimUiText(string text, int maxLength)
    {
        string normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static string ShortId(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= 12 ? trimmed : $"{trimmed[..8]}...";
    }

    private void SelectFirstEndpoint()
    {
        if (dgvEndpoints != null && dgvEndpoints.Rows.Count > 0)
        {
            dgvEndpoints.Rows[0].Selected = true;
            dgvEndpoints.CurrentCell = dgvEndpoints.Rows[0].Cells[1];
            LoadSelectedEndpointIntoEditor();
        }
    }

    private void LoadSelectedEndpointIntoEditor()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        _endpointSettingsEditMode = false;
        _loadingEndpointEditor = true;
        try
        {
            chkEndpointEnabled!.Checked = endpoint.Enabled;
            chkEndpointMock!.Checked = endpoint.MockMode;
            if (chkEndpointBmsTransmit != null)
            {
                chkEndpointBmsTransmit.Checked = endpoint.BmsTransmitEnabled;
            }

            txtDeskName!.Text = endpoint.DeskName;
            txtSourceDataId!.Text = endpoint.SourceDataId;
            txtSourceDataCode!.Text = endpoint.SourceDataCode;
            txtShoeId!.Text = endpoint.ShoeId;
            txtCurrentShoe!.Text = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture);
            txtCurrentRound!.Text = endpoint.CurrentRound.ToString(CultureInfo.InvariantCulture);
            if (numTotalBetTime != null)
            {
                numTotalBetTime.Value = endpoint.TotalBetTimeSeconds;
            }
            RefreshComPorts();
            SelectConnectionMode(_settings.ConnectionMode);
            cbComPort!.Text = endpoint.ComPort;
            txtMoxaHost!.Text = endpoint.MoxaHost;
            if (numMoxaPort != null)
            {
                numMoxaPort.Value = Math.Clamp(endpoint.MoxaPort, (int)numMoxaPort.Minimum, (int)numMoxaPort.Maximum);
            }
        }
        finally
        {
            _loadingEndpointEditor = false;
        }

        RefreshSelectedEndpointView(endpoint);
        SetEndpointSettingsEditMode(false);
        RefreshConnectionSettingInputs();
    }

    private void RefreshSelectedEndpointView(ShoeEndpoint endpoint)
    {
        string bmsTransmitText = endpoint.BmsTransmitEnabled ? "BMS傳送 開" : "BMS傳送 停";
        BridgeOutboxStatus outboxStatus = GetEndpointOutboxStatus(endpoint);
        string shoeEndingText = endpoint.ShoeEnding ? "    切牌已抽出，請停靴 / 換靴" : string.Empty;
        string lastEventText = string.IsNullOrWhiteSpace(endpoint.LastEventText) ? "--" : endpoint.LastEventText;

        lblSelectedPrimary!.Text = $"桌台  {endpoint.DeskName}    桌碼  {endpoint.SourceDataCode}";
        lblSelectedGameState!.Text = $"靴號  {endpoint.CurrentShoe}    局號  {endpoint.CurrentRound}    狀態  {endpoint.StatusText}    {bmsTransmitText}{shoeEndingText}";
        lblSelectedSource!.Text = $"端點  {endpoint.ShoeId}    來源ID  {ShortId(endpoint.SourceDataId)}    最後事件  {lastEventText}";
        lblSelectedOutboxDetail!.Text = FormatOutboxDetailText(outboxStatus);
        lblSelectedOutboxDetail.ForeColor = IsOutboxWarning(outboxStatus) ? Color.Firebrick : Color.FromArgb(40, 55, 70);
        PopulatePreviewPanel(previewPanel!, endpoint);
        RefreshEndpointConnectionControls(endpoint);

        // Safe Simulator Interaction: Only enable simulator controls for mock endpoints
        bool isMock = endpoint.MockMode;
        bool physicalComConnected = endpoint.IsConnected && !endpoint.MockMode;
        bool physicalCommandsAllowed = isMock || _settings.AllowPhysicalShoeCommands;
        bool canSimulate = isMock && endpoint.IsConnected && !endpoint.InErrorMode;
        bool isAutoRunning = _autoRunSessions.ContainsKey(endpoint);
        bool canDrawNext = canSimulate && !isAutoRunning && CanDrawNextMockCard(endpoint);
        bool canSettle = canSimulate && !isAutoRunning && CanSettleMockRound(endpoint);
        bool canCutCard = canSimulate && !isAutoRunning && !endpoint.ShoeEnding;
        bool canToggleAutoRun = canSimulate && (isAutoRunning || !endpoint.ShoeEnding);
        if (btnPhysicalCommandAuthorization != null)
        {
            btnPhysicalCommandAuthorization.Text = _settings.AllowPhysicalShoeCommands ? "關閉命令" : "啟用命令";
            if (_settings.AllowPhysicalShoeCommands)
            {
                StyleButton(btnPhysicalCommandAuthorization, Color.FromArgb(253, 235, 230), Color.FromArgb(176, 58, 46), Color.FromArgb(245, 183, 177));
            }
            else
            {
                StyleButton(btnPhysicalCommandAuthorization, Color.FromArgb(244, 246, 247), Color.FromArgb(44, 62, 80), Color.FromArgb(213, 219, 219));
            }
        }

        if (lblPhysicalCommandHint != null)
        {
            lblPhysicalCommandHint.Text = _settings.AllowPhysicalShoeCommands
                ? "工程授權已啟用；可對實體牌盒送命令。"
                : "正式模式唯讀；未啟用工程授權時不會送牌盒命令。";
            lblPhysicalCommandHint.ForeColor = _settings.AllowPhysicalShoeCommands
                ? Color.Firebrick
                : Color.FromArgb(90, 105, 120);
        }

        SetButtonEnabledVisual(btnPhysicalLock, physicalCommandsAllowed);
        SetButtonEnabledVisual(btnPhysicalUnlock, physicalCommandsAllowed);
        SetButtonEnabledVisual(btnPhysicalCancelError, physicalCommandsAllowed);
        SetButtonEnabledVisual(btnPhysicalConfirmGameProcess, physicalCommandsAllowed);
        if (cbCancelData != null) cbCancelData.Enabled = physicalCommandsAllowed;
        if (chkEndpointMock != null) chkEndpointMock.Enabled = !physicalComConnected;
        if (mockTestActions != null) mockTestActions.Visible = isMock;
        if (mockTestHint != null) mockTestHint.Visible = !isMock;
        SetButtonEnabledVisual(btnSimCard, canDrawNext);
        SetButtonEnabledVisual(btnSimResult, canSettle);
        SetButtonEnabledVisual(btnSimCutCard, canCutCard);
        SetButtonEnabledVisual(btnAutoRun, canToggleAutoRun);
        if (btnAutoRun != null) btnAutoRun.Text = isAutoRunning ? "停止自動" : "自動跑局";
        SetButtonEnabledVisual(btnMockReset, isMock);
        RefreshAutoCutAfterRoundsControls();
        RefreshCancelErrorOptions(endpoint);
    }

    private void RefreshAutoCutAfterRoundsControls()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        bool canEdit = endpoint != null &&
            endpoint.MockMode &&
            endpoint.IsConnected &&
            !endpoint.InErrorMode &&
            !_autoRunSessions.ContainsKey(endpoint);

        if (chkAutoCutAfterRounds != null)
        {
            chkAutoCutAfterRounds.Enabled = canEdit;
        }

        if (numAutoCutAfterRounds != null)
        {
            numAutoCutAfterRounds.Enabled = canEdit && chkAutoCutAfterRounds?.Checked == true;
        }
    }

    private void RefreshCancelErrorOptions(ShoeEndpoint endpoint)
    {
        if (cbCancelData == null)
        {
            return;
        }

        string[] options = endpoint.InErrorMode && endpoint.ErrorCode == 2
            ? [CancelOverdrawUseOption, CancelOverdrawDiscardOption]
            : [CancelGeneralOption];
        string current = cbCancelData.SelectedItem?.ToString() ?? string.Empty;
        bool sameOptions =
            cbCancelData.Items.Count == options.Length &&
            cbCancelData.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).SequenceEqual(options);

        if (!sameOptions)
        {
            cbCancelData.BeginUpdate();
            try
            {
                cbCancelData.Items.Clear();
                cbCancelData.Items.AddRange(options);
            }
            finally
            {
                cbCancelData.EndUpdate();
            }
        }

        if (cbCancelData.Items.Count == 0)
        {
            return;
        }

        if (!options.Contains(current))
        {
            cbCancelData.SelectedIndex = 0;
        }
    }

    private void PopulatePreviewPanel(BaccaratPreviewPanel panel, ShoeEndpoint endpoint)
    {
        panel.PlayerCards.Clear();
        panel.PlayerCards.AddRange(endpoint.PlayerCards);
        panel.BankerCards.Clear();
        panel.BankerCards.AddRange(endpoint.BankerCards);
        panel.GameResultText = endpoint.GameResultText;
        panel.GameResultColor = endpoint.GameResultColor;
        panel.CountdownActive = endpoint.IsBetCountdownActive;
        panel.CountdownTotalSeconds = endpoint.BetCountdownSeconds;
        panel.CountdownRemainingSeconds = endpoint.BetCountdownRemainingSeconds;
        ResultHoldCountdown resultHold = GetResultHoldCountdown(endpoint);
        panel.ResultHoldActive = resultHold.Active;
        panel.ResultHoldTotalMilliseconds = resultHold.TotalMilliseconds;
        panel.ResultHoldRemainingMilliseconds = resultHold.RemainingMilliseconds;
        panel.StatusItems.Clear();
        panel.StatusItems.AddRange(BuildPreviewStatusItems(endpoint));
        panel.Invalidate();
    }

    private ResultHoldCountdown GetResultHoldCountdown(ShoeEndpoint endpoint)
    {
        int totalMilliseconds = ResultToNextRoundDelaySeconds * 1000;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        if (_pendingNextRoundCountdowns.TryGetValue(endpoint, out PendingNextRoundCountdown? pending))
        {
            int remainingMilliseconds = Math.Clamp((int)Math.Ceiling((pending.DueAtUtc - nowUtc).TotalMilliseconds), 0, totalMilliseconds);
            return new ResultHoldCountdown(remainingMilliseconds > 0, totalMilliseconds, remainingMilliseconds);
        }

        if (_autoRunSessions.TryGetValue(endpoint, out BaccaratAutoRunSession? session) &&
            session.State == BaccaratAutoRunState.ResultHold &&
            session.ResultStartedAtUtc.HasValue)
        {
            DateTimeOffset dueAtUtc = session.ResultStartedAtUtc.Value.AddSeconds(AutoRunSettleHoldSeconds);
            int remainingMilliseconds = Math.Clamp((int)Math.Ceiling((dueAtUtc - nowUtc).TotalMilliseconds), 0, totalMilliseconds);
            return new ResultHoldCountdown(remainingMilliseconds > 0, totalMilliseconds, remainingMilliseconds);
        }

        return new ResultHoldCountdown(false, totalMilliseconds, 0);
    }

    private IEnumerable<PreviewStatusItem> BuildPreviewStatusItems(ShoeEndpoint endpoint)
    {
        yield return new PreviewStatusItem(
            endpoint.IsConnected ? "連線 已連線" : "連線 未連線",
            endpoint.IsConnected ? Color.FromArgb(46, 204, 113) : Color.FromArgb(231, 76, 60));

        yield return new PreviewStatusItem(
            endpoint.MockMode ? "模式 Mock" : endpoint.ConnectionDisplayLabel,
            endpoint.MockMode ? Color.FromArgb(93, 173, 226) : Color.FromArgb(174, 182, 191));

        yield return new PreviewStatusItem(
            endpoint.BmsTransmitEnabled ? "BMS 傳送開啟" : "BMS 傳送停止",
            endpoint.BmsTransmitEnabled ? Color.FromArgb(88, 214, 141) : Color.FromArgb(236, 112, 99));

        BridgeOutboxStatus outboxStatus = GetEndpointOutboxStatus(endpoint);
        if (outboxStatus.PendingCount > 0)
        {
            yield return new PreviewStatusItem(
                IsOutboxWarning(outboxStatus) ? $"BMS 離線 待送 {outboxStatus.PendingCount}" : $"BMS 待送 {outboxStatus.PendingCount}",
                GetOutboxStatusColor(outboxStatus));
        }
        else
        {
            yield return new PreviewStatusItem("BMS 待送 0", Color.FromArgb(88, 214, 141));
        }

        if (endpoint.IsLocked.HasValue)
        {
            yield return new PreviewStatusItem(
                endpoint.IsLocked.Value ? "鑰匙 已鎖定" : "鑰匙 已解鎖",
                endpoint.IsLocked.Value ? Color.FromArgb(245, 176, 65) : Color.FromArgb(88, 214, 141));
        }
        else
        {
            yield return new PreviewStatusItem("鑰匙 未回報", Color.FromArgb(149, 165, 166));
        }

        if (endpoint.InErrorMode)
        {
            string error = endpoint.ErrorCode.HasValue
                ? $"錯誤 {endpoint.ErrorCode:00} {endpoint.ErrorMessage}"
                : "錯誤 異常";
            yield return new PreviewStatusItem(error, Color.FromArgb(236, 112, 99));
        }
        else
        {
            yield return new PreviewStatusItem("錯誤 正常", Color.FromArgb(88, 214, 141));
        }

        if (endpoint.ShoeEnding)
        {
            yield return new PreviewStatusItem("切牌 已抽出，請停靴/換靴", Color.FromArgb(245, 176, 65));
        }

        if (!string.IsNullOrWhiteSpace(endpoint.LastEventText))
        {
            yield return new PreviewStatusItem($"最後 {endpoint.LastEventText}", Color.FromArgb(174, 214, 241));
        }
    }

    private void ToggleSelectedMockMode()
    {
        if (_loadingEndpointEditor || chkEndpointMock == null)
        {
            return;
        }

        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        bool enableMock = chkEndpointMock.Checked;
        if (enableMock && endpoint.IsConnected && !endpoint.MockMode)
        {
            _loadingEndpointEditor = true;
            try
            {
                chkEndpointMock.Checked = false;
            }
            finally
            {
                _loadingEndpointEditor = false;
            }

            AppendLog(endpoint, "WARN", "實體連線已連線，Mock 模式不可開啟；請先斷線再切換 Mock。");
            RefreshSelectedEndpointView(endpoint);
            return;
        }

        StopAutoRun(endpoint);

        try
        {
            if (endpoint.IsConnected)
            {
                endpoint.Disconnect();
            }

            endpoint.MockMode = enableMock;
            if (enableMock)
            {
                endpoint.Connect();
                AppendLog(endpoint, "SYS", "Mock 模式已開啟，模擬連線已初始化。");
            }
            else
            {
                AppendLog(endpoint, "SYS", "Mock 模式已關閉。");
            }
        }
        catch (Exception ex)
        {
            endpoint.MockMode = false;
            _loadingEndpointEditor = true;
            try
            {
                chkEndpointMock.Checked = false;
            }
            finally
            {
                _loadingEndpointEditor = false;
            }

            AppendLog(endpoint, "ERR", $"Mock 模式切換失敗: {ex.Message}");
        }

        _settings.Save();
        RefreshEndpointGridRow(endpoint);
        RefreshSelectedEndpointView(endpoint);
    }

    private void ToggleSelectedBmsTransmission()
    {
        if (_loadingEndpointEditor || _endpointSettingsEditMode || chkEndpointBmsTransmit == null)
        {
            return;
        }

        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        endpoint.BmsTransmitEnabled = chkEndpointBmsTransmit.Checked;
        _settings.Save();
        RefreshEndpointGridRow(endpoint);
        RefreshSelectedEndpointView(endpoint);

        string stateText = endpoint.BmsTransmitEnabled
            ? "BMS 事件傳送已開啟。"
            : "BMS 事件傳送已關閉；後續事件不會寫入 BMS Outbox。";
        AppendLog(endpoint, "SYS", stateText);

        if (endpoint.BmsTransmitEnabled)
        {
            _ = EnsureBmsDispatcherStartedAsync(showErrors: false);
        }
    }

    private bool ApplySelectedEndpointEditor()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return false;
        }

        bool enabled = chkEndpointEnabled!.Checked;
        bool mockMode = chkEndpointMock!.Checked;
        bool bmsTransmitEnabled = chkEndpointBmsTransmit?.Checked ?? endpoint.BmsTransmitEnabled;
        string deskName = string.IsNullOrWhiteSpace(txtDeskName!.Text) ? endpoint.DeskName : txtDeskName.Text.Trim();
        string connectionMode = GetSelectedConnectionMode();
        string sourceDataId = txtSourceDataId!.Text.Trim();
        if (!string.IsNullOrWhiteSpace(sourceDataId) &&
            (!Guid.TryParse(sourceDataId, out Guid parsedSourceDataId) || parsedSourceDataId == Guid.Empty))
        {
            MessageBox.Show("來源資料 ID 若有填寫，必須是 GUID。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string sourceDataCode = txtSourceDataCode!.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceDataCode))
        {
            MessageBox.Show("來源桌碼不可空白，例如 ANGEL_BAC01。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string shoeId = string.IsNullOrWhiteSpace(txtShoeId!.Text) ? endpoint.ShoeId : txtShoeId.Text.Trim();
        if (string.IsNullOrWhiteSpace(shoeId))
        {
            MessageBox.Show("端點 ID 不可空白，例如 SHOE01。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!ValidateEndpointMappingIsUnique(endpoint, sourceDataCode, sourceDataId, shoeId))
        {
            return false;
        }

        int totalBetTimeSeconds = GetTotalBetTimeSeconds(endpoint);
        if (!long.TryParse(txtCurrentShoe!.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long shoe) || shoe <= 0)
        {
            MessageBox.Show("BMS 靴號必須是大於 0 的數字，例如 202605250001。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!long.TryParse(txtCurrentRound!.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long round) || round < 0)
        {
            MessageBox.Show("局號必須是 0 或正整數。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string comPort = cbComPort!.Text.Trim();
        string moxaHost = txtMoxaHost!.Text.Trim();
        int moxaPort = (int)(numMoxaPort?.Value ?? 4001);
        bool useMoxaTcp = string.Equals(connectionMode, ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(moxaHost) &&
            Uri.CheckHostName(moxaHost) == UriHostNameType.Unknown)
        {
            MessageBox.Show("MOXA IP / Host 格式不正確，請輸入例如 10.5.32.25。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!useMoxaTcp &&
            !string.IsNullOrWhiteSpace(comPort) &&
            !SerialPort.GetPortNames().Contains(comPort, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show($"COM Port {comPort} 不是本機目前可用的序列埠，請重新整理後從清單選擇。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!useMoxaTcp && IsComPortAssignedToOtherEndpoint(endpoint, comPort))
        {
            MessageBox.Show($"COM Port {comPort} 已被其他端點使用，請選擇其他序列埠。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (useMoxaTcp && IsMoxaTcpAssignedToOtherEndpoint(endpoint, moxaHost, moxaPort))
        {
            MessageBox.Show($"MOXA {moxaHost}:{moxaPort} 已被其他端點使用，請確認桌台對應。", "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        endpoint.Enabled = enabled;
        endpoint.MockMode = mockMode;
        endpoint.BmsTransmitEnabled = bmsTransmitEnabled;
        endpoint.DeskName = deskName;
        endpoint.SourceDataId = sourceDataId;
        endpoint.SourceDataCode = sourceDataCode;
        endpoint.ShoeId = shoeId;
        ApplyGlobalConnectionMode(connectionMode);
        endpoint.TotalBetTimeSeconds = totalBetTimeSeconds;
        endpoint.SetGameNumber(shoe, round);
        endpoint.ComPort = comPort;
        endpoint.MoxaHost = moxaHost;
        endpoint.MoxaPort = moxaPort;
        _settings.Save();
        foreach (ShoeEndpoint changedEndpoint in _endpoints)
        {
            RefreshEndpointGridRow(changedEndpoint);
        }

        RefreshSelectedEndpointView(endpoint);
        AppendLog(endpoint, "SYS", "端點設定已套用。");
        return true;
    }

    private void ApplyGlobalConnectionMode(string connectionMode)
    {
        string normalizedMode = ShoeConnectionMode.Normalize(connectionMode);
        _settings.ConnectionMode = normalizedMode;
        _selectedConnectionMode = normalizedMode;

        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            endpoint.ConnectionMode = normalizedMode;
        }

        RefreshConnectionModeUi();
    }

    private void AddNewEndpoint()
    {
        ShoeEndpointSettings? settings = CreateNextUniqueEndpointSettings();
        if (settings == null)
        {
            MessageBox.Show("找不到可自動建立的唯一端點資料，請先移除重複或不使用的端點。", "設定重複", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        settings.ConnectionMode = ShoeConnectionMode.Normalize(_settings.ConnectionMode);
        _settings.Shoes.Add(settings);
        AddEndpoint(settings);
        _settings.Save();
        RefreshEndpointGrid();
    }

    private ShoeEndpointSettings? CreateNextUniqueEndpointSettings()
    {
        for (int number = 1; number <= 99; number++)
        {
            ShoeEndpointSettings candidate = ShoeEndpointSettings.CreateDefaultForNumber(number);
            if (FindDuplicateEndpoint(null, candidate.SourceDataCode, candidate.SourceDataId, candidate.ShoeId) == null)
            {
                return candidate;
            }
        }

        return null;
    }

    private bool ValidateEndpointMappingIsUnique(
        ShoeEndpoint endpoint,
        string sourceDataCode,
        string sourceDataId,
        string shoeId)
    {
        DuplicateEndpointField? duplicate = FindDuplicateEndpoint(endpoint, sourceDataCode, sourceDataId, shoeId);
        if (duplicate == null)
        {
            return true;
        }

        MessageBox.Show(
            $"{duplicate.Label}「{duplicate.Value}」已被 {duplicate.Endpoint.DeskName} / {duplicate.Endpoint.ShoeId} 使用，請改用不同的端點對應資料。",
            "設定重複",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }

    private DuplicateEndpointField? FindDuplicateEndpoint(
        ShoeEndpoint? currentEndpoint,
        string sourceDataCode,
        string sourceDataId,
        string shoeId)
    {
        foreach (ShoeEndpoint other in _endpoints)
        {
            if (ReferenceEquals(other, currentEndpoint))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sourceDataCode) &&
                string.Equals(other.SourceDataCode, sourceDataCode, StringComparison.OrdinalIgnoreCase))
            {
                return new DuplicateEndpointField("來源桌碼", sourceDataCode, other);
            }

            if (!string.IsNullOrWhiteSpace(sourceDataId) &&
                string.Equals(other.SourceDataId, sourceDataId, StringComparison.OrdinalIgnoreCase))
            {
                return new DuplicateEndpointField("來源資料ID", sourceDataId, other);
            }

            if (!string.IsNullOrWhiteSpace(shoeId) &&
                string.Equals(other.ShoeId, shoeId, StringComparison.OrdinalIgnoreCase))
            {
                return new DuplicateEndpointField("端點ID", shoeId, other);
            }
        }

        return null;
    }

    private void RemoveSelectedEndpoint()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }
        StopAutoRun(endpoint);
        CancelPendingNextRoundCountdown(endpoint);
        ClearCutCardAutoLock(endpoint);
        ClearResultHoldAutoLock(endpoint);

        endpoint.Disconnect();
        _endpoints.Remove(endpoint);
        _settings.Shoes.Remove(endpoint.Settings);
        if (_settings.Shoes.Count == 0)
        {
            ShoeEndpointSettings settings = ShoeEndpointSettings.CreateDefault();
            settings.ConnectionMode = ShoeConnectionMode.Normalize(_settings.ConnectionMode);
            _settings.Shoes.Add(settings);
            AddEndpoint(settings);
        }

        _settings.Save();
        RefreshEndpointGrid();
        SelectFirstEndpoint();
    }

    private void RefreshComPorts()
    {
        ShoeEndpoint? selectedEndpoint = SelectedEndpoint;
        string current = selectedEndpoint?.ComPort ?? cbComPort?.Text ?? string.Empty;
        HashSet<string> usedByOtherEndpoints = _endpoints
            .Where(endpoint => !ReferenceEquals(endpoint, selectedEndpoint))
            .Select(endpoint => endpoint.ComPort.Trim())
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] ports = SerialPort.GetPortNames()
            .Where(port => !usedByOtherEndpoints.Contains(port) || string.Equals(port, current, StringComparison.OrdinalIgnoreCase))
            .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cbComPort != null)
        {
            cbComPort.Items.Clear();
            cbComPort.Items.Add(string.Empty);
            cbComPort.Items.AddRange(ports);
            cbComPort.SelectedItem = cbComPort.Items.Contains(current) ? current : string.Empty;
        }
    }

    private bool IsComPortAssignedToOtherEndpoint(ShoeEndpoint endpoint, string comPort)
    {
        return !string.IsNullOrWhiteSpace(comPort) &&
            _endpoints.Any(other =>
                !ReferenceEquals(other, endpoint) &&
                string.Equals(other.ComPort, comPort, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsMoxaTcpAssignedToOtherEndpoint(ShoeEndpoint endpoint, string moxaHost, int moxaPort)
    {
        return !string.IsNullOrWhiteSpace(moxaHost) &&
            _endpoints.Any(other =>
                !ReferenceEquals(other, endpoint) &&
                other.MoxaPort == moxaPort &&
                string.Equals(other.MoxaHost, moxaHost, StringComparison.OrdinalIgnoreCase));
    }

    private void ConnectSelectedEndpoint()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (endpoint.IsConnected)
        {
            return;
        }

        try
        {
            if (_endpointSettingsEditMode && !ApplySelectedEndpointEditor())
            {
                return;
            }

            SetEndpointSettingsEditMode(false);

            if (!endpoint.MockMode && endpoint.IsMoxaTcpMode &&
                (string.IsNullOrWhiteSpace(endpoint.MoxaHost) || endpoint.MoxaPort <= 0 || endpoint.MoxaPort > 65535))
            {
                AppendLog(endpoint, "WARN", "未設定 MOXA IP / TCP Port，實體牌盒不執行連線。");
                RefreshSelectedEndpointView(endpoint);
                return;
            }

            if (!endpoint.MockMode && !endpoint.IsMoxaTcpMode && string.IsNullOrWhiteSpace(endpoint.ComPort))
            {
                AppendLog(endpoint, "WARN", "未選擇 COM Port，實體牌盒不執行連線。");
                RefreshSelectedEndpointView(endpoint);
                return;
            }

            endpoint.Connect();
            AppendLog(endpoint, "SYS", "牌盒已連線。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"牌盒連線失敗：{ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AppendLog(endpoint, "ERR", ex.Message);
        }
    }

    private void DisconnectSelectedEndpoint()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (!endpoint.IsConnected)
        {
            RefreshSelectedEndpointView(endpoint);
            return;
        }

        StopAutoRun(endpoint);
        CancelPendingNextRoundCountdown(endpoint);

        endpoint.Disconnect();
        AppendLog(endpoint, "SYS", "牌盒已斷線。");
    }

    private async Task LockEndpointForResultHoldAsync(ShoeEndpoint endpoint)
    {
        if (endpoint.MockMode || endpoint.ShoeEnding)
        {
            ClearResultHoldAutoLock(endpoint);
            return;
        }

        if (!CanSendShoeCommand(endpoint, "結算後自動鎖定"))
        {
            ClearResultHoldAutoLock(endpoint);
            return;
        }

        if (!endpoint.IsConnected)
        {
            ClearResultHoldAutoLock(endpoint);
            AppendLog(endpoint, "WARN", "結算後牌盒未連線，無法自動鎖定。");
            return;
        }

        if (endpoint.IsLocked == true)
        {
            AppendLog(endpoint, "SYS", "結算後牌盒目前已鎖定，保留現有鎖定狀態。");
            return;
        }

        MarkResultHoldAutoLocked(endpoint);
        SerialListener.SerialCommandResult result = await endpoint.SetLockAsync(true);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", $"結算後自動鎖定: {result.Message}");
        if (!result.Succeeded)
        {
            ClearResultHoldAutoLock(endpoint);
            AppendLog(endpoint, "WARN", "結算後自動鎖定失敗；Bridge 仍會在結果保留結束後嘗試進入下一局。");
        }
    }

    private async Task<bool> ReleaseResultHoldAutoLockIfNeededAsync(ShoeEndpoint endpoint)
    {
        if (!IsResultHoldAutoLocked(endpoint))
        {
            return true;
        }

        if (endpoint.MockMode)
        {
            ClearResultHoldAutoLock(endpoint);
            return true;
        }

        if (!CanSendShoeCommand(endpoint, "解除結算後自動鎖定"))
        {
            ClearResultHoldAutoLock(endpoint);
            return true;
        }

        if (!endpoint.IsConnected)
        {
            AppendLog(endpoint, "WARN", "牌盒未連線，無法解除結算後自動鎖定；下一局倒數未啟動。");
            RefreshEndpointGridRow(endpoint);
            if (ReferenceEquals(endpoint, SelectedEndpoint))
            {
                RefreshSelectedEndpointView(endpoint);
            }
            return false;
        }

        SerialListener.SerialCommandResult result = await endpoint.SetLockAsync(false);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", $"解除結算後自動鎖定: {result.Message}");
        if (result.Succeeded)
        {
            ClearResultHoldAutoLock(endpoint);
            return true;
        }

        AppendLog(endpoint, "WARN", "結算後自動鎖定尚未解除；下一局倒數未啟動，請確認牌盒連線或手動解鎖。");
        RefreshEndpointGridRow(endpoint);
        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }
        return false;
    }

    private void MarkResultHoldAutoLocked(ShoeEndpoint endpoint)
    {
        lock (_resultHoldAutoLockGate)
        {
            _resultHoldAutoLockedEndpoints.Add(endpoint);
        }
    }

    private bool IsResultHoldAutoLocked(ShoeEndpoint endpoint)
    {
        lock (_resultHoldAutoLockGate)
        {
            return _resultHoldAutoLockedEndpoints.Contains(endpoint);
        }
    }

    private void ClearResultHoldAutoLock(ShoeEndpoint endpoint)
    {
        lock (_resultHoldAutoLockGate)
        {
            _resultHoldAutoLockedEndpoints.Remove(endpoint);
        }
    }

    private async Task LockEndpointAfterCutCardAsync(ShoeEndpoint endpoint)
    {
        if (endpoint.MockMode)
        {
            ClearCutCardAutoLock(endpoint);
            AppendLog(endpoint, "SYS", "Mock 切牌不送牌盒鎖定請求。");
            return;
        }

        if (!CanSendShoeCommand(endpoint, "切牌後自動鎖定"))
        {
            ClearCutCardAutoLock(endpoint);
            return;
        }

        if (!endpoint.IsConnected)
        {
            ClearCutCardAutoLock(endpoint);
            AppendLog(endpoint, "WARN", "切牌已抽出，但牌盒未連線，無法自動鎖定。");
            return;
        }

        if (endpoint.IsLocked == true)
        {
            if (IsResultHoldAutoLocked(endpoint))
            {
                ClearResultHoldAutoLock(endpoint);
                MarkCutCardAutoLocked(endpoint);
                AppendLog(endpoint, "SYS", "結算後自動鎖定已轉為切牌鎖定。");
                return;
            }

            AppendLog(endpoint, "SYS", "切牌已抽出，牌盒目前已鎖定，保留現有鎖定狀態。");
            return;
        }

        SerialListener.SerialCommandResult result = await endpoint.SetLockAsync(true);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", $"切牌後自動鎖定: {result.Message}");
        if (result.Succeeded)
        {
            MarkCutCardAutoLocked(endpoint);
        }
        else
        {
            ClearCutCardAutoLock(endpoint);
            AppendLog(endpoint, "WARN", "切牌後自動鎖定失敗；未按「新靴」前收到的牌面仍會被 Bridge 忽略。");
        }
    }

    private async Task<bool> ReleaseCutCardAutoLockIfNeededAsync(ShoeEndpoint endpoint)
    {
        if (!IsCutCardAutoLocked(endpoint))
        {
            return true;
        }

        if (endpoint.MockMode)
        {
            ClearCutCardAutoLock(endpoint);
            return true;
        }

        if (!CanSendShoeCommand(endpoint, "解除切牌後自動鎖定"))
        {
            ClearCutCardAutoLock(endpoint);
            return true;
        }

        if (!endpoint.IsConnected)
        {
            AppendLog(endpoint, "WARN", "牌盒未連線，無法解除切牌後自動鎖定；新靴流程未啟動。");
            RefreshEndpointGridRow(endpoint);
            if (ReferenceEquals(endpoint, SelectedEndpoint))
            {
                RefreshSelectedEndpointView(endpoint);
            }
            return false;
        }

        SerialListener.SerialCommandResult result = await endpoint.SetLockAsync(false);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", $"解除切牌後自動鎖定: {result.Message}");
        if (result.Succeeded)
        {
            ClearCutCardAutoLock(endpoint);
            return true;
        }

        AppendLog(endpoint, "WARN", "切牌後自動鎖定尚未解除；新靴流程未啟動，請確認牌盒連線或手動解鎖。");
        RefreshEndpointGridRow(endpoint);
        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }
        return false;
    }

    private void MarkCutCardAutoLocked(ShoeEndpoint endpoint)
    {
        lock (_cutCardAutoLockGate)
        {
            _cutCardAutoLockedEndpoints.Add(endpoint);
        }
    }

    private bool IsCutCardAutoLocked(ShoeEndpoint endpoint)
    {
        lock (_cutCardAutoLockGate)
        {
            return _cutCardAutoLockedEndpoints.Contains(endpoint);
        }
    }

    private void ClearCutCardAutoLock(ShoeEndpoint endpoint)
    {
        lock (_cutCardAutoLockGate)
        {
            _cutCardAutoLockedEndpoints.Remove(endpoint);
        }
    }

    private async Task StartNewShoeForSelectedEndpointAsync()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }
        StopAutoRun(endpoint);
        CancelPendingNextRoundCountdown(endpoint);

        if (!await ReleaseResultHoldAutoLockIfNeededAsync(endpoint))
        {
            return;
        }

        if (!await ReleaseCutCardAutoLockIfNeededAsync(endpoint))
        {
            return;
        }

        if (endpoint.IsConnected && endpoint.IsLocked == true)
        {
            AppendLog(endpoint, "WARN", "牌盒目前仍為鎖定狀態，請先解鎖後再啟動新靴。");
            RefreshEndpointGridRow(endpoint);
            if (ReferenceEquals(endpoint, SelectedEndpoint))
            {
                RefreshSelectedEndpointView(endpoint);
            }
            return;
        }

        endpoint.StartNewShoe();
        long newShoe = endpoint.CurrentShoe;
        AppendLog(endpoint, "SYS", $"已切換新靴: {newShoe}");

        if (endpoint.IsConnected && !endpoint.InErrorMode)
        {
            await BeginNextRoundCountdownAsync(endpoint, DateTimeOffset.UtcNow, "新靴第一局倒數");
        }
        else
        {
            _settings.Save();
            RefreshEndpointGridRow(endpoint);
            RefreshSelectedEndpointView(endpoint);
            AppendLog(endpoint, "SYS", "牌盒未連線或仍在錯誤模式，新靴第一局倒數未啟動。");
        }

        txtCurrentShoe!.Text = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture);
        txtCurrentRound!.Text = endpoint.CurrentRound.ToString(CultureInfo.InvariantCulture);
    }

    private async Task SendSelectedLockAsync(bool locked)
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (!CanSendShoeCommand(endpoint, locked ? "牌盒鎖定" : "牌盒解鎖"))
        {
            return;
        }

        SerialListener.SerialCommandResult result = await endpoint.SetLockAsync(locked);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", result.Message);
        if (result.Succeeded && !locked)
        {
            ClearCutCardAutoLock(endpoint);
            ClearResultHoldAutoLock(endpoint);
        }
    }

    private async Task SendSelectedCancelErrorAsync()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null || cbCancelData == null)
        {
            return;
        }

        if (!CanSendShoeCommand(endpoint, "錯誤清除"))
        {
            return;
        }

        string data = (cbCancelData.SelectedItem?.ToString() ?? "00").Split(' ')[0];
        if (!ValidateCancelErrorSelection(endpoint, data))
        {
            return;
        }

        SerialListener.SerialCommandResult result = await endpoint.CancelErrorAsync(data);
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", result.Message);
    }

    private async Task SendSelectedGameProcessConfirmationAsync()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (!CanSendShoeCommand(endpoint, "流程確認"))
        {
            return;
        }

        SerialListener.SerialCommandResult result = await endpoint.ConfirmGameProcessAsync();
        AppendLog(endpoint, result.Succeeded ? "ACK" : "NAK", $"流程確認 GP 00: {result.Message}");
    }

    private bool ValidateCancelErrorSelection(ShoeEndpoint endpoint, string data)
    {
        if (!endpoint.InErrorMode || !endpoint.ErrorCode.HasValue)
        {
            return true;
        }

        if (endpoint.ErrorCode == 2 && data == "00")
        {
            cbCancelData!.SelectedIndex = 1;
            const string message = "目前錯誤是 02 發牌過多（Overdraw），不能使用 00 一般錯誤清除。請選擇 01 繼續使用，或 02 棄牌改用下一張。";
            AppendLog(endpoint, "WARN", message);
            MessageBox.Show(message, "清錯流程防呆", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (endpoint.ErrorCode != 2 && data is "01" or "02")
        {
            cbCancelData!.SelectedIndex = 0;
            string message = $"目前錯誤是 {endpoint.ErrorCode:00} {endpoint.ErrorMessage}，不是 Overdraw，請使用 00 一般錯誤清除。";
            AppendLog(endpoint, "WARN", message);
            MessageBox.Show(message, "清錯流程防呆", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void ResetSelectedMockTest()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }
        StopAutoRun(endpoint);
        CancelPendingNextRoundCountdown(endpoint);

        endpoint.ResetMockTestState();
        RefreshEndpointGridRow(endpoint);
        RefreshSelectedEndpointView(endpoint);
        AppendLog(endpoint, "SIM", "Mock 測試已重置。");
    }

    private void SimulateSelectedCard()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (endpoint.InErrorMode)
        {
            AppendLog(endpoint, "SIM", "目前處於錯誤模式，請先清錯後再繼續模擬。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.GameResultText))
        {
            AppendLog(endpoint, "SIM", "本局已結算，等待自動進入下一局。");
            return;
        }

        if (endpoint.IsBetCountdownActive)
        {
            AppendLog(endpoint, "SIM", "下注倒數中，倒數結束後才能模擬發牌。");
            return;
        }

        if (!CanDrawNextMockCard(endpoint))
        {
            AppendLog(endpoint, "SIM", "目前牌面已可結算，請按「模擬結算」。");
            return;
        }

        if (endpoint.PlayerCards.Count > 3 || endpoint.BankerCards.Count > 3)
        {
            AppendLog(endpoint, "SIM", "目前牌面已超過單局上限，模擬牌盒回報 Overdraw。");
            InjectSimulatedError(endpoint, 2);
            return;
        }

        int playerCount = endpoint.PlayerCards.Count;
        int bankerCount = endpoint.BankerCards.Count;

        if (playerCount == 0 && bankerCount == 0)
        {
            AppendLog(endpoint, "SIM", "模擬手動牌局開始。");
            InjectSimulatedCard(endpoint, 0, 1);
            return;
        }

        if (playerCount == 1 && bankerCount == 0)
        {
            InjectSimulatedCard(endpoint, 1, 1);
            return;
        }

        if (playerCount == 1 && bankerCount == 1)
        {
            InjectSimulatedCard(endpoint, 0, 2);
            return;
        }

        if (playerCount == 2 && bankerCount == 1)
        {
            InjectSimulatedCard(endpoint, 1, 2);
            return;
        }

        if (playerCount == 2 && bankerCount == 2)
        {
            BaccaratAutoRunState nextState = DecideAfterInitialDeal(endpoint);
            if (nextState == BaccaratAutoRunState.DrawPlayer3)
            {
                InjectSimulatedCard(endpoint, 0, 3);
                return;
            }

            if (nextState == BaccaratAutoRunState.DrawBanker3)
            {
                InjectSimulatedCard(endpoint, 1, 3);
                return;
            }

            AppendLog(endpoint, "SIM", "本局依百家樂規則已不需要補牌；再次抽牌會模擬 Overdraw。");
            InjectSimulatedError(endpoint, 2);
            return;
        }

        if (playerCount == 3 && bankerCount == 2)
        {
            if (ShouldBankerDrawThird(endpoint))
            {
                InjectSimulatedCard(endpoint, 1, 3);
                return;
            }

            AppendLog(endpoint, "SIM", "閒家已補第三張，莊家依規則不補牌；再次抽牌會模擬 Overdraw。");
            InjectSimulatedError(endpoint, 2);
            return;
        }

        if ((playerCount is 2 or 3) && (bankerCount is 2 or 3))
        {
            AppendLog(endpoint, "SIM", "本局發牌已完成；再次抽牌會模擬 Overdraw。");
            InjectSimulatedError(endpoint, 2);
            return;
        }

        AppendLog(endpoint, "SIM", "目前模擬牌面順序異常，已清除並重新開始模擬新局。");
        endpoint.ClearPreview();
        InjectSimulatedCard(endpoint, 0, 1);
    }

    private void SimulateSelectedResult()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (endpoint.InErrorMode)
        {
            AppendLog(endpoint, "SIM", "目前處於錯誤模式，請先清錯後再模擬結算。");
            return;
        }

        if (!CanSettleMockRound(endpoint))
        {
            AppendLog(endpoint, "SIM", "目前牌面尚未完成，請先按「模擬發牌」。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.GameResultText))
        {
            AppendLog(endpoint, "SIM", "本局已結算，不重複送出 GameResult。");
            return;
        }

        InjectSimulatedResult(endpoint);
    }

    private void SimulateSelectedCutCard()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (!endpoint.MockMode || !endpoint.IsConnected)
        {
            AppendLog(endpoint, "SIM", "請先開啟 Mock 模式並連線後再模擬切牌。");
            return;
        }

        if (endpoint.InErrorMode)
        {
            AppendLog(endpoint, "SIM", "目前處於錯誤模式，請先清錯後再模擬切牌。");
            return;
        }

        if (_autoRunSessions.ContainsKey(endpoint))
        {
            AppendLog(endpoint, "SIM", "自動跑局中不模擬切牌；請先停止自動跑局。");
            return;
        }

        if (endpoint.ShoeEnding)
        {
            AppendLog(endpoint, "SIM", "此靴已標記切牌抽出，請按「新靴」模擬現場換靴。");
            return;
        }

        AppendLog(endpoint, "SIM", "模擬現場切牌抽出；請停靴/換靴後按「新靴」。");
        InjectSimulatedCutCard(endpoint);
    }

    private static void InjectSimulatedCutCard(ShoeEndpoint endpoint)
    {
        endpoint.InjectSimulatedEvent("C", [(byte)'C']);
    }

    private static bool CanDrawNextMockCard(ShoeEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.GameResultText) || endpoint.IsBetCountdownActive)
        {
            return false;
        }

        int playerCount = endpoint.PlayerCards.Count;
        int bankerCount = endpoint.BankerCards.Count;
        if (playerCount > 3 || bankerCount > 3)
        {
            return false;
        }

        return (playerCount, bankerCount) switch
        {
            (0, 0) or
            (1, 0) or
            (1, 1) or
            (2, 1) => true,
            (2, 2) => DecideAfterInitialDeal(endpoint) != BaccaratAutoRunState.Settle,
            (3, 2) => ShouldBankerDrawThird(endpoint),
            _ => false
        };
    }

    private static bool CanSettleMockRound(ShoeEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.GameResultText))
        {
            return false;
        }

        int playerCount = endpoint.PlayerCards.Count;
        int bankerCount = endpoint.BankerCards.Count;
        if (playerCount < 2 || bankerCount < 2 || playerCount > 3 || bankerCount > 3)
        {
            return false;
        }

        return !CanDrawNextMockCard(endpoint);
    }

    private void ToggleAutoRun()
    {
        ShoeEndpoint? endpoint = SelectedEndpoint;
        if (endpoint == null)
        {
            return;
        }

        if (_autoRunSessions.ContainsKey(endpoint))
        {
            StopAutoRun(endpoint);
            return;
        }

        if (!endpoint.MockMode || !endpoint.IsConnected)
        {
            AppendLog(endpoint, "SIM", "請先開啟 Mock 模式並連線後再啟動自動跑局。");
            RefreshSelectedEndpointView(endpoint);
            return;
        }

        if (endpoint.InErrorMode)
        {
            AppendLog(endpoint, "SIM", "目前處於錯誤模式，請先清錯後再啟動自動跑局。");
            RefreshSelectedEndpointView(endpoint);
            return;
        }

        if (endpoint.ShoeEnding)
        {
            AppendLog(endpoint, "SIM", "此靴已標記切牌抽出，請按「新靴」後再啟動自動跑局。");
            RefreshSelectedEndpointView(endpoint);
            return;
        }

        CancelPendingNextRoundCountdown(endpoint);
        int cutAfterRounds = GetAutoCutAfterRounds();
        _autoRunSessions[endpoint] = new BaccaratAutoRunSession
        {
            CutAfterRounds = cutAfterRounds
        };
        if (!_autoRunTimer.Enabled)
        {
            _autoRunTimer.Start();
        }

        string cutCardText = cutAfterRounds > 0 ? $"（{cutAfterRounds} 局後模擬切牌）" : string.Empty;
        AppendLog(endpoint, "SIM", $"自動跑局已啟動{cutCardText}。");
        RefreshSelectedEndpointView(endpoint);
    }

    private int GetAutoCutAfterRounds()
    {
        if (chkAutoCutAfterRounds?.Checked != true || numAutoCutAfterRounds == null)
        {
            return 0;
        }

        return Math.Clamp((int)numAutoCutAfterRounds.Value, 1, 999);
    }

    private void StopAutoRun(ShoeEndpoint? endpoint = null)
    {
        if (endpoint == null)
        {
            foreach (ShoeEndpoint runningEndpoint in _autoRunSessions.Keys.ToList())
            {
                runningEndpoint.ClearBetCountdown();
                AppendLog(runningEndpoint, "SIM", "自動跑局已停止。");
            }

            _autoRunSessions.Clear();
            _autoRunTimer.Stop();
            if (btnAutoRun != null)
            {
                btnAutoRun.Text = "自動跑局";
            }

            return;
        }

        if (_autoRunSessions.Remove(endpoint))
        {
            endpoint.ClearBetCountdown();
            AppendLog(endpoint, "SIM", "自動跑局已停止。");
        }

        if (_autoRunSessions.Count == 0)
        {
            _autoRunTimer.Stop();
        }

        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }
    }

    private async void AutoRunTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoRunTickInProgress)
        {
            return;
        }

        _autoRunTickInProgress = true;
        try
        {
            foreach ((ShoeEndpoint endpoint, BaccaratAutoRunSession session) in _autoRunSessions.ToList())
            {
                await ProcessAutoRunSessionAsync(endpoint, session);
            }
        }
        finally
        {
            _autoRunTickInProgress = false;
        }
    }

    private async Task ProcessAutoRunSessionAsync(ShoeEndpoint endpoint, BaccaratAutoRunSession session)
    {
        if (!_endpoints.Contains(endpoint) || !endpoint.MockMode || !endpoint.IsConnected || endpoint.InErrorMode || endpoint.ShoeEnding)
        {
            StopAutoRun(endpoint);
            return;
        }

        switch (session.State)
        {
            case BaccaratAutoRunState.Idle:
                session.IdleTicks++;
                if (session.IdleTicks >= 2)
                {
                    session.State = BaccaratAutoRunState.Countdown;
                    session.CountdownStartedAtUtc = null;
                    session.CountdownSeconds = 0;
                    session.IdleTicks = 0;
                }
                break;

            case BaccaratAutoRunState.Countdown:
                if (session.CountdownStartedAtUtc == null)
                {
                    session.CountdownSeconds = GetTotalBetTimeSeconds(endpoint);
                    session.CountdownStartedAtUtc = DateTimeOffset.UtcNow;
                    AppendLog(endpoint, "SIM", $"------------------ 模擬新局倒數開始（{session.CountdownSeconds} 秒） ------------------");
                    await BeginNextRoundCountdownAsync(endpoint, session.CountdownStartedAtUtc.Value, "自動跑局新局倒數");
                    break;
                }

                if ((DateTimeOffset.UtcNow - session.CountdownStartedAtUtc.Value).TotalSeconds >= session.CountdownSeconds)
                {
                    endpoint.ClearBetCountdown();
                    session.State = BaccaratAutoRunState.DrawPlayer1;
                    ScheduleNextAutoRunAction(session);
                }
                else if (ReferenceEquals(endpoint, SelectedEndpoint))
                {
                    RefreshSelectedEndpointView(endpoint);
                }
                break;

            case BaccaratAutoRunState.DrawPlayer1:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedCard(endpoint, 0, 1);
                session.State = BaccaratAutoRunState.DrawBanker1;
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.DrawBanker1:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedCard(endpoint, 1, 1);
                session.State = BaccaratAutoRunState.DrawPlayer2;
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.DrawPlayer2:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedCard(endpoint, 0, 2);
                session.State = BaccaratAutoRunState.DrawBanker2;
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.DrawBanker2:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedCard(endpoint, 1, 2);
                session.State = DecideAfterInitialDeal(endpoint);
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.DrawPlayer3:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedCard(endpoint, 0, 3);
                session.State = BaccaratAutoRunState.DrawBanker3;
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.DrawBanker3:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                if (ShouldBankerDrawThird(endpoint))
                {
                    InjectSimulatedCard(endpoint, 1, 3);
                }
                session.State = BaccaratAutoRunState.Settle;
                ScheduleNextAutoRunAction(session);
                break;

            case BaccaratAutoRunState.Settle:
                if (!CanRunTimedAutoAction(endpoint, session))
                {
                    break;
                }

                InjectSimulatedResult(endpoint);
                session.CompletedRounds++;
                session.State = BaccaratAutoRunState.ResultHold;
                session.NextActionAtUtc = null;
                session.ResultStartedAtUtc = DateTimeOffset.UtcNow;
                session.IdleTicks = 0;
                break;

            case BaccaratAutoRunState.ResultHold:
                session.ResultStartedAtUtc ??= DateTimeOffset.UtcNow;
                if ((DateTimeOffset.UtcNow - session.ResultStartedAtUtc.Value).TotalSeconds >= AutoRunSettleHoldSeconds)
                {
                    if (session.CutAfterRounds > 0 && session.CompletedRounds >= session.CutAfterRounds)
                    {
                        AppendLog(endpoint, "SIM", $"自動跑局已完成 {session.CompletedRounds} 局，模擬切牌。");
                        InjectSimulatedCutCard(endpoint);
                        return;
                    }

                    session.State = BaccaratAutoRunState.Countdown;
                    session.CountdownStartedAtUtc = null;
                    session.CountdownSeconds = 0;
                    session.ResultStartedAtUtc = null;
                    session.IdleTicks = 0;
                }
                else if (ReferenceEquals(endpoint, SelectedEndpoint))
                {
                    RefreshSelectedEndpointView(endpoint);
                }
                break;
        }
    }

    private bool CanRunTimedAutoAction(ShoeEndpoint endpoint, BaccaratAutoRunSession session)
    {
        if (session.NextActionAtUtc == null)
        {
            ScheduleNextAutoRunAction(session);
            return false;
        }

        if (DateTimeOffset.UtcNow >= session.NextActionAtUtc.Value)
        {
            return true;
        }

        if (ReferenceEquals(endpoint, SelectedEndpoint))
        {
            RefreshSelectedEndpointView(endpoint);
        }

        return false;
    }

    private static void ScheduleNextAutoRunAction(BaccaratAutoRunSession session)
    {
        session.NextActionAtUtc = DateTimeOffset.UtcNow.AddSeconds(AutoRunDealDelaySeconds);
    }

    private void InjectSimulatedCard(ShoeEndpoint endpoint, int targetVal, int index)
    {
        int suitVal = _random.Next(1, 5);
        int valueVal = _random.Next(1, 14);
        byte byte1 = (byte)(0x80 | (targetVal << 4) | (index & 0x0F));
        byte byte2 = (byte)(0x80 | (suitVal << 4) | (valueVal & 0x0F));
        endpoint.InjectSimulatedEvent("D", [(byte)'D', byte1, byte2]);
    }

    private static void InjectSimulatedError(ShoeEndpoint endpoint, int errorCode)
    {
        byte payload = (byte)(0x80 | 0x40 | (errorCode & 0x3F));
        endpoint.InjectSimulatedEvent("E", [(byte)'E', payload]);
    }

    private void InjectSimulatedResult(ShoeEndpoint endpoint)
    {
        if (endpoint.PlayerCards.Count < 2 || endpoint.BankerCards.Count < 2)
        {
            AppendLog(endpoint, "SIM", "牌數不足，無法依百家樂規則模擬結算。");
            return;
        }

        int playerPoint = CalculateBaccaratScore(endpoint.PlayerCards);
        int bankerPoint = CalculateBaccaratScore(endpoint.BankerCards);
        int outcomeVal = playerPoint > bankerPoint ? 1 :
            playerPoint < bankerPoint ? 4 : 2;

        bool playerPair = HasPair(endpoint.PlayerCards);
        bool bankerPair = HasPair(endpoint.BankerCards);
        int pairVal = playerPair && bankerPair ? 3 :
            playerPair ? 1 :
            bankerPair ? 2 : 0;

        byte payload = (byte)(0x80 | (outcomeVal << 4) | (pairVal & 0x03));
        endpoint.InjectSimulatedEvent("G", [(byte)'G', payload]);
    }

    private static BaccaratAutoRunState DecideAfterInitialDeal(ShoeEndpoint endpoint)
    {
        int playerPoint = CalculateBaccaratScore(endpoint.PlayerCards);
        int bankerPoint = CalculateBaccaratScore(endpoint.BankerCards);

        if (playerPoint >= 8 || bankerPoint >= 8)
        {
            return BaccaratAutoRunState.Settle;
        }

        if (playerPoint <= 5)
        {
            return BaccaratAutoRunState.DrawPlayer3;
        }

        return bankerPoint <= 5 ? BaccaratAutoRunState.DrawBanker3 : BaccaratAutoRunState.Settle;
    }

    private static bool ShouldBankerDrawThird(ShoeEndpoint endpoint)
    {
        int bankerPoint = CalculateBaccaratScore(endpoint.BankerCards);
        BaccaratCard? playerThird = endpoint.PlayerCards.FirstOrDefault(c => c.Index == 3);
        if (playerThird == null)
        {
            return bankerPoint <= 5;
        }

        int playerThirdPoint = GetBaccaratCardPoint(playerThird);
        return bankerPoint switch
        {
            <= 2 => true,
            3 => playerThirdPoint != 8,
            4 => playerThirdPoint is >= 2 and <= 7,
            5 => playerThirdPoint is >= 4 and <= 7,
            6 => playerThirdPoint is 6 or 7,
            _ => false
        };
    }

    private static bool HasPair(List<BaccaratCard> cards)
    {
        BaccaratCard? first = cards.FirstOrDefault(c => c.Index == 1);
        BaccaratCard? second = cards.FirstOrDefault(c => c.Index == 2);
        return first != null && second != null && first.Value == second.Value;
    }

    private static int CalculateBaccaratScore(List<BaccaratCard> cards)
    {
        int sum = 0;
        foreach (BaccaratCard card in cards)
        {
            sum += GetBaccaratCardPoint(card);
        }

        return sum % 10;
    }

    private static int GetBaccaratCardPoint(BaccaratCard card)
    {
        return card.Value switch
        {
            "A" => 1,
            "J" or "Q" or "K" or "10" or "None" or "" => 0,
            _ => int.TryParse(card.Value, out int value) ? value : 0
        };
    }

    private bool TryRefreshGeneratedToken(bool showError)
    {
        if (string.IsNullOrWhiteSpace(_settings.JwtSigningKey))
        {
            _settings.BmsToken = string.Empty;
            if (txtBmsToken != null)
            {
                txtBmsToken.UseSystemPasswordChar = false;
                txtBmsToken.Text = "未配置 JWT Signing Key";
            }

            if (showError)
            {
                MessageBox.Show("JWT Signing Key 尚未配置。請由部署人員按「JWT設定」填入 BMS SourceProvider token 參數。", "Token 設定不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return false;
        }

        try
        {
            string token = JwtTokenGenerator.GenerateSourceProviderToken(
                _settings.JwtNameIdentifier,
                _settings.JwtSerialNumber,
                _settings.JwtIssuer,
                _settings.JwtAudience,
                _settings.JwtSigningKey,
                _settings.JwtLifetimeMinutes);
            _settings.BmsToken = token;
            if (txtBmsToken != null)
            {
                txtBmsToken.UseSystemPasswordChar = true;
                txtBmsToken.Text = token;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (showError)
            {
                MessageBox.Show(ex.Message, "Token 設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return false;
        }
    }

    private async void BtnJwtSettings_Click(object? sender, EventArgs e)
    {
        bool restartDispatcher = _bmsApiClient.IsRunning;
        if (_bmsApiClient.IsRunning)
        {
            await _bmsApiClient.StopAsync();
        }

        using JwtSettingsDialog dialog = new(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            if (restartDispatcher)
            {
                await EnsureBmsDispatcherStartedAsync(showErrors: false);
            }

            return;
        }

        TryRefreshGeneratedToken(showError: true);
        _settings.Save();
        UpdateApiUiState();
        AppendLog(null, "SYS", "JWT 設定已儲存並重新產生 Token。");
        await EnsureBmsDispatcherStartedAsync(showErrors: false);
    }

    private void BtnCopyToken_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.BmsToken))
        {
            MessageBox.Show("目前尚未產生 Token。請先確認 SQLite 設定已配置訊源商 JWT Signing Key。", "Token 尚未產生", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Clipboard.SetText(_settings.BmsToken);
        AppendLog(null, "SYS", "Token 已複製到剪貼簿。");
    }

    private void BtnConnectionModeSettings_Click(object? sender, EventArgs e)
    {
        using ConnectionModeSettingsDialog dialog = new(_settings.ConnectionMode);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string selectedMode = ShoeConnectionMode.Normalize(dialog.SelectedConnectionMode);
        string currentMode = ShoeConnectionMode.Normalize(_settings.ConnectionMode);
        if (string.Equals(selectedMode, currentMode, StringComparison.OrdinalIgnoreCase))
        {
            RefreshConnectionModeUi();
            return;
        }

        bool hasPhysicalConnection = _endpoints.Any(endpoint => endpoint.IsConnected && !endpoint.MockMode);
        if (hasPhysicalConnection)
        {
            MessageBox.Show(
                this,
                "目前已有實體牌盒連線中。請先斷線，再切換整台 Bridge 的連線方式。",
                "連線方式",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ApplyGlobalConnectionMode(selectedMode);
        _settings.Save();
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            RefreshEndpointGridRow(endpoint);
        }

        ShoeEndpoint? selectedEndpoint = SelectedEndpoint;
        if (selectedEndpoint != null)
        {
            RefreshSelectedEndpointView(selectedEndpoint);
        }
        else
        {
            RefreshEndpointConnectionControls(null);
        }

        AppendLog(null, "SYS", $"全域連線方式已切換為 {FormatConnectionModeText(selectedMode)}。");
    }

    private void SetApiSettingsEditMode(bool editMode)
    {
        _apiSettingsEditMode = editMode;
        UpdateApiUiState();
    }

    private async Task ApplyApiSettingsAsync()
    {
        _settings.BmsUrl = txtBmsUrl?.Text.Trim() ?? string.Empty;
        _settings.Save();
        SetApiSettingsEditMode(false);
        AppendLog(null, "SYS", "BMS 事件 API 設定已套用。");
        await RestartBmsDispatcherAsync(showErrors: false);
    }

    private void CancelApiSettingsEdit()
    {
        if (txtBmsUrl != null)
        {
            txtBmsUrl.Text = _settings.BmsUrl;
        }

        TryRefreshGeneratedToken(showError: false);
        SetApiSettingsEditMode(false);
        AppendLog(null, "SYS", "BMS 事件 API 編輯已取消。");
    }

    private async Task RestartBmsDispatcherAsync(bool showErrors)
    {
        if (_bmsApiClient.IsRunning)
        {
            await _bmsApiClient.StopAsync();
        }

        await EnsureBmsDispatcherStartedAsync(showErrors);
    }

    private Task EnsureBmsDispatcherStartedAsync(bool showErrors)
    {
        if (_bmsApiClient.IsRunning)
        {
            return Task.CompletedTask;
        }

        string url = txtBmsUrl?.Text.Trim() ?? _settings.BmsUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.CompletedTask;
        }

        if (_eventJournal == null)
        {
            if (showErrors)
            {
                MessageBox.Show("SQLite 事件 Journal 未啟用，無法傳送事件。請先確認資料庫檔案可建立或可寫入。", "Outbox 未啟用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return Task.CompletedTask;
        }

        if (!TryRefreshGeneratedToken(showErrors))
        {
            return Task.CompletedTask;
        }

        try
        {
            _settings.BmsUrl = url;
            _settings.Save();
            BmsApiSettings apiSettings = new(_settings.BmsUrl, _settings.BmsToken);
            _bmsApiClient.Start(
                apiSettings,
                _eventJournal,
                IsEventDispatchEnabled,
                BuildHeartbeatSnapshot,
                HandleBmsCommandAsync);
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show($"事件 API 設定失敗：{ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            AppendLog(null, "ERR", ex.Message);
        }
        finally
        {
            UpdateApiUiState();
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<AngelBridgeHeartbeatEndpointStatus> BuildHeartbeatSnapshot()
    {
        return _endpoints.Select(endpoint =>
        {
            BridgeOutboxStatus outboxStatus = GetEndpointOutboxStatus(endpoint);
            return new AngelBridgeHeartbeatEndpointStatus
            {
                DeskName = endpoint.DeskName,
                SourceDataCode = endpoint.SourceDataCode,
                SourceDataId = endpoint.SourceDataId,
                ShoeId = endpoint.ShoeId,
                DeviceId = endpoint.DeviceId,
                ComPort = endpoint.ConnectionDisplay,
                ConnectionMode = endpoint.ConnectionMode,
                MoxaHost = endpoint.IsMoxaTcpMode ? endpoint.MoxaHost : string.Empty,
                MoxaPort = endpoint.IsMoxaTcpMode ? endpoint.MoxaPort : null,
                Enabled = endpoint.Enabled,
                BmsTransmitEnabled = endpoint.BmsTransmitEnabled,
                MockMode = endpoint.MockMode,
                Status = endpoint.StatusText,
                Shoe = endpoint.CurrentShoe,
                Round = endpoint.CurrentRound,
                RoundId = endpoint.CurrentRoundId,
                PendingOutboxCount = outboxStatus.PendingCount,
                FailedOutboxCount = outboxStatus.FailedCount,
                LastEvent = endpoint.LastEventText
            };
        }).ToList();
    }

    private async Task<BridgeCommandHandlingResult> HandleBmsCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_eventJournal == null)
        {
            return BridgeCommandHandlingResult.Rejected("SQLite event journal is not available.");
        }

        if (!string.IsNullOrWhiteSpace(command.CommandId))
        {
            if (_handledBmsCommandIds.Contains(command.CommandId))
            {
                return BridgeCommandHandlingResult.Handled("Command was already handled in this bridge session.");
            }
        }

        string type = command.Type.Trim();
        BridgeCommandHandlingResult result = type switch
        {
            "RecoverRound" => await HandleRecoverRoundCommandAsync(command, cancellationToken).ConfigureAwait(false),
            "ResendEvent" => await HandleResendEventCommandAsync(command, cancellationToken).ConfigureAwait(false),
            _ => BridgeCommandHandlingResult.Rejected($"Unsupported command type: {command.Type}")
        };

        if (result.Success && !string.IsNullOrWhiteSpace(command.CommandId))
        {
            _handledBmsCommandIds.Add(command.CommandId);
        }

        return result;
    }

    private async Task<BridgeCommandHandlingResult> HandleRecoverRoundCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.Shoe.HasValue || !command.Round.HasValue)
        {
            return BridgeCommandHandlingResult.Rejected("RecoverRound requires shoe and round.");
        }

        ShoeEndpoint? endpoint = FindEndpointForCommand(command);
        if (endpoint == null)
        {
            return BridgeCommandHandlingResult.NotFound("Target endpoint was not found.");
        }

        if (!endpoint.BmsTransmitEnabled)
        {
            return BridgeCommandHandlingResult.Rejected("Target endpoint has BMS transmission disabled.");
        }

        BridgeEventQuery query = new()
        {
            Type = "GameResult",
            SourceDataCode = endpoint.SourceDataCode,
            DeviceId = endpoint.DeviceId,
            Shoe = command.Shoe,
            Round = command.Round,
            RoundId = command.RoundId,
            Limit = 1
        };

        int count = await _eventJournal!
            .RequeueMatchingEventsAsync(query, DateTime.UtcNow, $"BMS command {command.CommandId} RecoverRound")
            .ConfigureAwait(false);
        if (count <= 0)
        {
            AppendLog(endpoint, "API", $"BMS 補償要求找不到 GameResult {command.Shoe}/{command.Round}。");
            return BridgeCommandHandlingResult.NotFound($"GameResult {command.Shoe}/{command.Round} was not found locally.");
        }

        AppendLog(endpoint, "API", $"BMS 補償要求已重新排送 GameResult {command.Shoe}/{command.Round}。");
        _ = RefreshOutboxStatusesAsync();
        return BridgeCommandHandlingResult.Handled($"Requeued {count} GameResult event(s).");
    }

    private async Task<BridgeCommandHandlingResult> HandleResendEventCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.EventId.HasValue)
        {
            int requeuedById = await _eventJournal!
                .RequeueEventAsync(command.EventId.Value, DateTime.UtcNow, $"BMS command {command.CommandId} ResendEvent")
                .ConfigureAwait(false);
            if (requeuedById <= 0)
            {
                return BridgeCommandHandlingResult.NotFound($"EventId {command.EventId.Value} was not found locally.");
            }

            AppendLog(FindEndpointForCommand(command), "API", $"BMS 要求重送事件 #{command.EventId.Value}，已重新排送。");
            _ = RefreshOutboxStatusesAsync();
            return BridgeCommandHandlingResult.Handled($"Requeued event #{command.EventId.Value}.");
        }

        ShoeEndpoint? endpoint = FindEndpointForCommand(command);
        if (endpoint == null)
        {
            return BridgeCommandHandlingResult.NotFound("Target endpoint was not found.");
        }

        if (!endpoint.BmsTransmitEnabled)
        {
            return BridgeCommandHandlingResult.Rejected("Target endpoint has BMS transmission disabled.");
        }

        if (string.IsNullOrWhiteSpace(command.EventType) && (!command.Shoe.HasValue || !command.Round.HasValue))
        {
            return BridgeCommandHandlingResult.Rejected("ResendEvent requires eventId or eventType with shoe and round.");
        }

        BridgeEventQuery query = new()
        {
            Type = command.EventType,
            SourceDataCode = endpoint.SourceDataCode,
            DeviceId = endpoint.DeviceId,
            Shoe = command.Shoe,
            Round = command.Round,
            RoundId = command.RoundId,
            Limit = 20
        };

        int requeuedCount = await _eventJournal!
            .RequeueMatchingEventsAsync(query, DateTime.UtcNow, $"BMS command {command.CommandId} ResendEvent")
            .ConfigureAwait(false);
        if (requeuedCount <= 0)
        {
            return BridgeCommandHandlingResult.NotFound("No matching local events were found.");
        }

        AppendLog(endpoint, "API", $"BMS 要求重送 {requeuedCount} 筆事件，已重新排送。");
        _ = RefreshOutboxStatusesAsync();
        return BridgeCommandHandlingResult.Handled($"Requeued {requeuedCount} event(s).");
    }

    private ShoeEndpoint? FindEndpointForCommand(AngelBridgeCommand command)
    {
        return _endpoints.FirstOrDefault(endpoint =>
            (string.IsNullOrWhiteSpace(command.SourceDataCode) ||
                string.Equals(endpoint.SourceDataCode, command.SourceDataCode, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(command.DeviceId) ||
                string.Equals(endpoint.DeviceId, command.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(endpoint.ShoeId, command.DeviceId, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsEventDispatchEnabled(BridgePendingEvent pending)
    {
        ShoeEndpoint? endpoint = _endpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceDataCode, pending.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.DeviceId, pending.DeviceId, StringComparison.OrdinalIgnoreCase));

        return endpoint?.BmsTransmitEnabled ?? true;
    }

    private void UpdateApiUiState()
    {
        if (txtBmsUrl != null)
        {
            txtBmsUrl.Enabled = _apiSettingsEditMode;
            txtBmsUrl.ReadOnly = !_apiSettingsEditMode;
            txtBmsUrl.TabStop = _apiSettingsEditMode;
            txtBmsUrl.BackColor = _apiSettingsEditMode ? Color.White : Color.FromArgb(248, 250, 252);
        }

        if (txtBmsToken != null)
        {
            txtBmsToken.Enabled = false;
            txtBmsToken.ReadOnly = true;
            txtBmsToken.TabStop = false;
            txtBmsToken.BackColor = Color.FromArgb(248, 250, 252);
        }

        if (btnJwtSettings != null) btnJwtSettings.Enabled = true;
        if (btnConnectionModeSettings != null) btnConnectionModeSettings.Enabled = true;
        if (btnCopyToken != null) btnCopyToken.Enabled = !string.IsNullOrWhiteSpace(_settings.BmsToken);
        if (btnApiEdit != null) btnApiEdit.Visible = !_apiSettingsEditMode;
        if (btnApiApply != null) btnApiApply.Visible = _apiSettingsEditMode;
        if (btnApiCancel != null) btnApiCancel.Visible = _apiSettingsEditMode;
    }

    private void AppendLog(ShoeEndpoint? endpoint, string type, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(endpoint, type, message)));
            return;
        }

        if (_logPaused && type != "SYS")
        {
            return;
        }

        UiLogEntry entry = new(DateTime.Now, endpoint, type, EnsureAngelLogTag(message));
        _uiLogEntries.Add(entry);
        if (_uiLogEntries.Count > MaxUiLogRows)
        {
            _uiLogEntries.RemoveAt(0);
        }

        if (lvLogs == null || !ShouldDisplayLogEntry(entry))
        {
            return;
        }

        AddLogListViewItem(entry, ensureVisible: true);
    }

    private bool ShouldDisplayLogEntry(UiLogEntry entry)
    {
        return _logEndpointFilter == null ||
            ReferenceEquals(entry.Endpoint, _logEndpointFilter);
    }

    private void RenderLogEntries()
    {
        if (lvLogs == null)
        {
            return;
        }

        lvLogs.BeginUpdate();
        try
        {
            lvLogs.Items.Clear();
            foreach (UiLogEntry entry in _uiLogEntries)
            {
                if (ShouldDisplayLogEntry(entry))
                {
                    AddLogListViewItem(entry, ensureVisible: false);
                }
            }

            if (lvLogs.Items.Count > 0)
            {
                lvLogs.Items[^1].EnsureVisible();
            }
        }
        finally
        {
            lvLogs.EndUpdate();
        }
    }

    private void AddLogListViewItem(UiLogEntry entry, bool ensureVisible)
    {
        if (lvLogs == null)
        {
            return;
        }

        ListViewItem item = new(entry.Timestamp.ToString("HH:mm:ss.fff"));
        item.SubItems.Add(entry.Endpoint?.SourceDataCode ?? "-");
        item.SubItems.Add(entry.Endpoint?.ShoeId ?? "-");
        item.SubItems.Add(entry.Endpoint?.CurrentShoe.ToString(CultureInfo.InvariantCulture) ?? "-");
        item.SubItems.Add(entry.Endpoint?.CurrentRound.ToString(CultureInfo.InvariantCulture) ?? "-");
        item.SubItems.Add(entry.Type);
        item.SubItems.Add(entry.Message);

        item.ForeColor = entry.Type switch
        {
            "RX" => Color.Navy,
            "RXRAW" => Color.FromArgb(36, 113, 163),
            "TX" => Color.DarkGreen,
            "ACK" => Color.DarkGreen,
            "NAK" => Color.Firebrick,
            "ERR" => Color.Firebrick,
            "EVENT" => Color.Purple,
            "SIM" => Color.DarkCyan,
            "API" => Color.DarkSlateBlue,
            _ => Color.FromArgb(60, 65, 70)
        };

        lvLogs.Items.Add(item);
        if (lvLogs.Items.Count > MaxUiLogRows)
        {
            lvLogs.Items.RemoveAt(0);
        }

        if (ensureVisible)
        {
            item.EnsureVisible();
        }
    }

    private static string EnsureAngelLogTag(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AngelLogTag;
        }

        return message.Contains(AngelLogTag, StringComparison.Ordinal)
            ? message
            : $"{AngelLogTag} {message}";
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        StopAutoRun();
        CancelPendingNextRoundCountdown();
        _outboxStatusTimer.Stop();
        _nextRoundCountdownTimer.Stop();
        _settings.Save();
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            endpoint.Disconnect();
        }

        if (_bmsApiClient.IsRunning)
        {
            _bmsApiClient.StopAsync().GetAwaiter().GetResult();
        }

        _bmsApiClient.Dispose();
        _outboxStatusTimer.Dispose();
        _nextRoundCountdownTimer.Dispose();
    }

    private void Form1_Shown(object? sender, EventArgs e)
    {
        if (mainSplit != null && !mainSplit.Panel2Collapsed)
        {
            try
            {
                PositionEventLogBelowEndpointList();
            }
            catch (Exception ex)
            {
                AppendLog(null, "ERR", $"Layout SplitterDistance error: {ex.Message}");
            }
        }
    }

    private void MainSplit_SplitterMoving(object? sender, SplitterCancelEventArgs e)
    {
        if (mainSplit == null || mainSplit.Panel2Collapsed)
        {
            return;
        }

        (int minimumDistance, int maximumDistance) = GetMainSplitterBounds();
        if (e.SplitY < minimumDistance)
        {
            e.Cancel = true;
            ApplyMainSplitterBounds(minimumDistance);
        }
        else if (e.SplitY > maximumDistance)
        {
            e.Cancel = true;
            ApplyMainSplitterBounds(maximumDistance);
        }
    }

    private void ApplyMainSplitterBounds(int? requestedDistance = null)
    {
        if (mainSplit == null || mainSplit.Panel2Collapsed || mainSplit.Height <= mainSplit.SplitterWidth)
        {
            return;
        }

        try
        {
            (int minimumDistance, int maximumDistance) = GetMainSplitterBounds();
            int desiredDistance = requestedDistance ?? mainSplit.SplitterDistance;
            int clampedDistance = Math.Min(Math.Max(desiredDistance, minimumDistance), maximumDistance);
            int panel2MinSize = Math.Max(0, mainSplit.Height - mainSplit.SplitterWidth - maximumDistance);

            if (mainSplit.Panel1MinSize > minimumDistance)
            {
                mainSplit.Panel1MinSize = minimumDistance;
            }

            if (mainSplit.Panel2MinSize > panel2MinSize)
            {
                mainSplit.Panel2MinSize = panel2MinSize;
            }

            if (mainSplit.SplitterDistance != clampedDistance)
            {
                mainSplit.SplitterDistance = clampedDistance;
            }

            if (mainSplit.Panel1MinSize != minimumDistance)
            {
                mainSplit.Panel1MinSize = minimumDistance;
            }

            if (mainSplit.Panel2MinSize != panel2MinSize)
            {
                mainSplit.Panel2MinSize = panel2MinSize;
            }
        }
        catch (InvalidOperationException)
        {
            // 視窗縮到極小時 SplitContainer 可能短暫沒有合法範圍，下一次 Resize 會重新套用。
        }
        catch (ArgumentOutOfRangeException)
        {
            // 同上，避免使用者拖曳或 DPI 變更時讓 UI 事件中斷。
        }
    }

    private (int MinimumDistance, int MaximumDistance) GetMainSplitterBounds()
    {
        if (mainSplit == null)
        {
            return (0, 0);
        }

        int availableHeight = Math.Max(0, mainSplit.Height - mainSplit.SplitterWidth);
        if (availableHeight == 0)
        {
            return (0, 0);
        }

        int minimumDistance = Math.Min(GetCompactEndpointListHeight(), Math.Max(0, availableHeight - MinimumEventLogHeight));
        int maximumDistance = Math.Max(minimumDistance, availableHeight - MinimumEventLogHeight);
        return (minimumDistance, maximumDistance);
    }

    private void PositionEventLogBelowEndpointList()
    {
        if (mainSplit == null || mainSplit.Panel2Collapsed)
        {
            return;
        }

        mainSplit.PerformLayout();
        ApplyMainSplitterBounds(GetCompactEndpointListHeight());

        // Row heights and scrollbars finish laying out after the collapsed panel is restored.
        BeginInvoke(new Action(() =>
        {
            if (mainSplit != null && !mainSplit.Panel2Collapsed && !IsDisposed)
            {
                ApplyMainSplitterBounds(GetCompactEndpointListHeight());
            }
        }));
    }

    private int GetCompactEndpointListHeight()
    {
        if (dgvEndpoints == null)
        {
            return ProtectedMainContentHeight;
        }

        int rowHeight = dgvEndpoints.Rows.GetRowsHeight(DataGridViewElementStates.Visible);
        int headerHeight = dgvEndpoints.ColumnHeadersVisible ? dgvEndpoints.ColumnHeadersHeight : 0;
        int horizontalScrollHeight = dgvEndpoints.Controls.OfType<HScrollBar>().Any(scrollBar => scrollBar.Visible)
            ? SystemInformation.HorizontalScrollBarHeight
            : 0;
        int groupHeaderHeight = endpointListGroup?.DisplayRectangle.Top ?? 22;
        int layoutPadding = endpointListLayout?.Padding.Vertical ?? 16;
        int toolbarHeight = endpointListLayout?.RowStyles.Count > 1
            ? (int)Math.Ceiling(endpointListLayout.RowStyles[1].Height)
            : endpointListToolbar?.PreferredSize.Height ?? 38;
        int controlMargins = dgvEndpoints.Margin.Vertical + (endpointListToolbar?.Margin.Vertical ?? 0);

        return Math.Max(
            ProtectedMainContentHeight,
            groupHeaderHeight + layoutPadding + headerHeight + rowHeight + horizontalScrollHeight + toolbarHeight + controlMargins + 2);
    }

    private static Label CreateTopFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 6, 0),
            ForeColor = Color.FromArgb(40, 55, 70),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Regular)
        };
    }

    private static void AddLabeledControl(FlowLayoutPanel panel, string labelText, Control control)
    {
        Label label = new()
        {
            Text = labelText,
            AutoSize = true,
            Padding = new Padding(8, 6, 2, 0),
            Font = new Font("Microsoft JhengHei", 9.5F, FontStyle.Regular)
        };
        panel.Controls.Add(label);
        panel.Controls.Add(control);
    }
}

/// <summary>
/// Represents one baccarat card currently displayed in the bridge preview panel.
/// </summary>
public class BaccaratCard
{
    /// <summary>Card suit name.</summary>
    public string Suit { get; set; } = string.Empty;

    /// <summary>Card rank value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Draw index reported by the shoe.</summary>
    public int Index { get; set; }

    /// <summary>True when the card belongs to the player side.</summary>
    public bool IsPlayer { get; set; }
}

/// <summary>
/// One status chip shown above the live baccarat preview.
/// </summary>
/// <param name="Text">Status text.</param>
/// <param name="AccentColor">Chip accent color.</param>
public sealed record PreviewStatusItem(string Text, Color AccentColor);

/// <summary>
/// Draws live baccarat cards, result banner, betting countdown, and shoe status chips.
/// </summary>
public class BaccaratPreviewPanel : Panel
{
    private const int StatusAreaHeight = 88;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<BaccaratCard> PlayerCards { get; } = [];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<BaccaratCard> BankerCards { get; } = [];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string GameResultText { get; set; } = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Color GameResultColor { get; set; } = Color.White;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<PreviewStatusItem> StatusItems { get; } = [];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool CountdownActive { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int CountdownTotalSeconds { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int CountdownRemainingSeconds { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ResultHoldActive { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int ResultHoldTotalMilliseconds { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int ResultHoldRemainingMilliseconds { get; set; }

    /// <summary>
    /// Creates a double-buffered baccarat preview panel.
    /// </summary>
    public BaccaratPreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 40, 45);
    }

    /// <summary>
    /// Paints the live preview panel.
    /// </summary>
    /// <param name="e">Paint event arguments.</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        DrawStatusArea(g);
        int contentTop = StatusAreaHeight;

        using Pen separatorPen = new(Color.FromArgb(50, 70, 80), 2);
        g.DrawLine(separatorPen, Width / 2, contentTop + 10, Width / 2, Height - 10);

        DrawSide(g, "PLAYER (閒)", PlayerCards, 10, Width / 2 - 20, contentTop, Color.FromArgb(74, 138, 244));
        DrawSide(g, "BANKER (莊)", BankerCards, Width / 2 + 10, Width - 20, contentTop, Color.FromArgb(244, 74, 74));

        if (!string.IsNullOrEmpty(GameResultText))
        {
            int bannerH = 40;
            int bannerY = Height - bannerH - 12;
            using Brush bannerBg = new SolidBrush(Color.FromArgb(220, 20, 25, 30));
            g.FillRectangle(bannerBg, 15, bannerY, Width - 30, bannerH);

            using Font fontResult = new("Microsoft JhengHei", 13F, FontStyle.Bold);
            using Brush brushResult = new SolidBrush(GameResultColor);
            SizeF size = g.MeasureString(GameResultText, fontResult);
            float drawX = (Width - size.Width) / 2;
            float drawY = bannerY + (bannerH - size.Height) / 2;
            g.DrawString(GameResultText, fontResult, brushResult, drawX, drawY);

            if (ResultHoldActive && ResultHoldTotalMilliseconds > 0)
            {
                DrawResultHoldCountdown(g, new Rectangle(15, bannerY - 10, Width - 30, 6));
            }
        }
    }

    private void DrawStatusArea(Graphics g)
    {
        Rectangle area = new(0, 0, Width, StatusAreaHeight);
        using Brush bg = new SolidBrush(Color.FromArgb(36, 49, 55));
        g.FillRectangle(bg, area);

        if (CountdownActive && CountdownTotalSeconds > 0)
        {
            DrawCountdown(g, new Rectangle(14, 10, Math.Max(120, Width - 28), 24));
        }

        int y = CountdownActive ? 44 : 14;
        int x = 14;
        int maxX = Width - 14;
        using Font chipFont = new("Microsoft JhengHei", 9F, FontStyle.Bold);

        foreach (PreviewStatusItem item in StatusItems)
        {
            string text = item.Text.Length <= 34 ? item.Text : item.Text[..34] + "...";
            Size textSize = TextRenderer.MeasureText(text, chipFont, new Size(260, 24), TextFormatFlags.SingleLine);
            int chipW = Math.Min(280, Math.Max(82, textSize.Width + 22));
            if (x + chipW > maxX)
            {
                x = 14;
                y += 28;
            }

            if (y + 24 > StatusAreaHeight - 4)
            {
                break;
            }

            DrawChip(g, new Rectangle(x, y, chipW, 24), text, item.AccentColor, chipFont);
            x += chipW + 8;
        }

        using Pen divider = new(Color.FromArgb(60, 78, 86), 1);
        g.DrawLine(divider, 0, StatusAreaHeight - 1, Width, StatusAreaHeight - 1);
    }

    private void DrawCountdown(Graphics g, Rectangle bounds)
    {
        int remaining = Math.Clamp(CountdownRemainingSeconds, 0, CountdownTotalSeconds);
        float ratio = CountdownTotalSeconds <= 0 ? 0 : remaining / (float)CountdownTotalSeconds;
        int fillW = Math.Max(0, (int)Math.Round(bounds.Width * ratio));

        using Brush trackBrush = new SolidBrush(Color.FromArgb(24, 32, 36));
        using Brush fillBrush = new SolidBrush(Color.FromArgb(241, 196, 15));
        using Pen borderPen = new(Color.FromArgb(114, 92, 10), 1);
        g.FillRectangle(trackBrush, bounds);
        if (fillW > 0)
        {
            g.FillRectangle(fillBrush, new Rectangle(bounds.X, bounds.Y, fillW, bounds.Height));
        }

        g.DrawRectangle(borderPen, bounds);

        string text = $"下注倒數 {remaining} 秒";
        using Font font = new("Microsoft JhengHei", 10F, FontStyle.Bold);
        Color textColor = remaining <= 5 ? Color.FromArgb(255, 255, 255) : Color.FromArgb(20, 28, 32);
        TextRenderer.DrawText(
            g,
            text,
            font,
            bounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private void DrawResultHoldCountdown(Graphics g, Rectangle bounds)
    {
        int remaining = Math.Clamp(ResultHoldRemainingMilliseconds, 0, ResultHoldTotalMilliseconds);
        float ratio = ResultHoldTotalMilliseconds <= 0 ? 0 : remaining / (float)ResultHoldTotalMilliseconds;
        int fillW = Math.Max(0, (int)Math.Round(bounds.Width * ratio));

        using Brush trackBrush = new SolidBrush(Color.FromArgb(80, 12, 16, 18));
        using Brush fillBrush = new SolidBrush(Color.FromArgb(230, GameResultColor));
        using Pen borderPen = new(Color.FromArgb(110, 90, 100, 105), 1);
        g.FillRectangle(trackBrush, bounds);
        if (fillW > 0)
        {
            g.FillRectangle(fillBrush, new Rectangle(bounds.X, bounds.Y, fillW, bounds.Height));
        }
        g.DrawRectangle(borderPen, bounds);

        int seconds = Math.Max(1, (int)Math.Ceiling(remaining / 1000d));
        string text = $"{seconds}";
        using Font font = new("Microsoft JhengHei", 8F, FontStyle.Bold);
        Rectangle textBounds = new(bounds.Right - 34, bounds.Y - 12, 34, 12);
        TextRenderer.DrawText(
            g,
            text,
            font,
            textBounds,
            Color.FromArgb(210, 230, 235, 238),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static void DrawChip(Graphics g, Rectangle bounds, string text, Color accentColor, Font font)
    {
        using Brush chipBg = new SolidBrush(Color.FromArgb(52, 67, 74));
        using Brush accent = new SolidBrush(accentColor);
        using Pen border = new(Color.FromArgb(82, 101, 110), 1);
        g.FillRectangle(chipBg, bounds);
        g.FillRectangle(accent, bounds.X, bounds.Y, 5, bounds.Height);
        g.DrawRectangle(border, bounds);

        Rectangle textBounds = new(bounds.X + 10, bounds.Y, bounds.Width - 12, bounds.Height);
        TextRenderer.DrawText(
            g,
            text,
            font,
            textBounds,
            Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void DrawSide(Graphics g, string title, List<BaccaratCard> cards, int left, int right, int top, Color sideColor)
    {
        int score = CalculateScore(cards);
        string scoreText = cards.Count > 0 ? $"{score} 點" : "--";

        using Font fontTitle = new("Microsoft JhengHei", 11F, FontStyle.Bold);
        using Font fontScore = new("Microsoft JhengHei", 20F, FontStyle.Bold);
        using Brush brushTitle = new SolidBrush(sideColor);
        using Brush brushScore = new SolidBrush(Color.White);
        g.DrawString(title, fontTitle, brushTitle, left + 10, top + 10);
        g.DrawString(scoreText, fontScore, brushScore, left + 10, top + 32);

        int availableWidth = Math.Max(120, right - left);
        int startX = left + Math.Max(18, Math.Min(42, (availableWidth - 190) / 2));
        int startY = top + 76;
        for (int i = 0; i < cards.Count; i++)
        {
            DrawCard(g, cards[i], startX, startY);
            startX += i == 0 ? 50 : 60;
        }
    }

    private static void DrawCard(Graphics g, BaccaratCard card, int x, int y)
    {
        bool isThird = card.Index == 3;
        System.Drawing.Drawing2D.GraphicsState state = g.Save();

        if (isThird)
        {
            g.TranslateTransform(x + 42, y + 30);
            g.RotateTransform(90);
            g.TranslateTransform(-30, -42);
        }
        else
        {
            g.TranslateTransform(x, y);
        }

        using (Brush cardBg = new SolidBrush(Color.White))
        using (Pen cardBorder = new(Color.FromArgb(80, 80, 80), 1.5f))
        {
            System.Drawing.Drawing2D.GraphicsPath path = new();
            int r = 6;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(60 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(60 - r * 2, 85 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 85 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseAllFigures();
            g.FillPath(cardBg, path);
            g.DrawPath(cardBorder, path);
        }

        string suitSymbol = card.Suit switch
        {
            "Spade" => "♠",
            "Heart" => "♥",
            "Club" => "♣",
            "Diamond" => "♦",
            _ => string.Empty
        };
        Color textColor = card.Suit is "Heart" or "Diamond" ? Color.FromArgb(203, 32, 39) : Color.FromArgb(30, 30, 30);
        string value = card.Value;

        using Font fontSuitSmall = new("Arial", 11F, FontStyle.Regular);
        using Font fontSuitLarge = new("Arial", 22F, FontStyle.Regular);
        using Font fontValue = new("Arial", 10F, FontStyle.Bold);
        using Brush brushText = new SolidBrush(textColor);
        g.DrawString(value, fontValue, brushText, 3, 2);
        g.DrawString(suitSymbol, fontSuitSmall, brushText, 3, 15);
        g.DrawString(suitSymbol, fontSuitSmall, brushText, 45, 52);
        g.DrawString(value, fontValue, brushText, 42, 66);
        g.DrawString(suitSymbol, fontSuitLarge, brushText, 17, 26);

        g.Restore(state);
    }

    private static int CalculateScore(List<BaccaratCard> cards)
    {
        int sum = 0;
        foreach (BaccaratCard card in cards)
        {
            int val = card.Value switch
            {
                "A" => 1,
                "J" => 0,
                "Q" => 0,
                "K" => 0,
                "10" => 0,
                "None" => 0,
                "" => 0,
                _ => int.TryParse(card.Value, out int v) ? v : 0
            };
            sum += val;
        }

        return sum % 10;
    }
}

/// <summary>
/// Dialog used to edit the global physical shoe connection mode.
/// </summary>
public sealed class ConnectionModeSettingsDialog : Form
{
    private readonly RadioButton rbComPort = new()
    {
        Text = "COM Port",
        AutoSize = true,
        Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
    };
    private readonly RadioButton rbMoxaTcp = new()
    {
        Text = "MOXA TCP",
        AutoSize = true,
        Font = new Font("Microsoft JhengHei", 10F, FontStyle.Bold)
    };

    /// <summary>
    /// Gets the selected global connection mode.
    /// </summary>
    public string SelectedConnectionMode { get; private set; }

    /// <summary>
    /// Creates the connection mode dialog.
    /// </summary>
    /// <param name="currentMode">Current bridge connection mode.</param>
    public ConnectionModeSettingsDialog(string currentMode)
    {
        SelectedConnectionMode = ShoeConnectionMode.Normalize(currentMode);

        Text = "連線方式設定";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 380);
        Font = new Font("Microsoft JhengHei", 10F, FontStyle.Regular);
        BackColor = Color.FromArgb(240, 244, 248);

        rbComPort.Checked = string.Equals(SelectedConnectionMode, ShoeConnectionMode.ComPort, StringComparison.OrdinalIgnoreCase);
        rbMoxaTcp.Checked = string.Equals(SelectedConnectionMode, ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase);
        rbComPort.CheckedChanged += (_, _) =>
        {
            if (rbComPort.Checked)
            {
                rbMoxaTcp.Checked = false;
            }
        };
        rbMoxaTcp.CheckedChanged += (_, _) =>
        {
            if (rbMoxaTcp.Checked)
            {
                rbComPort.Checked = false;
            }
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        Label hint = new()
        {
            Dock = DockStyle.Fill,
            Text = "整台 Bridge 共用同一種連線方式；每張桌只設定自己的 COM Port，或 MOXA IP / TCP Port。",
            ForeColor = Color.FromArgb(40, 55, 70),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 0, 8)
        };

        TableLayoutPanel options = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
            Padding = new Padding(16, 14, 16, 14)
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.Controls.Add(BuildModeOption(rbComPort, "使用 Windows COM Port；也適用 NPort Real COM driver 已映射成 COM 的情境。"), 0, 0);
        options.Controls.Add(BuildModeOption(rbMoxaTcp, "Bridge 直接連 MOXA / NPort 的 IP:Port；不用 Windows COM Port 映射。"), 1, 0);

        Label warning = new()
        {
            Dock = DockStyle.Fill,
            Text = "切換前請先斷開實體牌盒連線。此設定不代表牌盒固定綁桌，桌台仍由來源桌碼與端點設定辨識。",
            ForeColor = Color.FromArgb(120, 40, 31),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 16, 0, 0)
        };

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        Button btnOk = new() { Text = "確定", Width = 86, Height = 30 };
        Button btnCancel = new() { Text = "取消", Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
        btnOk.Click += BtnOk_Click;
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);

        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(warning, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static Control BuildModeOption(RadioButton radioButton, string description)
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label descriptionLabel = new()
        {
            Dock = DockStyle.Fill,
            Text = description,
            ForeColor = Color.FromArgb(88, 100, 112),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 4, 12, 0)
        };

        panel.Controls.Add(radioButton, 0, 0);
        panel.Controls.Add(descriptionLabel, 0, 1);
        panel.Click += (_, _) => radioButton.Checked = true;
        descriptionLabel.Click += (_, _) => radioButton.Checked = true;
        return panel;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        SelectedConnectionMode = rbMoxaTcp.Checked ? ShoeConnectionMode.MoxaTcp : ShoeConnectionMode.ComPort;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>
/// Dialog used to edit the source provider JWT settings stored by the bridge.
/// </summary>
public sealed class JwtSettingsDialog : Form
{
    private readonly BridgeSettings _settings;
    private readonly TextBox txtNameIdentifier = new() { Width = 520 };
    private readonly TextBox txtSerialNumber = new() { Width = 520 };
    private readonly TextBox txtIssuer = new() { Width = 520 };
    private readonly TextBox txtAudience = new() { Width = 520 };
    private readonly TextBox txtSigningKey = new() { Width = 520, UseSystemPasswordChar = true };
    private readonly TextBox txtLifetimeMinutes = new() { Width = 120 };

    /// <summary>
    /// Creates a JWT settings dialog for the supplied bridge settings.
    /// </summary>
    /// <param name="settings">Bridge settings to edit.</param>
    public JwtSettingsDialog(BridgeSettings settings)
    {
        _settings = settings;

        Text = "JWT 設定";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(740, 380);
        Font = new Font("Microsoft JhengHei", 10F, FontStyle.Regular);
        BackColor = Color.FromArgb(240, 244, 248);

        txtNameIdentifier.Text = _settings.JwtNameIdentifier;
        txtSerialNumber.Text = _settings.JwtSerialNumber;
        txtIssuer.Text = _settings.JwtIssuer;
        txtAudience.Text = _settings.JwtAudience;
        txtSigningKey.Text = _settings.JwtSigningKey;
        txtLifetimeMinutes.Text = _settings.JwtLifetimeMinutes.ToString(CultureInfo.InvariantCulture);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        Label hint = new()
        {
            Dock = DockStyle.Fill,
            Text = "部署用設定。這些值必須對應 GCS SourceProviderTokens；Signing Key 必須與 BMS 端一致，否則 API 會回 401。",
            ForeColor = Color.FromArgb(120, 40, 31),
            TextAlign = ContentAlignment.MiddleLeft
        };

        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            AutoSize = true
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }

        AddField(fields, 0, "訊源商 ID:", txtNameIdentifier);
        AddField(fields, 1, "訊源商序號:", txtSerialNumber);
        AddField(fields, 2, "Issuer:", txtIssuer);
        AddField(fields, 3, "Audience:", txtAudience);
        AddField(fields, 4, "Signing Key:", txtSigningKey);
        AddField(fields, 5, "有效分鐘:", txtLifetimeMinutes);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        Button btnOk = new() { Text = "確定", Width = 86, Height = 30 };
        Button btnCancel = new() { Text = "取消", Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
        btnOk.Click += BtnOk_Click;
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);

        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(fields, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (IsMissing(txtNameIdentifier, "訊源商 ID")
            || IsMissing(txtSerialNumber, "訊源商序號")
            || IsMissing(txtIssuer, "Issuer")
            || IsMissing(txtAudience, "Audience")
            || IsMissing(txtSigningKey, "Signing Key"))
        {
            return;
        }

        if (!int.TryParse(txtLifetimeMinutes.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lifetime)
            || lifetime <= 0)
        {
            MessageBox.Show(this, "有效分鐘必須是大於 0 的整數。", "JWT 設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtLifetimeMinutes.Focus();
            return;
        }

        _settings.JwtNameIdentifier = txtNameIdentifier.Text.Trim();
        _settings.JwtSerialNumber = txtSerialNumber.Text.Trim();
        _settings.JwtIssuer = txtIssuer.Text.Trim();
        _settings.JwtAudience = txtAudience.Text.Trim();
        _settings.JwtSigningKey = txtSigningKey.Text.Trim();
        _settings.JwtLifetimeMinutes = lifetime;

        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddField(TableLayoutPanel fields, int row, string labelText, Control control)
    {
        Label label = new()
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 10, 0)
        };
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        fields.Controls.Add(label, 0, row);
        fields.Controls.Add(control, 1, row);
    }

    private bool IsMissing(TextBox textBox, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text))
        {
            return false;
        }

        MessageBox.Show(this, $"{fieldName} 不可空白。", "JWT 設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        textBox.Focus();
        return true;
    }
}

/// <summary>
/// Simple owner-drawn toggle switch used by the WinForms bridge UI.
/// </summary>
public class ToggleSwitch : CheckBox
{
    /// <summary>
    /// Creates the toggle switch and enables owner-drawn double buffering.
    /// </summary>
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer, true);
        Size = new Size(120, 25);
    }

    /// <summary>
    /// Paints the toggle track, thumb, and optional label text.
    /// </summary>
    /// <param name="pevent">Paint event arguments.</param>
    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics g = pevent.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.White);

        // Track dimensions
        int rectWidth = 40;
        int rectHeight = 20;
        int x = string.IsNullOrEmpty(Text) ? (Width - rectWidth) / 2 : 2;
        int y = (Height - rectHeight) / 2;

        // Path for rounded track
        System.Drawing.Drawing2D.GraphicsPath path = new();
        int r = rectHeight;
        path.AddArc(x, y, r, r, 90, 180);
        path.AddArc(x + rectWidth - r, y, r, r, 270, 180);
        path.CloseAllFigures();

        // Draw track
        Color trackColor = Checked ? Color.FromArgb(46, 204, 113) : Color.FromArgb(189, 195, 199);
        using (Brush brush = new SolidBrush(trackColor))
        {
            g.FillPath(brush, path);
        }

        // Draw thumb
        int thumbSize = rectHeight - 4;
        int thumbX = Checked ? x + rectWidth - thumbSize - 2 : x + 2;
        int thumbY = y + 2;
        using (Brush thumbBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(thumbBrush, thumbX, thumbY, thumbSize, thumbSize);
        }

        // Draw Text if present
        if (!string.IsNullOrEmpty(Text))
        {
            using (Brush textBrush = new SolidBrush(ForeColor))
            {
                SizeF textSize = g.MeasureString(Text, Font);
                float textX = x + rectWidth + 8;
                float textY = (Height - textSize.Height) / 2;
                g.DrawString(Text, Font, textBrush, textX, textY);
            }
        }
    }
}
