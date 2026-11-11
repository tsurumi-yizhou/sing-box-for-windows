using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SFW.Models;
using SFW.Services.Core;
using Windows.Globalization;

namespace SFW.Services;

public sealed class AppState : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _autoUpdateCts = new();
    private readonly object _autoUpdateLock = new();
    private readonly Task _autoUpdateLoopTask;

    /// <summary>
    /// Last time each remote profile was checked, whether it succeeded or not.
    /// Upstream a0668e4: scheduling uses max(lastUpdated, lastAttempt) so a failed
    /// update waits a full interval instead of being retried on every poll.
    /// </summary>
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastUpdateAttempts = new();

    /// <summary>Wakes the auto-update scheduler as soon as profile settings change.</summary>
    private readonly SemaphoreSlim _autoUpdateSignal = new(0, 1);

    /// <summary>Longest delay Task.Delay represents, matching the upstream timer clamp.</summary>
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Serializes subscription downloads/writes so manual and automatic
    /// updates can never write the same profile file concurrently.</summary>
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);

    private const long MaximumRemoteProfileBytes = 16 * 1024 * 1024;
    private const long MaximumRemoteErrorBytes = 64 * 1024;

    /// <summary>Shared client for subscription downloads (SFA HTTPClient parity:
    /// per-request User-Agent, no per-request allocation).</summary>
    private static readonly HttpClient SubscriptionHttpClient = CreateSubscriptionHttpClient();

    private static HttpClient CreateSubscriptionHttpClient()
    {
        // The User-Agent is set per request: it includes the daemon's sing-box
        // version and the UI language, which are only known after startup.
        return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public AppSettings Settings { get; private set; } = new();
    public ICoreController Core { get; }
    public List<string> Logs { get; } = [];
    public event EventHandler<string>? LogAdded;
    public event EventHandler? LogsCleared;
    private readonly object _logLock = new();
    private readonly object _settingsWriteLock = new();

    /// <summary>Directory holding settings.json, downloads and imported configurations.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>
    /// Per-user local app data root, with the smoke test isolated under the temp
    /// directory. Program.Main needs this before App exists (language override).
    /// </summary>
    private static string ResolveLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Environment.GetCommandLineArgs().Contains("--ui-smoke-test"))
            localAppData = Path.Combine(Path.GetTempPath(), "SFW-ui-smoke-test");
        return localAppData;
    }

    /// <summary>
    /// Applies the saved language before WinUI initializes its resource system.
    /// x:Uid text is resolved when the first page loads, and that happens after
    /// Application.Start, so setting the override in the App constructor is too
    /// late for compiled XAML resources (Loc.Get would already honour it).
    /// </summary>
    public static void ApplySavedLanguageOverrideBeforeXaml()
    {
        try
        {
            var settingsPath = Path.Combine(ResolveLocalAppData(), "Sing-Box", "settings.json");
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(settingsPath), AppJsonContext.Default.AppSettings);
            ApplicationLanguages.PrimaryLanguageOverride = settings?.LanguageTag ?? string.Empty;
        }
        catch (Exception)
        {
            // A broken settings file must never keep the app from starting; the
            // AppState constructor re-applies the same override after loading.
        }
    }

    /// <summary>App-owned copies of imported local configurations.</summary>
    private string ProfilesDirectory => Path.Combine(_dataDirectory, "profiles");

    public AppState()
    {
        var localAppData = ResolveLocalAppData();
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
        ApplyLanguageOverride();
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
                if (profile.SourcePath is { } sourcePath &&
                    sourcePath.StartsWith(legacyDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    profile.SourcePath = _dataDirectory + sourcePath[legacyDirectory.Length..];
                }
            }
            Save();
        }

        // Older builds registered local profiles with the user's own file path.
        // The app owns its configurations now, so adopt those files once.
        MigrateLocalProfilesIntoDataDirectory();

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

    /// <summary>
    /// Applies the language the user picked in App settings. The packaged resource
    /// system resolves x:Uid and Loc.Get from this override, so it has to run before
    /// the first resource lookup; an unparsable tag must never block startup.
    /// </summary>
    private void ApplyLanguageOverride()
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = Settings.LanguageTag ?? string.Empty;
        }
        catch (Exception)
        {
            // Fall back to the Windows language list.
        }
    }

    public void Save()
    {
        lock (_settingsWriteLock)
            AtomicFile.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, AppJsonContext.Default.AppSettings));
        // Profile changes (added, removed, interval edited) can move the next
        // due time; wake the scheduler instead of waiting for its current timer.
        try { _autoUpdateSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task StartAsync()
    {
        StartupDiag.Log("AppState.StartAsync: enter");
        var profile = ActiveProfile()
            ?? throw new InvalidOperationException(Loc.Get("AppState.SelectOrImportAProfileBeforeConnecting"));
        StartupDiag.Log($"AppState.StartAsync: profile={profile.Path}");
        var config = await File.ReadAllTextAsync(profile.Path);
        StartupDiag.Log($"AppState.StartAsync: config read, {config.Length} chars");
        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidOperationException(Loc.Get("AppState.TheSelectedProfileIsEmpty"));
        config = RuleSetMirror.RewriteGithubRuleSetUrls(config, AddLog);
        StartupDiag.Log("AppState.StartAsync: rule-set rewrite done, calling Core.StartAsync");
        await Core.StartAsync(config, BuildStartOptions());
        StartupDiag.Log("AppState.StartAsync: Core.StartAsync returned");
    }

    /// <summary>
    /// SFA/SFM pass the debug-tool preferences on every service start; the daemon
    /// merges them into its saved start options.
    /// </summary>
    private CoreStartOptions BuildStartOptions()
    {
        var limit = Settings.OomMemoryLimitMegabytes;
        if (!CoreStartOptions.MemoryLimitMegabytes.Contains(limit))
            limit = CoreStartOptions.MemoryLimitMegabytes[0];
        return new CoreStartOptions(
            Settings.OomKillerEnabled,
            Settings.OomKillerKillConnections,
            limit * 1024L * 1024L,
            Settings.PowerReportEnabled);
    }

    /// <summary>
    /// Persists a debug-tool preference and restarts a running service so the
    /// daemon picks it up (SFA/SFM restart the tunnel from their report screens).
    /// </summary>
    public async Task ApplyDebugReportSettingsAsync(Action<AppSettings> mutate)
    {
        mutate(Settings);
        Save();
        if (!Core.State.IsRunning) return;
        AddLog("Restarting service to apply the debug report settings...");
        await StopAsync();
        await StartAsync();
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
            var now = DateTimeOffset.UtcNow;
            var dueProfiles = Settings.Profiles
                .Where(x => x.IsRemote && x.AutoUpdate)
                .Where(profile => NextUpdateTime(profile) <= now)
                .ToList();

            foreach (var profile in dueProfiles)
            {
                // Record the attempt before running: a failed update must wait a
                // full interval instead of retrying on every poll (a0668e4).
                _lastUpdateAttempts[profile.Id] = now;
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

    /// <summary>
    /// Upstream a0668e4: due time is max(lastUpdated, lastAttempt) + interval, so
    /// transient failures back off for one interval instead of retrying per poll.
    /// </summary>
    private DateTimeOffset NextUpdateTime(Profile profile)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, profile.AutoUpdateInterval));
        var last = profile.LastUpdated ?? DateTimeOffset.MinValue;
        if (_lastUpdateAttempts.TryGetValue(profile.Id, out var attempt) && attempt > last)
            last = attempt;
        return last + interval;
    }

    private async Task AutoUpdateLoopAsync(CancellationToken token)
    {
        // SFM parity: run an overdue check immediately at startup instead of
        // waiting for the first scheduled time.
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
                await WaitForNextAutoUpdateAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Sleeps until the earliest due time instead of polling every minute; a
    /// settings change in <see cref="Save"/> cuts the wait short so a new or
    /// edited profile is scheduled without delay.
    /// </summary>
    private async Task WaitForNextAutoUpdateAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var next = Settings.Profiles
            .Where(x => x.IsRemote && x.AutoUpdate)
            .Select(NextUpdateTime)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var delay = next - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > MaximumTimerDelay) delay = MaximumTimerDelay;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Task.Delay(delay, linked.Token);
        var signal = _autoUpdateSignal.WaitAsync(linked.Token);
        await Task.WhenAny(timer, signal);
        linked.Cancel();
        try { await Task.WhenAll(timer, signal); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Registers an app-managed configuration file as a local profile.
    /// <paramref name="configPath"/> must be a file the app owns (under the data
    /// directory); use <see cref="AddLocalProfileAsync"/> for user-picked files.
    /// </summary>
    public Profile AddProfile(string name, string configPath, string? sourcePath = null)
    {
        var profile = new Profile
        {
            Id = Settings.NextProfileId++,
            Order = Settings.Profiles.Count,
            Name = UniqueName(name),
            Type = ProfileType.Local,
            Path = configPath,
            SourcePath = sourcePath,
            // LastUpdated stays null for local profiles: it tracks remote
            // subscription updates, not when the file was added.
        };
        Settings.Profiles.Add(profile);
        Settings.SelectedProfileId = profile.Id;
        Save();
        return profile;
    }

    /// <summary>
    /// Imports a user-picked local configuration: validates it, copies it into the
    /// app data directory and registers the copy. Only the copy is stored, so the
    /// profile survives the user moving, renaming or deleting the original file.
    /// </summary>
    public async Task<Profile> AddLocalProfileAsync(string name, string sourcePath, CancellationToken token = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(Loc.Get("AppState.ConfigurationFileWasNotFound"), sourcePath);
        var content = await File.ReadAllTextAsync(sourcePath, token);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(Loc.Get("AppState.TheSelectedProfileIsEmpty"));
        await ConfigurationValidator.CheckAsync(content, token);

        var profileName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name;
        var path = await WriteManagedConfigAsync(content, token);
        return AddProfile(profileName, path, sourcePath);
    }

    /// <summary>Imports raw JSON content (clipboard) as an app-owned local profile.</summary>
    public async Task<Profile> AddLocalProfileContentAsync(string name, string content, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(Loc.Get("AppState.TheSelectedProfileIsEmpty"));
        await ConfigurationValidator.CheckAsync(content, token);
        var path = await WriteManagedConfigAsync(content, token);
        return AddProfile(name, path);
    }

    private async Task<string> WriteManagedConfigAsync(string content, CancellationToken token = default)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        var path = Path.Combine(ProfilesDirectory, $"{Guid.NewGuid():N}.json");
        await AtomicFile.WriteAllTextAsync(path, content, token);
        return path;
    }

    private string WriteManagedConfig(string content)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        var path = Path.Combine(ProfilesDirectory, $"{Guid.NewGuid():N}.json");
        AtomicFile.WriteAllText(path, content);
        return path;
    }

    /// <summary>True when the path is one of the app's own files under the data directory.</summary>
    private bool IsManagedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory))
                            + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// One-time adoption of local profiles that older builds registered with the
    /// user's own file path: copy the file into the data directory and repoint the
    /// profile. The original location is kept in <see cref="Profile.SourcePath"/>
    /// and is never modified or deleted.
    /// </summary>
    private void MigrateLocalProfilesIntoDataDirectory()
    {
        var migrated = false;
        foreach (var profile in Settings.Profiles)
        {
            if (profile.IsRemote) continue;
            profile.SourcePath ??= profile.Path;
            if (IsManagedPath(profile.Path) || !File.Exists(profile.Path)) continue;
            try
            {
                var content = File.ReadAllText(profile.Path);
                if (string.IsNullOrWhiteSpace(content)) continue;
                profile.Path = WriteManagedConfig(content);
                migrated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the external reference when the copy fails.
            }
        }
        if (migrated) Save();
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
            throw new ArgumentException(Loc.Get("AppState.EnterAValidHTTPOrHTTPSSubscriptionURL"), nameof(sourceUrl));

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
            ?? throw new InvalidOperationException(Loc.Get("AppState.ProfileNotFound"));
        if (!profile.IsRemote || string.IsNullOrWhiteSpace(profile.RemoteUrl))
            throw new InvalidOperationException(Loc.Get("AppState.ThisIsALocalProfileNotASubscription"));

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

    /// <summary>
    /// Downloads subscription content and checks it with the bundled core.
    /// Mirrors libbox's HTTPClient (upstream profiles.ts): URL userinfo becomes a
    /// Basic Authorization header, only HTTP 200 counts as success, and both the
    /// profile and the error body are read with a hard size ceiling.
    /// </summary>
    private static async Task<string> DownloadSubscriptionAsync(string url, CancellationToken token = default)
    {
        using var request = CreateSubscriptionRequest(url, await BuildUserAgentAsync(token));
        using var response = await SubscriptionHttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(await DescribeHttpErrorAsync(response, token));

        var content = await ReadLimitedResponseAsync(response, MaximumRemoteProfileBytes, token);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(Loc.Get("AppState.TheSubscriptionReturnedAnEmptyConfiguration"));
        await ConfigurationValidator.CheckAsync(content, token);
        return content;
    }

    /// <summary>Builds the request, folding URL credentials into a Basic header the
    /// way libbox does, so user:password@host URLs keep working.</summary>
    private static HttpRequestMessage CreateSubscriptionRequest(string url, string userAgent)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(Loc.Get("AppState.EnterAValidHTTPOrHTTPSSubscriptionURL"), nameof(url));

        var request = new HttpRequestMessage(HttpMethod.Get, StripUserInfo(uri, out var authorization));
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    /// <summary>Upstream userAgent(): "SFW (sing-box &lt;version&gt;; language zh_CN)".</summary>
    private static async Task<string> BuildUserAgentAsync(CancellationToken token)
    {
        string version;
        try
        {
            version = await DaemonServiceManager.QueryVersionAsync(token) ?? "unknown";
        }
        catch (Exception)
        {
            version = "unknown";
        }
        return $"SFW (sing-box {version}; language {Loc.UiLanguage.Replace('-', '_')})";
    }

    private static string StripUserInfo(Uri uri, out string? authorization)
    {
        authorization = null;
        if (string.IsNullOrEmpty(uri.UserInfo)) return uri.AbsoluteUri;

        var separator = uri.UserInfo.IndexOf(':');
        var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
        var password = separator < 0 ? string.Empty : uri.UserInfo[(separator + 1)..];
        var credentials = $"{Uri.UnescapeDataString(user)}:{Uri.UnescapeDataString(password)}";
        authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.AbsoluteUri;
    }

    private static async Task<string> DescribeHttpErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var status = string.IsNullOrEmpty(response.ReasonPhrase)
            ? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
            : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var body = await ReadLimitedResponseAsync(response, MaximumRemoteErrorBytes, token);
            return $"HTTP {status}: {body}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return $"HTTP {status}: {ex.Message}";
        }
    }

    /// <summary>Reads a response body, refusing to buffer more than the ceiling.</summary>
    private static async Task<string> ReadLimitedResponseAsync(
        HttpResponseMessage response, long maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maximumBytes)
            throw new InvalidOperationException($"Response exceeds {maximumBytes} bytes.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException($"Response exceeds {maximumBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
            ?? throw new InvalidOperationException(Loc.Get("AppState.ProfileNotFound"));
        if (!profile.IsRemote)
            throw new InvalidOperationException(Loc.Get("AppState.AutoUpdateIsOnlyAvailableForRemoteProfiles"));
        profile.AutoUpdate = autoUpdate;
        Save();
    }

    public async Task UpdateProfileAsync(long id, string name, string source, int? autoUpdateIntervalMinutes = null)
    {
        var profile = Settings.Profiles.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("AppState.ProfileNotFound"));
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Unnamed profile" : name.Trim();
        var trimmedSource = source.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSource))
        {
            // Remote profiles always need a URL; a local profile already owns its
            // copy, so an empty field simply keeps that copy.
            if (profile.IsRemote)
                throw new ArgumentException(Loc.Get("AppState.ProfileSourceCannotBeEmpty"), nameof(source));
            trimmedSource = profile.Path;
        }

        var sourceChanged = false;
        string? remoteContent = null;
        string? localContent = null;
        if (profile.IsRemote)
        {
            if (!Uri.TryCreate(trimmedSource, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException(Loc.Get("AppState.EnterAValidHTTPOrHTTPSSubscriptionURL"), nameof(source));
            sourceChanged = !string.Equals(profile.RemoteUrl, trimmedSource, StringComparison.Ordinal);
            if (sourceChanged) remoteContent = await DownloadSubscriptionAsync(trimmedSource);
        }
        else
        {
            // The dialog edits the app-owned copy, which is all the profile needs
            // after import. The original file only matters when the user points at
            // a new one; touching an unchanged managed path must keep working even
            // after the import source was moved or deleted.
            sourceChanged = !string.Equals(profile.Path, trimmedSource, StringComparison.OrdinalIgnoreCase);
            if (sourceChanged)
            {
                if (!File.Exists(trimmedSource))
                    throw new FileNotFoundException(Loc.Get("AppState.ConfigurationFileWasNotFound"), trimmedSource);
                localContent = await File.ReadAllTextAsync(trimmedSource);
                await ConfigurationValidator.CheckAsync(localContent);
            }
        }

        string? retiredManagedPath = null;
        await _subscriptionLock.WaitAsync();
        try
        {
            if (!Settings.Profiles.Contains(profile))
                throw new InvalidOperationException(Loc.Get("AppState.ProfileNotFound"));
            if (remoteContent is not null)
            {
                await AtomicFile.WriteAllTextAsync(profile.Path, remoteContent);
                profile.LastUpdated = DateTimeOffset.Now;
                profile.LastUpdateError = null;
            }
            if (profile.IsRemote)
            {
                profile.RemoteUrl = trimmedSource;
            }
            else if (localContent is not null)
            {
                // A changed local source becomes a fresh app-owned copy; the
                // previous copy is retired after the settings are saved.
                if (IsManagedPath(profile.Path)) retiredManagedPath = profile.Path;
                profile.Path = await WriteManagedConfigAsync(localContent);
                profile.SourcePath = trimmedSource;
            }
            profile.Name = trimmedName;
            if (autoUpdateIntervalMinutes is { } interval && profile.IsRemote)
                profile.AutoUpdateInterval = ProfileDialogs.ClampAutoUpdateInterval(interval);
            Save();
        }
        finally { _subscriptionLock.Release(); }

        if (retiredManagedPath is not null &&
            !string.Equals(retiredManagedPath, profile.Path, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(retiredManagedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (sourceChanged) await RestartServiceIfRunningAsync(id);
    }

    public async Task SelectProfileAsync(long id)
    {
        if (Settings.SelectedProfileId == id) return;
        var profile = Settings.Profiles.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException(Loc.Get("AppState.ProfileNotFound"));
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
        _lastUpdateAttempts.TryRemove(id, out _);

        // Delete app-managed files only: subscriptions and imported configuration
        // copies live under the data directory. The user's original file, kept in
        // SourcePath, is never touched.
        if (IsManagedPath(profile.Path))
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
