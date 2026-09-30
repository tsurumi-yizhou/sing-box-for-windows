using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        button.Content = busyText is null
            ? new ProgressRing { IsActive = true, Width = 16, Height = 16 }
            : (object)new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                    new TextBlock { Text = busyText, VerticalAlignment = VerticalAlignment.Center },
                },
            };
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
