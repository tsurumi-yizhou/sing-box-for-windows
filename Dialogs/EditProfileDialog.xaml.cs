using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Models;
using SFW.Services;

namespace SFW.Dialogs;

/// <summary>
/// The shared edit-profile dialog. A local profile is served from an app-owned
/// copy, so only its name is edited and replacing the configuration is an
/// explicit action; a remote profile edits its URL and auto-update interval.
/// </summary>
public sealed partial class EditProfileDialog : ContentDialog
{
    private string? _replacementSource;

    public EditProfileDialog(Profile profile)
    {
        InitializeComponent();
        Loaded += (_, _) => SFW.Services.TextCopy.AttachTree(this);
        NameBox.Text = profile.Name;

        var remote = profile.IsRemote;
        // This header is only meaningful for the URL field; the local path is an
        // implementation detail the user never needs to see.
        SourceBox.Header = Loc.Get("ProfileDialogs.SourceRemoteHeader");
        SourceBox.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        LocalConfigPanel.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        if (remote) SourceBox.Text = profile.RemoteUrl;

        IntervalBox.Value = ProfileDialogs.ClampAutoUpdateInterval(profile.AutoUpdateInterval);
        IntervalBox.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
    }

    public string ProfileName => NameBox.Text ?? string.Empty;

    /// <summary>
    /// Remote: the URL shown in the field. Local: only a path the user
    /// picked to replace the configuration; empty keeps the app-owned copy.
    /// </summary>
    public string Source => _replacementSource ?? (SourceBox.Text ?? string.Empty);

    public int? IntervalMinutes => IntervalBox.Visibility == Visibility.Visible
        && !double.IsNaN(IntervalBox.Value)
        ? (int)IntervalBox.Value
        : null;

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfigFilePicker.PickAsync() is { } picked)
            {
                _replacementSource = picked;
                ReplacementText.Text = Path.GetFileName(picked);
                ReplacementText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // The picker is best-effort; a failed pick simply keeps the copy.
        }
    }
}
