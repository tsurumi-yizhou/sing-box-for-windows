using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;

namespace SFW.Dialogs;

/// <summary>The values the add-profile form collected when the user confirmed.</summary>
public sealed record AddProfileForm(
    string Name, bool IsRemote, string Source, bool AutoUpdate, int IntervalMinutes);

/// <summary>
/// The add-profile dialog. The form markup lives in XAML; this class only handles
/// the type switch, the file picker and the confirm flow, and hands the collected
/// values to <see cref="SubmitAsync"/> so page logic stays in the page.
/// </summary>
public sealed partial class AddProfileDialog : ContentDialog
{
    public AddProfileDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => SFW.Services.TextCopy.AttachTree(this);
        UpdateTypeVisibility();
    }

    /// <summary>
    /// Registers the profile. Returns null on success (the dialog closes itself),
    /// or an error message the dialog shows in place of the form.
    /// </summary>
    public Func<AddProfileForm, Task<string?>>? SubmitAsync { get; set; }

    private void TypeRadio_Checked(object sender, RoutedEventArgs e) => UpdateTypeVisibility();

    private void UpdateTypeVisibility()
    {
        var remote = RemoteRadio.IsChecked == true;
        PathGrid.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        RemotePanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfigFilePicker.PickAsync() is { } picked) PathBox.Text = picked;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the form open: it closes itself only once the profile exists.
        args.Cancel = true;

        var remote = RemoteRadio.IsChecked == true;
        var source = (remote ? UrlBox.Text : PathBox.Text).Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            ShowError(remote
                ? Loc.Get("Home.EnterAValidHTTPOrHTTPSSubscriptionURL")
                : Loc.Get("Home.ChooseASingBoxJSONConfigurationFile"));
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        ProgressRow.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        ProgressRing.IsActive = remote;
        IsPrimaryButtonEnabled = false;
        try
        {
            var interval = double.IsNaN(IntervalBox.Value)
                ? ProfileDialogs.DefaultAutoUpdateIntervalMinutes
                : ProfileDialogs.ClampAutoUpdateInterval((int)IntervalBox.Value);
            var form = new AddProfileForm(
                NameBox.Text.Trim(), remote, source, AutoUpdateToggle.IsOn, interval);
            var error = SubmitAsync is null ? null : await SubmitAsync(form);
            if (error is null)
            {
                Hide();
            }
            else
            {
                ShowError(error);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            ProgressRing.IsActive = false;
            ProgressRow.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
