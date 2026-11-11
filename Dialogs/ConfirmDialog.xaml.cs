using Microsoft.UI.Xaml.Controls;
using SFW.Services;

namespace SFW.Dialogs;

/// <summary>
/// A confirmation prompt: the layout is XAML, the title, message and button
/// labels are resource keys so each caller only supplies its own wording.
/// </summary>
public sealed partial class ConfirmDialog : ContentDialog
{
    public ConfirmDialog(string titleKey, string messageKey, string primaryKey, params object[] messageArgs)
    {
        InitializeComponent();
        Loaded += (_, _) => SFW.Services.TextCopy.AttachTree(this);
        Title = Loc.Get(titleKey);
        MessageText.Text = messageArgs.Length == 0
            ? Loc.Get(messageKey)
            : string.Format(Loc.Get(messageKey), messageArgs);
        PrimaryButtonText = Loc.Get(primaryKey);
        CloseButtonText = Loc.Get("Common.Cancel");
    }
}
