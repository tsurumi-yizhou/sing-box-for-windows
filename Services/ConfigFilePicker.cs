using Microsoft.Windows.Storage.Pickers;

namespace SFW.Services;

/// <summary>
/// Shared .json source picker for the profile forms (SFA "Import File" parity).
/// Uses the Windows App SDK picker: it takes the host window's WindowId and
/// returns a plain path, so the WinRT HWND interop is no longer needed.
/// </summary>
public static class ConfigFilePicker
{
    /// <summary>Shows the picker. Returns the picked path, or null when cancelled.</summary>
    public static async Task<string?> PickAsync()
    {
        if (MainWindow.Instance is not { } window) return null;

        var picker = new FileOpenPicker(window.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
