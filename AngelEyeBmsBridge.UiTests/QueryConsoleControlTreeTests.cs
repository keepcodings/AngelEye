using AngelEyeBmsBridge;
using System.Reflection;
using Xunit;

namespace AngelEyeBmsBridge.UiTests;

public sealed class QueryConsoleControlTreeTests
{
    [Fact]
    public void QueryConsole_ContainsExpectedReadOnlyAreas_AndNoOperationalWriteButtons()
    {
        RunSta(() =>
        {
            using QueryConsoleForm form = new();
            List<Control> controls = Descendants(form).ToList();
            string[] tabs = controls.OfType<TabPage>().Select(tab => tab.Text).ToArray();
            string[] buttons = controls.OfType<Button>().Select(button => button.Text).ToArray();

            Assert.Contains("總覽", tabs);
            Assert.Contains("牌局查詢", tabs);
            Assert.Contains("異常中心", tabs);
            Assert.Contains("MOXA 即時監看", tabs);
            Assert.Contains("技術資料（唯讀）", tabs);
            Assert.Contains("Events / Payload", tabs);
            Assert.Contains("Outbox", tabs);
            Assert.Contains("Recoveries", tabs);

            string[] forbidden =
            [
                "重送", "Lock", "Unlock", "鎖定", "解鎖", "清錯", "Clear",
                "Transmit", "傳送開關", "儲存設定", "主備切換",
                "部署", "Preflight", "Rollback", "SSH"
            ];
            Assert.DoesNotContain(buttons, text => forbidden.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("開始監看選取桌台", buttons);
            Assert.Contains("停止監看選取桌台", buttons);
            Label provenance = Assert.Single(controls.OfType<Label>()
                .Where(label => label.Name == "MoxaProvenanceLabel"));
            Assert.Contains("MOXA 直連", provenance.Text);
            Assert.Contains("session-local", provenance.Text);
            Assert.Contains("不送 BMS", provenance.Text);
            Assert.All(controls.OfType<TextBox>().Where(textBox => textBox.Multiline), textBox => Assert.True(textBox.ReadOnly));
        });
    }

    [Fact]
    public void QueryConsole_DoesNotOwnEngineeringOrDispatchObjects()
    {
        Type[] fieldTypes = typeof(QueryConsoleForm)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(fieldTypes, type => type.Name is "ShoeEndpoint" or "SerialListener" or "BridgeEventJournal" or "BmsApiClient");
        Assert.Contains(fieldTypes, type => type == typeof(TeleBetQueryClient));
    }

    [Fact]
    public void EngineeringPhysicalCommands_RemainBlockedUntilExplicitlyAuthorized()
    {
        Assert.False(EngineeringCommandPolicy.CanSend(mockMode: false, allowPhysicalShoeCommands: false));
        Assert.True(EngineeringCommandPolicy.CanSend(mockMode: true, allowPhysicalShoeCommands: false));
        Assert.True(EngineeringCommandPolicy.CanSend(mockMode: false, allowPhysicalShoeCommands: true));
    }

    [Fact]
    public void DeploymentMode_IsSeparateAndShowsOnlyTheThreeAllowListedTargets()
    {
        RunSta(() =>
        {
            using WorkerDeploymentForm form = new();
            List<Control> controls = Descendants(form).ToList();
            Assert.Contains(controls.OfType<Label>(), label =>
                label.Name == "DeploymentModeBanner" &&
                label.Text.Contains("非 Query Console", StringComparison.Ordinal));

            DataGridView grid = Assert.Single(controls.OfType<DataGridView>(), item =>
                item.Name == "WorkerDeploymentTargetGrid");
            Assert.Equal(3, grid.Rows.Count);
            Assert.Equal(
                new[] { "QA .29", "正式主用 .30", "正式備援 .31" },
                grid.Rows.Cast<DataGridViewRow>()
                    .Select(row => row.Cells[0].Value?.ToString())
                    .ToArray());
        });
    }

    [Theory]
    [InlineData(1920, 1200)]
    [InlineData(1180, 760)]
    public void DeploymentForm_MainAreasStayInsideClientAtSupportedSizes(
        int width,
        int height)
    {
        RunSta(() =>
        {
            using WorkerDeploymentForm form = new(
                WorkerDeploymentTargets.All,
                new WorkerDeploymentCoordinator(
                    new NeverConnectSessionFactory(),
                    new EmptyAuditStore()))
            {
                WindowState = FormWindowState.Normal,
                Size = new Size(width, height)
            };
            form.Show();
            form.PerformLayout();
            Application.DoEvents();

            List<Control> controls = Descendants(form).ToList();
            DataGridView targetGrid = Assert.Single(
                controls.OfType<DataGridView>(),
                item => item.Name == "WorkerDeploymentTargetGrid");
            DataGridView stageGrid = Assert.Single(
                controls.OfType<DataGridView>(),
                item => item.Name == "DeploymentStageGrid");
            Label state = Assert.Single(
                controls.OfType<Label>(),
                item => item.Name == "DeploymentOperationState");

            Assert.True(targetGrid.Width > 0 && targetGrid.Height > 0);
            Assert.True(stageGrid.Width > 0 && stageGrid.Height > 0);
            Assert.True(state.Width > 0 && state.Height > 0);
            Rectangle formBounds =
                form.RectangleToScreen(form.ClientRectangle);
            Assert.True(
                targetGrid.RectangleToScreen(targetGrid.ClientRectangle).Bottom <=
                formBounds.Bottom);
            Assert.True(
                stageGrid.RectangleToScreen(stageGrid.ClientRectangle).Bottom <=
                formBounds.Bottom);

            form.Close();
            Application.DoEvents();
            Assert.True(form.IsDisposed);
        });
    }

    [Fact]
    public void DeploymentTargets_AreImmutableAllowListWithoutSecretFields()
    {
        Assert.Equal(
            new[]
            {
                "qa-29|10.5.32.29:53229|QA|Primary|False",
                "production-30|10.5.32.30:22|Production|Primary|False",
                "standby-31|10.5.32.31:22|Production|Standby|True"
            },
            WorkerDeploymentTargets.All.Select(target =>
                $"{target.Id}|{target.Host}:{target.SshPort}|{target.ExpectedEnvironment}|{target.ExpectedRole}|{target.IsStandby}"));

        string[] propertyNames = typeof(WorkerDeploymentTarget)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase));

        WorkerDeploymentTarget standby = WorkerDeploymentTargets.Get("standby-31");
        Assert.True(new WorkerDeploymentStatus(
            "telebet-31", "Production", "Standby", "1.0", "0.9",
            false, false, true, "idle", null).Matches(standby));
        Assert.False(new WorkerDeploymentStatus(
            "telebet-31", "Production", "Primary", "1.0", "0.9",
            false, false, true, "idle", null).Matches(standby));
    }

    [Fact]
    public void DeploymentProtocol_OnlyBuildsFixedCommandsAndStagingPaths()
    {
        DeploymentId id = DeploymentId.From(
            Guid.Parse("4f97045b-ec5d-42c8-8c82-b45c97a1fa92"));

        Assert.Equal(
            "sudo -n /usr/local/sbin/angel-eye-worker-deploy status",
            WorkerDeploymentCommandBuilder.StatusCommand);
        Assert.Equal(
            "sudo -n /usr/local/sbin/angel-eye-worker-deploy preflight 4f97045b-ec5d-42c8-8c82-b45c97a1fa92",
            WorkerDeploymentCommandBuilder.Build(WorkerDeploymentAction.Preflight, id));
        Assert.Equal(
            "sudo -n /usr/local/sbin/angel-eye-worker-deploy install 4f97045b-ec5d-42c8-8c82-b45c97a1fa92",
            WorkerDeploymentCommandBuilder.Build(WorkerDeploymentAction.Install, id));
        Assert.Equal(
            "sudo -n /usr/local/sbin/angel-eye-worker-deploy rollback 4f97045b-ec5d-42c8-8c82-b45c97a1fa92",
            WorkerDeploymentCommandBuilder.Build(WorkerDeploymentAction.Rollback, id));
        Assert.Equal(
            "/var/tmp/angel-eye-deploy/4f97045b-ec5d-42c8-8c82-b45c97a1fa92/release.tar.gz",
            WorkerDeploymentCommandBuilder.RemoteArtifactPath(id));
        Assert.Throws<ArgumentException>(() => DeploymentId.From(Guid.Empty));

        string[] sessionMethods = typeof(IWorkerDeploymentSession)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();
        Assert.DoesNotContain(sessionMethods, method =>
            method.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(IWorkerDeploymentSession.InstallAsync), sessionMethods);
        Assert.Contains(nameof(IWorkerDeploymentSession.RollbackAsync), sessionMethods);
    }

    [Fact]
    public void DeploymentHostKeyVerifier_FailsClosed()
    {
        const string pinned = "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";
        Assert.True(WorkerDeploymentHostKeyVerifier.IsTrusted(pinned, pinned));
        Assert.False(WorkerDeploymentHostKeyVerifier.IsTrusted(
            pinned,
            "SHA256:attacker"));
        Assert.False(WorkerDeploymentHostKeyVerifier.IsTrusted(string.Empty, pinned));
        Assert.False(WorkerDeploymentHostKeyVerifier.IsTrusted(pinned, string.Empty));

        string[] authProperties = typeof(WorkerDeploymentAuthentication)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(authProperties, property =>
            property.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeploymentStatusParser_MapsScriptJsonAndRejectsUnknownFields()
    {
        const string json = """
            {
              "instanceName":"telebet-29",
              "environment":"QA",
              "role":"Primary",
              "currentVersion":"1.4.0",
              "previousVersion":"1.3.0",
              "serviceActive":true,
              "serviceEnabled":true,
              "healthy":true,
              "stage":"idle",
              "lastDeploymentId":"4f97045b-ec5d-42c8-8c82-b45c97a1fa92"
            }
            """;

        WorkerDeploymentStatus status = WorkerDeploymentStatusParser.Parse(json);
        Assert.True(status.Matches(WorkerDeploymentTargets.Get("qa-29")));
        Assert.Equal("1.4.0", status.CurrentVersion);
        Assert.Equal(
            Guid.Parse("4f97045b-ec5d-42c8-8c82-b45c97a1fa92"),
            status.LastDeploymentId);

        Assert.Throws<InvalidDataException>(() =>
            WorkerDeploymentStatusParser.Parse(
                json.Replace(
                    "\"stage\":\"idle\"",
                    "\"stage\":\"idle\",\"unexpected\":true",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void QueryConsole_LongOfflineMessage_StaysInsideHeaderWithoutWrappingControls()
    {
        RunSta(() =>
        {
            using QueryConsoleForm form = new(autoStartQueries: false)
            {
                WindowState = FormWindowState.Normal,
                Size = new Size(1920, 1200)
            };
            form.Show();
            Application.DoEvents();

            Label connection = GetPrivateField<Label>(form, "_connectionBanner");
            Label source = GetPrivateField<Label>(form, "_sourceBanner");
            connection.Text = "牌局查詢失敗；篩選條件與既有資料已保留（No connection could be made because the target machine actively refused it.）";
            form.PerformLayout();
            connection.Parent!.PerformLayout();
            Application.DoEvents();

            Assert.NotSame(source.Parent, connection.Parent);
            Assert.False(connection.AutoSize);
            Assert.True(connection.AutoEllipsis);
            Assert.True(connection.Width <= connection.Parent.ClientSize.Width);
            Assert.True(connection.Bottom <= connection.Parent.ClientSize.Height);
            Assert.True(connection.PointToScreen(Point.Empty).Y >= source.PointToScreen(Point.Empty).Y + source.Height);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new TargetInvocationException(failure);
        }
    }

    private static T GetPrivateField<T>(object instance, string fieldName) where T : class =>
        Assert.IsType<T>(instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance));

    private sealed class NeverConnectSessionFactory :
        IWorkerDeploymentSessionFactory
    {
        public Task<IWorkerDeploymentSession> ConnectAsync(
            WorkerDeploymentTarget target,
            WorkerDeploymentAuthentication authentication,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("UI smoke test must not connect.");
    }

    private sealed class EmptyAuditStore : IWorkerDeploymentAuditStore
    {
        public Task<bool> AppendAsync(
            WorkerDeploymentAuditEntry entry,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<WorkerDeploymentAuditEntry>> ReadAllAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerDeploymentAuditEntry>>(
                Array.Empty<WorkerDeploymentAuditEntry>());
    }
}
