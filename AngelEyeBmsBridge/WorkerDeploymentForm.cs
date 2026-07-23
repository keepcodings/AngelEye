using System.Security.Cryptography.X509Certificates;

namespace AngelEyeBmsBridge;

/// <summary>Dedicated engineering-only Worker deployment surface.</summary>
public sealed class WorkerDeploymentForm : Form
{
    private readonly IReadOnlyList<WorkerDeploymentTarget> _configuredTargets;
    private readonly WorkerDeploymentCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGridView _targets = CreateTargetGrid();
    private readonly DataGridView _stages = CreateStageGrid();
    private readonly TextBox _sshUser = CreateTextBox("angeldeploy");
    private readonly TextBox _privateKey = CreateTextBox();
    private readonly TextBox _releaseFolder = CreateTextBox();
    private readonly TextBox _certificate = CreateTextBox();
    private readonly TextBox _releaseIdentity = CreateReadOnlyTextBox();
    private readonly TextBox _deploymentId = CreateTextBox();
    private readonly Label _operationState = new()
    {
        Name = "DeploymentOperationState",
        Dock = DockStyle.Fill,
        Text = "請先配置 fingerprint、SSH key 與簽章憑證。",
        ForeColor = Color.DarkSlateGray,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Button _refreshButton = CreateButton("查詢狀態");
    private readonly Button _verifyButton = CreateButton("驗證套件");
    private readonly Button _preflightButton = CreateButton("上傳並 Preflight");
    private readonly Button _deployButton = CreateButton("確認部署");
    private readonly Button _recoverButton = CreateButton("恢復操作狀態");
    private readonly Button _rollbackButton = CreateButton("人工 Rollback");
    private WorkerReleaseBundle? _release;
    private WorkerDeploymentPlan? _plan;
    private bool _busy;

    public WorkerDeploymentForm(
        IReadOnlyList<WorkerDeploymentTarget>? targets = null,
        WorkerDeploymentCoordinator? coordinator = null)
    {
        Text = "ANGEL Worker 部署管理（工程模式）";
        Font = new Font("Microsoft JhengHei", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        Size = new Size(1540, 940);
        BackColor = Color.FromArgb(240, 244, 248);

        _configuredTargets = targets ?? LoadTargetConfiguration();
        _coordinator = coordinator ?? new WorkerDeploymentCoordinator(
            new SshNetWorkerDeploymentSessionFactory(),
            new FileWorkerDeploymentAuditStore(
                FileWorkerDeploymentAuditStore.DefaultPath));

        BuildLayout();
        LoadTargets();
        WireEvents();
        UpdateActionState();
    }

    private static IReadOnlyList<WorkerDeploymentTarget> LoadTargetConfiguration()
    {
        try
        {
            return WorkerDeploymentTargetConfiguration.Load(
                WorkerDeploymentTargetConfiguration.DefaultPath);
        }
        catch
        {
            return WorkerDeploymentTargets.All;
        }
    }

    private void BuildLayout()
    {
        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(64, 38, 88),
            Padding = new Padding(18, 8, 18, 8)
        };
        header.Controls.Add(new Label
        {
            Name = "DeploymentModeBanner",
            Dock = DockStyle.Fill,
            Text = "Worker 部署管理／受控 SSH／非 Query Console　｜　QA .29 → 正式 .30/.31",
            ForeColor = Color.White,
            Font = new Font("Microsoft JhengHei", 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        });

        var vertical = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Size = new Size(1500, 820),
            SplitterDistance = 270,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 220,
            Panel2MinSize = 380
        };

        GroupBox targetGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = "固定部署目標與遠端狀態",
            Padding = new Padding(10)
        };
        targetGroup.Controls.Add(_targets);
        vertical.Panel1.Controls.Add(targetGroup);

        var lower = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(1500, 540),
            SplitterDistance = 570,
            Panel1MinSize = 500,
            Panel2MinSize = 500
        };
        lower.Panel1.Controls.Add(BuildOperationPanel());

        GroupBox logGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = "結構化部署階段（本機與遠端以 Deployment ID 對應）",
            Padding = new Padding(10)
        };
        logGroup.Controls.Add(_stages);
        lower.Panel2.Controls.Add(logGroup);
        vertical.Panel2.Controls.Add(lower);

        Controls.Add(vertical);
        Controls.Add(header);
    }

    private Control BuildOperationPanel()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "受控操作",
            Padding = new Padding(12)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            Padding = new Padding(4)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        for (int row = 0; row < 8; row++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        }
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddField(table, 0, "SSH 使用者", _sshUser);
        AddField(
            table,
            1,
            "SSH 私鑰",
            _privateKey,
            CreateBrowseButton("選擇", () => SelectFile(_privateKey, "SSH 私鑰|*.*")));
        AddField(
            table,
            2,
            "Release 目錄",
            _releaseFolder,
            CreateBrowseButton("選擇", SelectReleaseFolder));
        AddField(
            table,
            3,
            "簽章憑證",
            _certificate,
            CreateBrowseButton(
                "選擇",
                () => SelectFile(
                    _certificate,
                    "憑證 (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|所有檔案|*.*")));
        AddField(table, 4, "套件身分", _releaseIdentity, _verifyButton);
        AddField(table, 5, "Deployment ID", _deploymentId);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        actions.Controls.AddRange(
        [
            _refreshButton,
            _preflightButton,
            _deployButton,
            _recoverButton,
            _rollbackButton
        ]);
        table.Controls.Add(new Label
        {
            Text = "操作",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 6);
        table.Controls.Add(actions, 1, 6);
        table.SetColumnSpan(actions, 2);

        var warning = new Label
        {
            Dock = DockStyle.Fill,
            Text =
                "正式環境無 override：相同 digest 必須先通過 QA；部署 .30 前會確認 .31 disabled/inactive。按下部署只會執行固定 root-owned script。",
            ForeColor = Color.DarkRed,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        table.Controls.Add(warning, 0, 7);
        table.SetColumnSpan(warning, 3);
        table.Controls.Add(_operationState, 0, 8);
        table.SetColumnSpan(_operationState, 3);
        group.Controls.Add(table);
        return group;
    }

    private static void AddField(
        TableLayoutPanel table,
        int row,
        string label,
        Control value,
        Control? action = null)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        value.Dock = DockStyle.Fill;
        table.Controls.Add(value, 1, row);
        if (action != null)
        {
            action.Dock = DockStyle.Fill;
            table.Controls.Add(action, 2, row);
        }
    }

    private void LoadTargets()
    {
        _targets.Columns.Add("target", "目標");
        _targets.Columns.Add("host", "SSH Host");
        _targets.Columns.Add("environment", "Environment");
        _targets.Columns.Add("role", "Role");
        _targets.Columns.Add("fingerprint", "Host Fingerprint");
        _targets.Columns.Add("version", "版本");
        _targets.Columns.Add("service", "Service");
        _targets.Columns.Add("health", "Health");
        _targets.Columns.Add("status", "狀態");

        foreach (WorkerDeploymentTarget target in _configuredTargets)
        {
            int index = _targets.Rows.Add(
                target.DisplayName,
                $"{target.Host}:{target.SshPort}",
                target.ExpectedEnvironment,
                target.ExpectedRole,
                target.HasPinnedFingerprint ? target.ExpectedFingerprint : "未配置",
                "-",
                "-",
                "-",
                "尚未連線");
            _targets.Rows[index].Tag = target;
        }
        if (_targets.Rows.Count > 0)
        {
            _targets.Rows[0].Selected = true;
        }
    }

    private void WireEvents()
    {
        _targets.SelectionChanged += (_, _) =>
        {
            _plan = null;
            UpdateActionState();
        };
        _refreshButton.Click += async (_, _) =>
            await RunBusyAsync(RefreshStatusAsync);
        _verifyButton.Click += async (_, _) =>
            await RunBusyAsync(VerifyReleaseAsync);
        _preflightButton.Click += async (_, _) =>
            await RunBusyAsync(PreflightAsync);
        _deployButton.Click += async (_, _) =>
            await RunBusyAsync(DeployAsync);
        _recoverButton.Click += async (_, _) =>
            await RunBusyAsync(RecoverAsync);
        _rollbackButton.Click += async (_, _) =>
            await RunBusyAsync(RollbackAsync);
        FormClosing += (_, _) => _lifetime.Cancel();
        FormClosed += (_, _) => _lifetime.Dispose();
    }

    private async Task RefreshStatusAsync()
    {
        WorkerDeploymentTarget target = SelectedTarget();
        WorkerDeploymentStatus status = await _coordinator.GetStatusAsync(
            target,
            Authentication(),
            _lifetime.Token);
        ShowStatus(target, status);
        SetState($"{target.DisplayName} 狀態已更新。", isError: false);
    }

    private async Task VerifyReleaseAsync()
    {
        string folder = _releaseFolder.Text.Trim();
        string artifact = Path.Combine(folder, "release.tar.gz");
        string manifest = Path.Combine(folder, "release-manifest.json");
        string signature = Path.Combine(folder, "release-manifest.p7s");
        using X509Certificate2 certificate =
            X509CertificateLoader.LoadCertificateFromFile(
                _certificate.Text.Trim());
        _release = await WorkerReleaseVerifier.VerifyAsync(
            artifact,
            manifest,
            signature,
            certificate,
            _lifetime.Token);
        _plan = null;
        _releaseIdentity.Text =
            $"{_release.Manifest.Version} / {_release.Manifest.BuildCommit} / " +
            $"{_release.Manifest.ArtifactSha256}";
        SetState("簽章、runtime 與 SHA-256 驗證成功。", isError: false);
    }

    private async Task PreflightAsync()
    {
        WorkerDeploymentTarget target = SelectedTarget();
        WorkerReleaseBundle release = _release
            ?? throw new InvalidOperationException("請先驗證 release 套件。");
        _plan = await _coordinator.PreflightAsync(
            target,
            Authentication(),
            release,
            _sshUser.Text.Trim(),
            _lifetime.Token);
        _deploymentId.Text = _plan.DeploymentId.ToString();
        SetState(
            $"Preflight 成功；Deployment ID {_plan.DeploymentId}。尚未送出 install。",
            isError: false);
    }

    private async Task DeployAsync()
    {
        WorkerDeploymentPlan plan = _plan
            ?? throw new InvalidOperationException("請先完成同一目標與套件的 preflight。");
        string roleWarning = plan.Target.IsStandby
            ? "Standby 安裝完成後必須保持 disabled/inactive。"
            : "部署期間 Worker 服務會短暫停止。";
        DialogResult confirmation = MessageBox.Show(
            this,
            $"即將部署 {plan.Release.Manifest.Version} 到 {plan.Target.DisplayName}。\n" +
            $"Digest: {plan.Release.Manifest.ArtifactSha256}\n" +
            $"Deployment ID: {plan.DeploymentId}\n\n{roleWarning}\n\n確定送出一次 install？",
            "確認 Worker 部署",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            SetState("已取消，未送出 install。", isError: false);
            return;
        }

        WorkerDeploymentOperationResult result =
            await _coordinator.InstallAsync(
                plan,
                Authentication(),
                _lifetime.Token);
        RenderProgress(result.Progress);
        ShowStatus(plan.Target, result.Status);
        SetState(
            result.RemoteResultKnown
                ? $"部署完成：{result.Status.Stage} / {result.Status.LastResult}"
                : "遠端結果未知；請使用恢復操作狀態。",
            isError: !result.RemoteResultKnown);
    }

    private async Task RecoverAsync()
    {
        WorkerDeploymentTarget target = SelectedTarget();
        DeploymentId deploymentId = ParseDeploymentId();
        WorkerDeploymentOperationResult result =
            await _coordinator.RecoverAsync(
                target,
                Authentication(),
                deploymentId,
                _lifetime.Token);
        RenderProgress(result.Progress);
        ShowStatus(target, result.Status);
        SetState(
            result.RemoteResultKnown
                ? $"已恢復遠端結果：{result.Status.Stage} / {result.Status.LastResult}"
                : "尚無可證明的遠端最終結果；不會重送 install。",
            isError: !result.RemoteResultKnown);
    }

    private async Task RollbackAsync()
    {
        WorkerDeploymentTarget target = SelectedTarget();
        DeploymentId deploymentId = ParseDeploymentId();
        string digest = _release?.Manifest.ArtifactSha256
            ?? throw new InvalidOperationException(
                "Rollback 前請載入原 deployment 使用的 release，核對 digest。");
        DialogResult confirmation = MessageBox.Show(
            this,
            $"要在 {target.DisplayName} 對已記錄的 {deploymentId} 執行 rollback？",
            "確認 Worker Rollback",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        WorkerDeploymentOperationResult result =
            await _coordinator.RollbackAsync(
                target,
                Authentication(),
                deploymentId,
                _sshUser.Text.Trim(),
                digest,
                _lifetime.Token);
        RenderProgress(result.Progress);
        ShowStatus(target, result.Status);
        SetState(
            result.RemoteResultKnown ? "Rollback 完成。" : "Rollback 失敗，請依人工復原位置處理。",
            isError: !result.RemoteResultKnown);
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        UpdateActionState();
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The form is closing. A submitted remote install is recovered by ID later.
        }
        catch (Exception exception)
        {
            SetState(exception.Message, isError: true);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                UpdateActionState();
            }
        }
    }

    private void ShowStatus(
        WorkerDeploymentTarget target,
        WorkerDeploymentStatus status)
    {
        DataGridViewRow row = _targets.Rows
            .Cast<DataGridViewRow>()
            .Single(item => ((WorkerDeploymentTarget)item.Tag!).Id == target.Id);
        row.Cells["version"].Value = status.CurrentVersion;
        row.Cells["service"].Value =
            $"{(status.ServiceEnabled ? "enabled" : "disabled")}/" +
            $"{(status.ServiceActive ? "active" : "inactive")}";
        row.Cells["health"].Value = status.Healthy ? "healthy" : "not healthy";
        row.Cells["status"].Value =
            $"{status.Stage}" +
            (string.IsNullOrWhiteSpace(status.LastResult)
                ? string.Empty
                : $" / {status.LastResult}");
    }

    private void RenderProgress(
        IEnumerable<WorkerDeploymentAuditEntry> progress)
    {
        foreach (WorkerDeploymentAuditEntry entry in progress)
        {
            _stages.Rows.Add(
                entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                entry.DeploymentId.ToString("D"),
                entry.TargetId,
                entry.Stage,
                entry.Status,
                entry.Message,
                entry.RollbackPerformed ? "是" : "否");
        }
        if (_stages.Rows.Count > 0)
        {
            _stages.FirstDisplayedScrollingRowIndex = _stages.Rows.Count - 1;
        }
    }

    private WorkerDeploymentAuthentication Authentication() =>
        new(_sshUser.Text.Trim(), _privateKey.Text.Trim());

    private WorkerDeploymentTarget SelectedTarget() =>
        _targets.SelectedRows.Count == 1 &&
        _targets.SelectedRows[0].Tag is WorkerDeploymentTarget target
            ? target
            : throw new InvalidOperationException("請選擇一個固定部署目標。");

    private DeploymentId ParseDeploymentId() =>
        Guid.TryParse(_deploymentId.Text.Trim(), out Guid value)
            ? DeploymentId.From(value)
            : throw new InvalidOperationException("Deployment ID 必須是有效 UUID。");

    private void SetState(string message, bool isError)
    {
        _operationState.Text = message;
        _operationState.ForeColor = isError ? Color.DarkRed : Color.DarkGreen;
    }

    private void UpdateActionState()
    {
        bool targetSelected = _targets.SelectedRows.Count == 1;
        _refreshButton.Enabled = !_busy && targetSelected;
        _verifyButton.Enabled = !_busy;
        _preflightButton.Enabled = !_busy && targetSelected && _release != null;
        _deployButton.Enabled = !_busy && _plan != null;
        _recoverButton.Enabled = !_busy && targetSelected;
        _rollbackButton.Enabled = !_busy && targetSelected && _release != null;
    }

    private static void SelectFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = filter
        };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            target.Text = dialog.FileName;
        }
    }

    private void SelectReleaseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description =
                "選擇包含 release.tar.gz、release-manifest.json 與 release-manifest.p7s 的目錄",
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _releaseFolder.Text = dialog.SelectedPath;
            _release = null;
            _plan = null;
            _releaseIdentity.Clear();
            UpdateActionState();
        }
    }

    private static Button CreateBrowseButton(string text, Action action)
    {
        Button button = CreateButton(text);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button CreateButton(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(90, 30),
            Margin = new Padding(3)
        };

    private static TextBox CreateTextBox(string text = "") =>
        new()
        {
            Text = text,
            BorderStyle = BorderStyle.FixedSingle
        };

    private static TextBox CreateReadOnlyTextBox() =>
        new()
        {
            ReadOnly = true,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

    private static DataGridView CreateTargetGrid() =>
        new()
        {
            Name = "WorkerDeploymentTargetGrid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Color.White
        };

    private static DataGridView CreateStageGrid()
    {
        var grid = new DataGridView
        {
            Name = "DeploymentStageGrid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Color.White
        };
        grid.Columns.Add("time", "時間");
        grid.Columns.Add("deploymentId", "Deployment ID");
        grid.Columns.Add("target", "目標");
        grid.Columns.Add("stage", "Stage");
        grid.Columns.Add("result", "結果");
        grid.Columns.Add("message", "訊息");
        grid.Columns.Add("rollback", "Rollback");
        return grid;
    }
}
