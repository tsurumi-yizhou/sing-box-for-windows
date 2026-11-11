using Microsoft.UI.Xaml.Controls;
using SFW.Services;

namespace SFW.Dialogs;

/// <summary>
/// A single-message prompt: the layout is XAML, the wording comes from
/// resources (title key plus the caller's already-formatted message).
/// </summary>
public sealed partial class MessageDialog : ContentDialog
{
    public MessageDialog(string titleKey, string message, string closeKey = "Common.OK")
    {
        InitializeComponent();
        Title = Loc.Get(titleKey);
        MessageText.Text = message;
        CloseButtonText = Loc.Get(closeKey);
    }
}
