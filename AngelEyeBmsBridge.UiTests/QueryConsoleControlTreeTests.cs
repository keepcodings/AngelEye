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

            string[] forbidden = ["重送", "Lock", "Unlock", "鎖定", "解鎖", "清錯", "Clear", "Transmit", "傳送開關", "儲存設定", "主備切換"];
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
}
