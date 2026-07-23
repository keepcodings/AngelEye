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
            Assert.Contains("技術資料（唯讀）", tabs);
            Assert.Contains("Events / Payload", tabs);
            Assert.Contains("Outbox", tabs);
            Assert.Contains("Recoveries", tabs);

            string[] forbidden = ["重送", "Lock", "Unlock", "鎖定", "解鎖", "清錯", "Clear", "Transmit", "傳送開關", "儲存設定", "主備切換"];
            Assert.DoesNotContain(buttons, text => forbidden.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
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
}
