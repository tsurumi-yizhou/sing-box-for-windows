using System.Collections;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Dialogs;
using SFW.Controls;
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
            // Reproduce the dashboard refresh after adding a subscription to a
            // previously empty list, without downloading private subscriptions.
            // Tailscale: the page and its endpoint card are native controls now.
            frame.Navigate(typeof(TailscalePage));
            await Task.Delay(200);
            if (frame.Content is not TailscalePage tailscalePage ||
                tailscalePage.FindName("StatusBar") is not InfoBar tailscaleBar || !tailscaleBar.IsOpen ||
                tailscalePage.FindName("TailscaleCard") is not StackPanel tailscaleCard ||
                tailscaleCard.Visibility != Visibility.Collapsed)
                throw new Exception("The stopped Tailscale page must show its status bar only.");

            var tailscaleSample = new CoreTailscaleEndpoint(
                string.Empty, "Running", "Running", string.Empty, "network", string.Empty,
                null, Array.Empty<CoreTailscaleUserGroup>(), null, false, false, 0, 0, 0);
            var endpointCard = new TailscaleEndpointCard();
            endpointCard.ApplyHeader(tailscaleSample, false, new SolidColorBrush(Microsoft.UI.Colors.Green));
            endpointCard.ShowNetwork("network");
            endpointCard.ShowThisDevice("device", "100.64.0.1", true);
            endpointCard.ShowExitNode("none");
            endpointCard.ShowAuthUrl("https://example.com");
            if (endpointCard.FindName("NetworkCard") is not SettingsCard networkCard || networkCard.Visibility != Visibility.Visible ||
                endpointCard.FindName("DeviceCard") is not SettingsCard deviceCard || deviceCard.Visibility != Visibility.Visible ||
                endpointCard.FindName("ExitNodeCard") is not SettingsCard exitCard || exitCard.Visibility != Visibility.Visible ||
                endpointCard.FindName("AuthUrlCard") is not SettingsCard authCard || authCard.Visibility != Visibility.Visible)
                throw new Exception("Tailscale endpoint rows must be native SettingsCards.");

            frame.Navigate(typeof(HomePage));
            await Task.Delay(200);
            var profileBox = (ComboBox)((HomePage)frame.Content).FindName("ProfileBox");
            var sampleProfile = new Profile { Id = 1, Name = "Subscription smoke test", Type = ProfileType.Remote };
            App.State.Settings.Profiles.Add(sampleProfile);
            App.State.Settings.SelectedProfileId = sampleProfile.Id;
            var refresh = typeof(HomePage).GetMethod("RefreshProfiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            refresh.Invoke(frame.Content, null);
            profileBox.UpdateLayout();
            await Task.Delay(100);
            if (profileBox.Items.Count != 1 || profileBox.SelectedItem != sampleProfile)
                throw new Exception("New subscription was not selected after refresh.");
            RequireText(profileBox, sampleProfile.Name);
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
            var settingsPage = (SettingsPage)frame.Content;
            // Settings and tools now use the native SettingsExpander groups;
            // keep a regression guard so the hand-drawn section lists cannot
            // come back unnoticed.
            if (settingsPage.FindName("GeneralExpander") is not SettingsExpander generalExpander ||
                settingsPage.FindName("AboutExpander") is not SettingsExpander aboutExpander)
                throw new Exception("Settings sections must be native SettingsExpanders.");
            if (!generalExpander.IsExpanded || generalExpander.Items.Count != 3 ||
                !aboutExpander.IsExpanded || aboutExpander.Items.Count != 3)
                throw new Exception("Settings sections must be expanded SettingsExpanders with their cards.");
            if (generalExpander.Header is not string generalHeader || generalHeader.Length == 0)
                throw new Exception("Settings expander headers must come from x:Uid resources.");
            if (settingsPage.FindName("ServiceRepairCard") is not null)
                throw new Exception("Unexpected permanent service repair control.");
            // The cards live in the expander's ItemsRepeater, so a parent in the
            // visual tree proves the native expander rendered them.
            settingsPage.UpdateLayout();
            await Task.Delay(100);
            if (settingsPage.FindName("AppCard") is not SettingsCard appCard ||
                VisualTreeHelper.GetParent(appCard) is null)
                throw new Exception("Settings expander cards were not rendered.");
            if (appCard.Header is not string appHeader || appHeader.Length == 0)
                throw new Exception("Settings card headers must come from x:Uid resources.");

            // App settings: the language selector must exist and explain itself.
            frame.Navigate(typeof(AppSettingsPage));
            await Task.Delay(150);
            if (frame.Content is not AppSettingsPage appSettingsPage ||
                appSettingsPage.FindName("LanguageCard") is not SettingsCard languageCard ||
                languageCard.Description is not string languageDescription || languageDescription.Length == 0 ||
                languageCard.Content is not ComboBox languageBox ||
                languageBox.Items.Count != 4)
                throw new Exception("App settings must offer the language selector with its explanation.");
            var languageTags = languageBox.Items.OfType<ComboBoxItem>()
                .Select(item => item.Tag?.ToString() ?? string.Empty)
                .ToArray();
            if (!languageTags.SequenceEqual(new[] { string.Empty, "en-US", "zh-CN", "zh-HK" }))
                throw new Exception($"Unexpected language choices: {string.Join(", ", languageTags)}.");
            languageCard.UpdateLayout();
            if (languageBox.ActualWidth < 80)
                throw new Exception($"The language selector has no usable size ({languageBox.ActualWidth}).");
            var languageOffset = languageBox.TransformToVisual(languageCard)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            if (languageOffset.X < 0 || languageOffset.X + languageBox.ActualWidth > languageCard.ActualWidth + 1)
                throw new Exception($"The language selector is laid out outside its card (x={languageOffset.X}).");
            // SFA parity: the tools entry is always available; only the endpoint
            // section inside the page depends on the running core.
            var navView = (NavigationView)((FrameworkElement)window.Content).FindName("NavView");
            var toolsItem = navView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => Equals(item.Tag?.ToString(), "tools"));
            if (toolsItem is null) throw new Exception("Tools navigation item is missing.");
            if (toolsItem.Visibility == Visibility.Collapsed)
                throw new Exception("Tools navigation item must stay visible while the core is stopped.");
            window.NavigateTo("tools");
            await Task.Delay(100);
            if (frame.Content is not ToolsPage toolsPage) throw new Exception("Tools page did not open while the core is stopped.");
            if (toolsPage.FindName("EndpointsExpander") is not SettingsExpander endpointsExpander)
                throw new Exception("The endpoint section must be a SettingsExpander.");
            if (endpointsExpander.Visibility != Visibility.Collapsed)
                throw new Exception("The endpoint section must stay hidden while the core is stopped.");
            if (toolsPage.FindName("NetworkExpander") is not SettingsExpander networkExpander ||
                !networkExpander.IsExpanded || networkExpander.Items.Count != 2)
                throw new Exception("The tools network section must be an expanded SettingsExpander with two cards.");
            if (networkExpander.Header is not string networkHeader || networkHeader.Length == 0)
                throw new Exception("Tools expander headers must come from x:Uid resources.");
            if (toolsPage.FindName("DebugExpander") is not SettingsExpander debugExpander ||
                !debugExpander.IsExpanded || debugExpander.Items.Count != 3)
                throw new Exception("The tools debug section must be an expanded SettingsExpander with three cards.");
            toolsPage.UpdateLayout();
            await Task.Delay(100);
            if (toolsPage.FindName("NetworkQualityCard") is not SettingsCard networkQualityCard ||
                VisualTreeHelper.GetParent(networkQualityCard) is null)
                throw new Exception("Tools expander cards were not rendered.");
            if (!Application.Current.Resources.TryGetValue("IconActionButtonStyle", out _))
                throw new Exception("The icon action button style is missing.");

            // Profiles: the add form is a native Expander card with the picker
            // attached inside the path field.
            frame.Navigate(typeof(ProfilesPage));
            await Task.Delay(150);
            if (frame.Content is not ProfilesPage profilesPage ||
                profilesPage.FindName("AddProfileExpander") is not Expander addProfileExpander ||
                !addProfileExpander.IsExpanded)
                throw new Exception("The add-profile form must be an expanded Expander.");
            if (profilesPage.FindName("ConfigBox") is not TextBox configBox ||
                profilesPage.FindName("BrowseButton") is not Button browseButton)
                throw new Exception("The add-profile form fields are missing.");
            if (configBox.Padding.Right < 40 || browseButton.Visibility != Visibility.Visible)
                throw new Exception("The browse action must sit inside the path field.");

            // Edit dialog: every EditProfileDialog.* resource must map to a real
            // ContentDialog property, and the dynamic source header must resolve.
            var editSample = new Profile
            {
                Id = 99, Name = "Edit smoke", Type = ProfileType.Local,
                Path = "C:\\config.json", SourcePath = "C:\\config.json",
            };
            var editDialog = new EditProfileDialog(editSample) { XamlRoot = window.Content.XamlRoot };
            if (editDialog.FindName("LocalConfigPanel") is not StackPanel localConfigPanel ||
                localConfigPanel.Visibility != Visibility.Visible ||
                editDialog.FindName("SourceBox") is not TextBox localPathBox ||
                localPathBox.Visibility != Visibility.Collapsed)
                throw new Exception("A local profile must not expose its configuration path.");
            if (!string.IsNullOrEmpty(editDialog.Source))
                throw new Exception("An unchanged local profile must keep its app-owned copy.");
            // Show it once: the original failure appeared when the dialog opened.
            var editShow = editDialog.ShowAsync();
            await Task.Delay(300);
            editDialog.Hide();
            await editShow;

            window.NavigateTo("home");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")))
                throw new Exception("Published application icon is missing.");
            File.WriteAllText(resultPath, "PASS: dashboard and sidebar navigation, populated lists, compiled bindings, live row updates, settings expanders, language selector; 3 passes.");
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
