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
    /// App language override as a BCP-47 tag; empty follows the Windows language
    /// list. Applied through ApplicationLanguages.PrimaryLanguageOverride at
    /// startup, so a change takes effect after restarting the app.
    /// </summary>
    public string LanguageTag { get; set; } = string.Empty;

    /// <summary>
    /// Dashboard cards the user hid via "Dashboard items"
    /// (mirrors SFA dashboardDisabledItems; profiles can never be hidden).
    /// </summary>
    public List<string> DisabledDashboardCards { get; set; } = [];

    // ----- Debug tools (SFA/SFM SharedPreferences parity) -----

    /// <summary>OOM killer: provide a soft memory limit for the service.</summary>
    public bool OomKillerEnabled { get; set; }

    /// <summary>
    /// Kill all connections when the memory limit is exceeded. Stored as the
    /// positive form; the daemon's start option is its inverse
    /// (<c>oom_killer_disabled</c>).
    /// </summary>
    public bool OomKillerKillConnections { get; set; }

    /// <summary>Soft memory limit in megabytes; one of CoreStartOptions.MemoryLimitMegabytes.</summary>
    public int OomMemoryLimitMegabytes { get; set; } = 50;

    /// <summary>Save a power report for every service run (SFA/SFM power reports).</summary>
    public bool PowerReportEnabled { get; set; }
}

public sealed class Profile
{
    public long Id { get; set; }
    public long Order { get; set; }
    public string Name { get; set; } = "New profile";
    public ProfileType Type { get; set; } = ProfileType.Local;

    /// <summary>
    /// App-managed configuration file. Local imports are copied under the data
    /// directory, so this never points at a file the user picked in place.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Where a local configuration was imported from. Kept for display only; the
    /// profile is served from <see cref="Path"/>, so moving or deleting the
    /// original file no longer breaks it.
    /// </summary>
    public string? SourcePath { get; set; }

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


