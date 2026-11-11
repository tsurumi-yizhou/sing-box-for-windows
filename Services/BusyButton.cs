using Microsoft.UI.Xaml.Controls;
using SFW.Controls;

namespace SFW.Services;

/// <summary>
/// Gives immediate visual feedback for long-running button actions: the button
/// is disabled and its content is replaced by a spinner until the action ends.
/// </summary>
public static class BusyButton
{
    public static async Task RunAsync(Button button, Func<Task> action, string? busyText = null)
    {
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = new BusyIndicator(busyText);
        try
        {
            await action();
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }
}
