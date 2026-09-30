using System.Text.Json;
using System.Text.Json.Serialization;
using SFW.Models;
using SFW.Services.Core;

namespace SFW.Services;

public sealed class AppState : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _autoUpdateCts = new();
    private readonly object _autoUpdateLock = new();
    private readonly Task _autoUpdateLoopTask;

    /// <summary>Serializes subscription downloads/writes so manual and automatic
    /// updates can never write the same profile file concurrently.</summary>
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);

    /// <summary>Shared client for subscription downloads (SFA HTTPClient parity:
    /// explicit User-Agent, no per-request allocation).</summary>
    private static readonly HttpClient SubscriptionHttpClient = CreateSubscriptionHttpClient();

    private static HttpClient CreateSubscriptionHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sing-box-for-windows/1.0");
        return client;
    }

    public AppSettings Settings { get; private set; } = new();
    public ICoreController Core { get; }
    public List<string> Logs { get; } = [];
    public event EventHandler<string>? LogAdded;
    public event EventHandler? LogsCleared;
    private readonly object _logLock = new();
    private readonly object _settingsWriteLock = new();

    /// <summary>Directory holding settings.json and downloaded subscriptions.</summary>
    public string DataDirectory => _dataDirectory;

    public AppState()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataDirectory = Path.Combine(localAppData, "Sing-Box");

        // One-time migration from the pre-rename directory.
        var legacyDirectory = Path.Combine(localAppData, "sing-box-for-windows");
        var migrated = false;
        if (!Directory.Exists(_dataDirectory) && Directory.Exists(legacyDirectory))
        {
            try
            {
                Directory.Move(legacyDirectory, _dataDirectory);
                migrated = true;
            }
            catch
            {
                // Fall through: a fresh directory is created below.
            }
        }
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");

        Settings = Load();
        if (migrated)
        {
            // Profile paths store absolute locations of downloaded subscriptions;
            // point them at the renamed directory.
            foreach (var profile in Settings.Profiles)
            {
                if (profile.Path.StartsWith(legacyDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    profile.Path = _dataDirectory + profile.Path[legacyDirectory.Length..];
                }
            }
            Save();
        }

        Core = new BoxddCoreController();
        Core.LogReceived += (_, line) => AddLog(line);
        Core.LogsReset += (_, _) => ClearLogs();

        try
        {
            var diag = Path.Combine(_dataDirectory, "startup-diag.log");
            File.AppendAllText(diag,
                $"[{DateTimeOffset.Now:O}] BaseDirectory={AppContext.BaseDirectory}{Environment.NewLine}" +
                $"ProcessPath={Environment.ProcessPath}{Environment.NewLine}" +
                $"CommandLine={Environment.CommandLine}{Environment.NewLine}" +
                $"Daemon={DaemonServiceManager.FindDaemonExecutable()}{Environment.NewLine}" +
                $"ServiceImagePath={DaemonServiceManager.QueryServiceImagePath()}{Environment.NewLine}" +
                $"Worker={DaemonServiceManager.FindWorkerExecutable()}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic only.
        }

        _autoUpdateLoopTask = AutoUpdateLoopAsync(_autoUpdateCts.Token);
    }

    public void ClearLogs()
    {
        lock (_logLock)
        {
            Logs.Clear();
        }
        LogsCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Thread-safe copy of the log buffer for UI rendering.</summary>
    public string[] SnapshotLogs()
    {
        lock (_logLock)
        {
            return Logs.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _autoUpdateCts.Cancel();
        try
        {
            await Core.DisposeAsync();
        }
        finally
        {
            try { await _autoUpdateLoopTask; } catch (OperationCanceledException) { }
            _autoUpdateCts.Dispose();
        }
    }

    private void AddLog(string line)
    {
        // Called from gRPC stream threads and process event handlers.
        lock (_logLock)
        {
            Logs.Add(line);
            if (Logs.Count > 3000) Logs.RemoveAt(0); // SFA/SFM buffer cap
        }
        LogAdded?.Invoke(this, line);
    }

    /// <summary>Thread-safe log append for UI-originated entries.</summary>
    public void AppendLog(string line) => AddLog(line);

    public void Save()
    {
        lock (_settingsWriteLock)
            AtomicFile.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, AppJsonContext.Default.AppSettings));
    }

    public async Task StartAsync()
    {
        StartupDiag.Log("AppState.StartAsync: enter");
        var profile = ActiveProfile()
            ?? throw new InvalidOperationException(Loc.Get("Select or import a profile before connecting.", "连接前请选择或导入配置。"));
        StartupDiag.Log($"AppState.StartAsync: profile={profile.Path}");
        var config = await File.ReadAllTextAsync(profile.Path);
        StartupDiag.Log($"AppState.StartAsync: config read, {config.Length} chars");
        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidOperationException(Loc.Get("The selected profile is empty.", "所选配置为空。"));
        config = RuleSetMirror.RewriteGithubRuleSetUrls(config, AddLog);
        StartupDiag.Log("AppState.StartAsync: rule-set rewrite done, calling Core.StartAsync");
        await Core.StartAsync(config);
        StartupDiag.Log("AppState.StartAsync: Core.StartAsync returned");
    }

    public Task StopAsync() => Core.StopAsync();

    /// <summary>
    /// Login launch: bring the core up without any UI. The daemon service already
    /// starts itself at boot, so this only has to start the core with the selected
    /// profile; when the daemon is not reachable we stay silent instead of asking
    /// for elevation at sign-in, and the tray keeps the manual start available.
    /// </summary>
    public async Task<bool> TryStartAtLoginAsync()
    {
        var profile = ActiveProfile();
        if (profile is null)
        {
            AddLog("Login start skipped: no profile is selected.");
            return false;
        }

        if (!await DaemonServiceManager.IsDaemonReachableAsync())
        {
            AddLog("Login start skipped: the SingBox daemon service is not running.");
            return false;
        }

        try
        {
            await StartAsync();
            AddLog($"Started the SingBox service for '{profile.Name}' at login.");
            return true;
        }
        catch (Exception exception)
        {
            AddLog($"Login start failed: {exception.Message}");
            return false;
        }
    }

    public async Task UpdateAutoUpdateProfilesAsync()
    {
        if (!Monitor.TryEnter(_autoUpdateLock)) return;
        try
        {
            var now = DateTimeOffset.Now;
            foreach (var profile in Settings.Profiles
                         .Where(x => x.IsRemote && x.AutoUpdate)
                         .ToList())
            {
                var interval = Math.Max(1, profile.AutoUpdateInterval);
                if (profile.LastUpdated is { } lastUpdated &&
                    now - lastUpdated < TimeSpan.FromMinutes(interval))
                    continue;

                try
                {
                    await UpdateSubscriptionAsync(profile.Id);
                    AddLog($"Auto-updated remote profile '{profile.Name}'.");
                }
                catch (Exception ex)
                {
                    AddLog($"Auto-update failed for '{profile.Name}': {ex.Message}");
                }
            }
        }
        finally
        {
            Monitor.Exit(_autoUpdateLock);
        }
    }

    private async Task AutoUpdateLoopAsync(CancellationToken token)
    {
        // SFM parity: run an overdue check immediately at startup instead of
        // waiting a full poll interval, then keep polling.
        while (!token.IsCancellationRequested)
        {
            try
            {
                await UpdateAutoUpdateProfilesAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad cycle kill the loop permanently.
                AddLog($"Auto-update cycle failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public Profile AddProfile(string name, string configPath)
    {
        var profile = new Profile
        {
            Id = Settings.NextProfileId++,
            Order = Settings.Profiles.Count,
            Name = UniqueName(name),
            Type = ProfileType.Local,
            Path = configPath,
            // LastUpdated stays null for local profiles: it tracks remote
            // subscription updates, not when the file was added.
        };
        Settings.Profiles.Add(profile);
        Settings.SelectedProfileId = profile.Id;
        Save();
        return profile;
    }

    /// <summary>
    /// Downloads and validates a subscription, then registers it as a remote
    /// profile. SFA/SFM parity: a new remote profile defaults to auto update on,
    /// every 60 minutes, and the interval is floored at 15 minutes.
    /// </summary>
    public async Task<Profile> AddSubscriptionAsync(
        string name,
        string sourceUrl,
        bool autoUpdate = true,
        int autoUpdateIntervalMinutes = ProfileDialogs.DefaultAutoUpdateIntervalMinutes)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(Loc.Get("Enter a valid HTTP or HTTPS subscription URL.", "请输入有效的 HTTP 或 HTTPS 订阅 URL。"), nameof(sourceUrl));

        var content = await DownloadSubscriptionAsync(sourceUrl);

        var subscriptionsDirectory = Path.Combine(_dataDirectory, "subscriptions");
        Directory.CreateDirectory(subscriptionsDirectory);

        var profile = new Profile
        {
            Id = Settings.NextProfileId++,
            Order = Settings.Profiles.Count,
            Name = UniqueName(string.IsNullOrWhiteSpace(name) ? uri.Host : name),
            Type = ProfileType.Remote,
            Path = Path.Combine(subscriptionsDirectory, $"{Guid.NewGuid():N}.json"),
            RemoteUrl = sourceUrl,
            AutoUpdate = autoUpdate,
            AutoUpdateInterval = ProfileDialogs.ClampAutoUpdateInterval(autoUpdateIntervalMinutes),
            LastUpdated = DateTimeOffset.Now,
        };
        await _subscriptionLock.WaitAsync();
        try
        {
            await AtomicFile.WriteAllTextAsync(profile.Path, content);
            Settings.Profiles.Add(profile);
            Settings.SelectedProfileId = profile.Id;
            Save();
        }
        finally
        {
            _subscriptionLock.Release();
        }
        return profile;
    }

    /// <summary>
    /// Downloads the subscription, validates it and replaces the cached content.
    /// Returns true when the on-disk content actually changed. When the profile
    /// is the selected one and the service is running, a content change restarts
    /// the service so the new configuration takes effect (SFA/SFM reload parity).
    /// </summary>
    public async Task<bool> UpdateSubscriptionAsync(long id)
    {
        var profile = Settings.Profiles.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("Profile not found.", "未找到配置。"));
        if (!profile.IsRemote || string.IsNullOrWhiteSpace(profile.RemoteUrl))
            throw new InvalidOperationException(Loc.Get("This is a local profile, not a subscription.", "这是本地配置，不是订阅。"));

        bool changed;
        try
        {
            var requestedUrl = profile.RemoteUrl;
            var content = await DownloadSubscriptionAsync(requestedUrl);

            await _subscriptionLock.WaitAsync();
            try
            {
                // Discard a response if the profile was edited or removed during download.
                if (!Settings.Profiles.Contains(profile) || profile.RemoteUrl != requestedUrl) return false;
                // SFA/SFM: skip the write entirely when nothing changed.
                var existing = File.Exists(profile.Path) ? await File.ReadAllTextAsync(profile.Path) : null;
                changed = existing != content;
                if (changed)
                {
                    await AtomicFile.WriteAllTextAsync(profile.Path, content);
                }
                profile.LastUpdated = DateTimeOffset.Now;
                profile.LastUpdateError = null;
                Save();
            }
            finally
            {
                _subscriptionLock.Release();
            }
        }
        catch (Exception ex)
        {
            profile.LastUpdateError = ex.Message;
            Save();
            throw;
        }

        if (changed)
        {
            try
            {
                await RestartServiceIfRunningAsync(profile.Id);
            }
            catch (Exception ex)
            {
                // The update itself succeeded; a failed restart must not fail it.
                AddLog($"Service restart after updating '{profile.Name}' failed: {ex.Message}");
            }
        }
        return changed;
    }

    /// <summary>
    /// Restarts the service when the given profile is the selected one and the
    /// service is currently running, so on-disk content changes take effect.
    /// </summary>
    public async Task<bool> RestartServiceIfRunningAsync(long profileId)
    {
        if (Settings.SelectedProfileId != profileId || !Core.State.IsRunning) return false;
        AddLog("Restarting service to apply the updated profile...");
        await StopAsync();
        await StartAsync();
        return true;
    }

    /// <summary>Downloads subscription content and checks it with the bundled core.</summary>
    private static async Task<string> DownloadSubscriptionAsync(string url)
    {
        var content = await SubscriptionHttpClient.GetStringAsync(url);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(Loc.Get("The subscription returned an empty configuration.", "订阅返回了空配置。"));
        await ConfigurationValidator.CheckAsync(content);
        return content;
    }

    /// <summary>SFM uniqueName: append " (n)" until the name is unique.</summary>
    private string UniqueName(string name)
    {
        if (Settings.Profiles.All(x => x.Name != name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (Settings.Profiles.All(x => x.Name != candidate)) return candidate;
        }
    }

    public void SetProfileAutoUpdate(long id, bool autoUpdate)
    {
        var profile = Settings.Profiles.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("Profile not found.", "未找到配置。"));
        if (!profile.IsRemote)
            throw new InvalidOperationException(Loc.Get("Auto update is only available for remote profiles.", "仅远程配置支持自动更新。"));
        profile.AutoUpdate = autoUpdate;
        Save();
    }

    public async Task UpdateProfileAsync(long id, string name, string source, int? autoUpdateIntervalMinutes = null)
    {
        var profile = Settings.Profiles.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("Profile not found.", "未找到配置。"));
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Unnamed profile" : name.Trim();
        var trimmedSource = source.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSource))
            throw new ArgumentException(Loc.Get("Profile source cannot be empty.", "配置来源不能为空。"), nameof(source));

        var sourceChanged = false;
        string? remoteContent = null;
        if (profile.IsRemote)
        {
            if (!Uri.TryCreate(trimmedSource, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException(Loc.Get("Enter a valid HTTP or HTTPS subscription URL.", "请输入有效的 HTTP 或 HTTPS 订阅 URL。"), nameof(source));
            sourceChanged = !string.Equals(profile.RemoteUrl, trimmedSource, StringComparison.Ordinal);
            if (sourceChanged) remoteContent = await DownloadSubscriptionAsync(trimmedSource);
        }
        else
        {
            if (!File.Exists(trimmedSource))
                throw new FileNotFoundException(Loc.Get("Configuration file was not found.", "未找到配置文件。"), trimmedSource);
            sourceChanged = !string.Equals(profile.Path, trimmedSource, StringComparison.OrdinalIgnoreCase);
            if (sourceChanged) await ConfigurationValidator.CheckAsync(await File.ReadAllTextAsync(trimmedSource));
        }

        await _subscriptionLock.WaitAsync();
        try
        {
            if (!Settings.Profiles.Contains(profile))
                throw new InvalidOperationException(Loc.Get("Profile not found.", "未找到配置。"));
            if (remoteContent is not null)
            {
                await AtomicFile.WriteAllTextAsync(profile.Path, remoteContent);
                profile.LastUpdated = DateTimeOffset.Now;
                profile.LastUpdateError = null;
            }
            if (profile.IsRemote) profile.RemoteUrl = trimmedSource;
            else profile.Path = trimmedSource;
            profile.Name = trimmedName;
            if (autoUpdateIntervalMinutes is { } interval && profile.IsRemote)
                profile.AutoUpdateInterval = ProfileDialogs.ClampAutoUpdateInterval(interval);
            Save();
        }
        finally { _subscriptionLock.Release(); }

        if (sourceChanged) await RestartServiceIfRunningAsync(id);
    }

    public async Task SelectProfileAsync(long id)
    {
        if (Settings.SelectedProfileId == id) return;
        var profile = Settings.Profiles.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("Profile not found.", "未找到配置。"));
        await ConfigurationValidator.CheckAsync(await File.ReadAllTextAsync(profile.Path));
        Settings.SelectedProfileId = id;
        Save();

        // SFA/SFM: switching the profile while the service runs restarts the
        // service on the newly selected profile.
        if (Core.State.IsRunning)
        {
            await StopAsync();
            await StartAsync();
        }
    }

    public async Task RemoveProfileAsync(long id)
    {
        var profile = Settings.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null) return;

        // Deleting the active profile stops the running service first.
        if (Settings.SelectedProfileId == id && Core.State.IsRunning)
        {
            await StopAsync();
        }

        Settings.Profiles.Remove(profile);

        // Delete app-managed files: downloaded subscriptions and clipboard-imported
        // JSON both live under the data directory. User-owned files elsewhere
        // on disk are never touched.
        if (profile.Path.StartsWith(_dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(profile.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (Settings.SelectedProfileId == id)
            Settings.SelectedProfileId = Settings.Profiles.FirstOrDefault()?.Id;
        Save();
    }

    public Profile? ActiveProfile() =>
        Settings.Profiles.FirstOrDefault(profile => profile.Id == Settings.SelectedProfileId);

    private AppSettings Load()
    {
        if (!File.Exists(_settingsPath)) return new AppSettings();
        var json = File.ReadAllText(_settingsPath);
        try
        {
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return MigrateLegacySettings(json);
        }
    }

    private static AppSettings MigrateLegacySettings(string json)
    {
        var settings = new AppSettings();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var oldToNew = new Dictionary<string, long>();

            if (root.TryGetProperty("Profiles", out var profilesNode) &&
                profilesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in profilesNode.EnumerateArray())
                {
                    var oldId = node.TryGetProperty("Id", out var idNode) ? idNode.GetString() : null;
                    var name = node.TryGetProperty("Name", out var nameNode) ? nameNode.GetString() : null;
                    var configPath = node.TryGetProperty("ConfigPath", out var pathNode) ? pathNode.GetString() : null;
                    var sourceUrl = node.TryGetProperty("SourceUrl", out var urlNode) ? urlNode.GetString() : null;

                    var profile = new Profile
                    {
                        Id = settings.NextProfileId++,
                        Order = settings.Profiles.Count,
                        Name = string.IsNullOrWhiteSpace(name) ? "Imported profile" : name,
                        Type = string.IsNullOrWhiteSpace(sourceUrl) ? ProfileType.Local : ProfileType.Remote,
                        Path = configPath ?? string.Empty,
                        RemoteUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                        LastUpdated = string.IsNullOrWhiteSpace(sourceUrl) ? null : DateTimeOffset.Now,
                    };
                    settings.Profiles.Add(profile);
                    if (!string.IsNullOrWhiteSpace(oldId)) oldToNew[oldId] = profile.Id;
                }
            }

            if (root.TryGetProperty("ActiveProfileId", out var activeNode) &&
                activeNode.ValueKind == JsonValueKind.String &&
                activeNode.GetString() is { } activeId &&
                oldToNew.TryGetValue(activeId, out var selectedId))
            {
                settings.SelectedProfileId = selectedId;
            }
            else
            {
                settings.SelectedProfileId = settings.Profiles.FirstOrDefault()?.Id;
            }

            if (root.TryGetProperty("SingBoxPath", out var singBoxPathNode) &&
                singBoxPathNode.ValueKind == JsonValueKind.String)
                settings.DevelopmentSingBoxPath = singBoxPathNode.GetString() ?? string.Empty;

            if (root.TryGetProperty("WorkingDirectory", out var workingDirectoryNode) &&
                workingDirectoryNode.ValueKind == JsonValueKind.String)
                settings.DevelopmentWorkingDirectory = workingDirectoryNode.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        return settings;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
