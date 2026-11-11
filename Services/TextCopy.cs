using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace SFW.Services;

/// <summary>
/// Makes every informational TextBlock copyable without changing layout:
/// a right-click "Copy" menu is attached to each one (including text created
/// later by lists and cards), and plain text outside interactive containers also
/// gets native drag selection / Ctrl+C. Text inside buttons, list items and
/// clickable settings rows only gets the menu so copying never steals a click.
/// </summary>
public static class TextCopy
{
    public static void Attach(TextBlock text)
    {
        if (text.IsTextSelectionEnabled)
        {
            // Already natively selectable (dialogs, paths): its built-in menu is fine.
            return;
        }

        if (text.ContextFlyout is not MenuFlyout)
        {
            var copy = new MenuFlyoutItem
            {
                Text = Loc.Get("Common.Copy"),
                Icon = new FontIcon { Glyph = "\uE8C8" },
            };
            copy.Click += (_, _) => Copy(text.Text);

            var flyout = new MenuFlyout();
            flyout.Items.Add(copy);
            text.ContextFlyout = flyout;
        }

        if (!IsInsideInteractiveContainer(text))
        {
            text.IsTextSelectionEnabled = true;
        }
    }

    /// <summary>
    /// Finds the TextBlock under a right-click and, when it is not inside a text
    /// editor (which has its own menu), reports it for our copy menu.
    /// </summary>
    public static bool TryShowCopyMenu(DependencyObject? source, XamlRoot? xamlRoot, out TextBlock? target)
    {
        target = null;
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or RichEditBox) return false;
            if (current is TextBlock text)
            {
                if (text.IsTextSelectionEnabled) return false; // native menu already copies
                target = text;
                return true;
            }
        }
        return false;
    }

    /// <summary>Shows the copy menu for a TextBlock found by a right-click.</summary>
    public static void ShowCopyMenu(TextBlock text, Windows.Foundation.Point position)
    {
        var copy = new MenuFlyoutItem
        {
            Text = Loc.Get("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" },
        };
        copy.Click += (_, _) => Copy(text.Text);

        var flyout = new MenuFlyout();
        flyout.Items.Add(copy);
        flyout.Closed += (_, _) =>
        {
            // From now on the text behaves like normal selectable text (drag
            // selection and Ctrl+C); this first menu just makes the affordance
            // discoverable even inside dense rows.
            if (!IsInsideInteractiveContainer(text)) text.IsTextSelectionEnabled = true;
        };
        flyout.ShowAt(text, position);
    }

    /// <summary>Attaches to every TextBlock currently under the given root.</summary>
    public static void AttachTree(DependencyObject root)
    {
        if (root is TextBlock text) Attach(text);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            AttachTree(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void Copy(string? value)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(value ?? string.Empty);
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // Clipboard can be held by another process; copying is best effort.
        }
    }

    private static bool IsInsideInteractiveContainer(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or ListViewItem or GridViewItem or NavigationViewItem
                or ComboBoxItem or ToggleSwitch)
            {
                return true;
            }

            // A clickable SettingsCard owns the whole row; a plain one is just a
            // value surface, so its text stays selectable.
            if (current is CommunityToolkit.WinUI.Controls.SettingsCard card && card.IsClickEnabled)
            {
                return true;
            }
        }

        return false;
    }
}
