using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Models;

namespace SFW.Services;

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
        var dialog = new Dialogs.EditProfileDialog(profile) { XamlRoot = xamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        await App.State.UpdateProfileAsync(
            profile.Id, dialog.ProfileName, dialog.Source, dialog.IntervalMinutes);
        return true;
    }

    /// <summary>Shows the remove-profile confirmation. Returns true when confirmed.</summary>
    public static async Task<bool> ConfirmRemoveAsync(XamlRoot xamlRoot, Profile profile)
    {
        var isActive = App.State.Settings.SelectedProfileId == profile.Id && App.State.Core.State.IsRunning;
        var dialog = new Dialogs.ConfirmDialog(
            "ProfileDialogs.RemoveProfile",
            isActive
                ? "ProfileDialogs.RemoveItIsCurrentlyInUseTheRunningServiceWillBeStopp"
                : "ProfileDialogs.Remove",
            "ProfileDialogs.RemoveAlt2",
            profile.Name)
        { XamlRoot = xamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
