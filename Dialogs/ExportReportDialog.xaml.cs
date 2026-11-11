using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SFW.Dialogs;

/// <summary>
/// The report export sheet: which parts of the report go into the archive and
/// whether it is age-encrypted. The layout and wording live in the XAML; the page
/// only asks for the three choices after the dialog is confirmed.
/// </summary>
public sealed partial class ExportReportDialog : ContentDialog
{
    public ExportReportDialog(bool hasLog, bool showPrivacyNote)
    {
        InitializeComponent();
        WithLogBox.IsChecked = hasLog;
        PrivacyNote.Visibility = showPrivacyNote ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>SFM preselects "With Log" only when the report has a log.</summary>
    public bool WithLog => WithLogBox.IsChecked == true;

    public bool WithConfiguration => WithConfigurationBox.IsChecked == true;

    public bool Encrypt => EncryptBox.IsChecked == true;
}
