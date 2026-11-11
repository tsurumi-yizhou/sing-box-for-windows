using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;

namespace SFW.Dialogs;

/// <summary>
/// A dialog with a scrolling body supplied by the caller. The chrome (title,
/// scroll area, buttons) is XAML; pages only pass the body element and keys.
/// </summary>
public sealed partial class DetailDialog : ContentDialog
{
    public DetailDialog(
        string title,
        UIElement body,
        string closeKey,
        string? primaryKey = null,
        double maxHeight = 560)
    {
        InitializeComponent();
        Loaded += (_, _) => SFW.Services.TextCopy.AttachTree(this);
        Title = title;
        Body.Content = body;
        BodyScroller.MaxHeight = maxHeight;
        CloseButtonText = Loc.Get(closeKey);
        if (primaryKey is null)
        {
            DefaultButton = ContentDialogButton.Close;
        }
        else
        {
            PrimaryButtonText = Loc.Get(primaryKey);
            DefaultButton = ContentDialogButton.Primary;
        }
    }
}
