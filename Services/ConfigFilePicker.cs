using Windows.Storage.Pickers;

namespace SFW.Services;

/// <summary>
/// Shared .json source picker for the profile forms (SFA "Import File" parity).
/// </summary>
public static class ConfigFilePicker
{
    /// <summary>Shows the picker. Returns the picked path, or null when cancelled.</summary>
    public static async Task<string?> PickAsync()
    {
        var picker = new FileOpenPicker();
        if (MainWindow.Instance is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
