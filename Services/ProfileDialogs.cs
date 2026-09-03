using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sing_box_for_windows.Models;

namespace sing_box_for_windows.Services;

/// <summary>
/// Shared profile dialogs used by both HomePage and ProfilesPage, so the two
/// pages cannot drift apart (SFM EditProfileView parity: name, source, and for
/// remote profiles the auto-update interval in minutes).
/// </summary>
public static class ProfileDialogs
{
    /// <summary>SFM clamping: values below 15 minutes are lifted; 0 falls back to the 60-minute default.</summary>
    public const int MinAutoUpdateIntervalMinutes = 15;
    public const int DefaultAutoUpdateIntervalMinutes = 60;

    public static int ClampAutoUpdateInterval(int minutes) =>
        minutes <= 0 ? DefaultAutoUpdateIntervalMinutes : Math.Max(MinAutoUpdateIntervalMinutes, minutes);

    /// <summary>Shows the edit-profile dialog. Returns true when saved.</summary>
    public static async Task<bool> ShowEditAsync(XamlRoot xamlRoot, Profile profile)
    {
        var nameBox = new TextBox
        {
            Header = Loc.Get("Profile name", "配置名称"),
            Text = profile.Name,
        };
        var sourceBox = new TextBox
        {
            Header = profile.IsRemote ? Loc.Get("Subscription URL", "订阅 URL") : Loc.Get("Configuration file", "配置文件"),
            Text = profile.IsRemote ? profile.RemoteUrl : profile.Path,
            MinWidth = 420,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(sourceBox);

        NumberBox? intervalBox = null;
        if (profile.IsRemote)
        {
            intervalBox = new NumberBox
            {
                Header = Loc.Get("Auto update interval (minutes)", "自动更新间隔（分钟）"),
                Minimum = MinAutoUpdateIntervalMinutes,
                Maximum = 7 * 24 * 60,
                Value = ClampAutoUpdateInterval(profile.AutoUpdateInterval),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 15,
                LargeChange = 60,
            };
            panel.Children.Add(intervalBox);
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Edit profile", "编辑配置"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Save", "保存"),
            CloseButtonText = Loc.Get("Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        await App.State.UpdateProfileAsync(
            profile.Id,
            nameBox.Text ?? string.Empty,
            sourceBox.Text ?? string.Empty,
            intervalBox is null || double.IsNaN(intervalBox.Value) ? null : (int)intervalBox.Value);
        return true;
    }

    /// <summary>Shows the remove-profile confirmation. Returns true when confirmed.</summary>
    public static async Task<bool> ConfirmRemoveAsync(XamlRoot xamlRoot, Profile profile)
    {
        var isActive = App.State.Settings.SelectedProfileId == profile.Id && App.State.Core.State.IsRunning;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Remove profile", "删除配置"),
            Content = isActive
                ? string.Format(Loc.Get(
                    "Remove \"{0}\"? It is currently in use; the running service will be stopped.",
                    "确定删除“{0}”吗？该配置正在使用中，运行中的服务将被停止。"), profile.Name)
                : string.Format(Loc.Get("Remove \"{0}\"?", "确定删除“{0}”吗？"), profile.Name),
            PrimaryButtonText = Loc.Get("Remove", "删除"),
            CloseButtonText = Loc.Get("Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
