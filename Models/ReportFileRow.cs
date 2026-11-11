using Microsoft.UI.Xaml;
using SFW.Services.Core;

namespace SFW.Models;

/// <summary>
/// One row of the report file list. The row markup lives in
/// ReportDetailPage.xaml; this type only holds what the template binds to.
/// </summary>
public sealed class ReportFileRow(CoreReportFile file, string profileHint)
{
    public CoreReportFile File { get; } = file;

    public string Name => File.Name;

    public bool IsText => File.IsText;

    public string ProfileHint { get; } = profileHint;

    /// <summary>Only profile files carry the "export to view" caption.</summary>
    public Visibility ProfileHintVisibility =>
        string.IsNullOrEmpty(ProfileHint) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Binary/profile files are not navigable, so they show no chevron.</summary>
    public Visibility ChevronVisibility =>
        IsText ? Visibility.Visible : Visibility.Collapsed;
}
