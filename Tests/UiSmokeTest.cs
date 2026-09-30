using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Models;
using SFW.Pages;
using SFW.Services.Core;

namespace SFW.Tests;

// Opt-in regression check in the real packaged executable, including Release.
// Run sing-box.exe --ui-smoke-test with no existing instance; no core is started.
internal static class UiSmokeTest
{
    internal static async Task RunAsync(MainWindow window)
    {
        var resultPath = Path.Combine(App.State.DataDirectory, "ui-smoke-test.txt");
        try
        {
            File.WriteAllText(resultPath, "RUNNING");
            await Task.Delay(300);
            var frame = (Frame)((FrameworkElement)window.Content).FindName("NavFrame");
            for (var pass = 0; pass < 3; pass++)
            {
                window.NavigateTo("home");
                await Task.Delay(100);
                var button = (HyperlinkButton)((HomePage)frame.Content).FindName("ViewConnectionsButton");
                button.IsEnabled = true;
                ((IInvokeProvider)new HyperlinkButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(100);
                if (frame.Content is not ConnectionsPage connections) throw new Exception("Dashboard navigation failed.");
                var list = (ListView)connections.FindName("ConnectionsList");
                var sample = new CoreConnection("smoke", "test", "mixed", 4, "tcp", "127.0.0.1:1",
                    "example.test:443", "example.test", "example.test:443", "tls", "", "",
                    1, 0, 1024, 2048, 4096, 8192, "test", "direct", "direct", new[] { "direct" }, null);
                var row = new ConnectionRow(sample);
                ((IList)list.ItemsSource).Add(row);
                list.UpdateLayout();
                await Task.Delay(100);
                RequireText(list, row.Title);
                row.Update(sample with { Uplink = 2048 });
                await Task.Delay(100);
                RequireText(list, row.UpText);
                ((IList)list.ItemsSource).Clear();

                window.NavigateTo("logs");
                await Task.Delay(100);
                var logs = (LogsPage)frame.Content;
                var logList = (ListView)logs.FindName("LogList");
                ((IList)logList.ItemsSource).Add(new LogRow("SFW UI smoke test", new SolidColorBrush(Microsoft.UI.Colors.White)));
                logList.UpdateLayout();
                await Task.Delay(100);
                RequireText(logList, "SFW UI smoke test");
                window.NavigateTo("connections");
                await Task.Delay(100);
                if (frame.Content is not ConnectionsPage) throw new Exception("Sidebar navigation failed.");
            }
            window.NavigateTo("settings");
            await Task.Delay(100);
            if (((FrameworkElement)frame.Content).FindName("ServiceRepairCard") is not null)
                throw new Exception("Unexpected permanent service repair control.");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")))
                throw new Exception("Published application icon is missing.");
            window.NavigateTo("home");
            File.WriteAllText(resultPath, "PASS: dashboard and sidebar navigation, populated lists, compiled bindings, live row updates; 3 passes.");
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, "FAIL: " + error);
        }
    }

    private static void RequireText(DependencyObject root, string expected)
    {
        if (!ContainsText(root, expected)) throw new Exception("Rendered text missing: " + expected);
    }

    private static bool ContainsText(DependencyObject root, string expected)
    {
        if (root is TextBlock text && text.Text == expected) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsText(VisualTreeHelper.GetChild(root, i), expected)) return true;
        return false;
    }
}
