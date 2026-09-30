using System.Text.Json.Serialization;

namespace SFW.Models;

public enum ProfileType
{
    Local = 0,
    ICloud = 1,
    Remote = 2,
}

public sealed class AppSettings
{
    public long NextProfileId { get; set; } = 1;
    public List<Profile> Profiles { get; set; } = [];
    public long? SelectedProfileId { get; set; }

    /// <summary>
    /// Development-only fallback. Not part of the native core control plane.
    /// </summary>
    public string DevelopmentSingBoxPath { get; set; } = string.Empty;

    public string DevelopmentWorkingDirectory { get; set; } = string.Empty;

    /// <summary>App theme override: "default" | "light" | "dark".</summary>
    public string Theme { get; set; } = "default";

    /// <summary>Window backdrop: "mica" | "acrylic".</summary>
    public string Backdrop { get; set; } = "mica";

    /// <summary>
    /// Dashboard cards the user hid via "Dashboard items"
    /// (mirrors SFA dashboardDisabledItems; profiles can never be hidden).
    /// </summary>
    public List<string> DisabledDashboardCards { get; set; } = [];
}

public sealed class Profile
{
    public long Id { get; set; }
    public long Order { get; set; }
    public string Name { get; set; } = "New profile";
    public ProfileType Type { get; set; } = ProfileType.Local;
    public string Path { get; set; } = string.Empty;
    public string? RemoteUrl { get; set; }
    public bool AutoUpdate { get; set; }
    public int AutoUpdateInterval { get; set; } = 60;
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>Error message from the last failed subscription update; null after a success.</summary>
    public string? LastUpdateError { get; set; }

    [JsonIgnore]
    public bool IsRemote => Type == ProfileType.Remote;

    [JsonIgnore]
    public string DisplaySource => IsRemote ? (RemoteUrl ?? Path) : Path;
}


