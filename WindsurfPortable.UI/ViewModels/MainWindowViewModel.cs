using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using ReactiveUI;
using Velopack;
using Velopack.Sources;
using WindsurfPortable;

namespace WindsurfPortable.UI.ViewModels;

[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
internal partial class JsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(UpdateResponse))]
internal partial class UpdateResponseContext : JsonSerializerContext { }

public partial class MainWindowViewModel : ReactiveObject
{
    private const string DefaultLauncherUpdateRepoUrl = "https://github.com/KillTrot/WindsurfPortable";

    public ObservableCollection<string> Profiles { get; } = new();

    public ObservableCollection<string> AutoHideOptions { get; } = new() { "Immediately", "Once running", "Never" };

    private string _autoHideMode = "Once running";
    public string AutoHideMode
    {
        get => _autoHideMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoHideMode, value);
            SaveSettings();
        }
    }

    public event EventHandler? HideToTrayRequested;

    private string _defaultProfile = "default";
    public string DefaultProfile
    {
        get => _defaultProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _defaultProfile, value);
            SaveSettings();
            DefaultProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _autoStartDefaultProfile;
    public bool AutoStartDefaultProfile
    {
        get => _autoStartDefaultProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoStartDefaultProfile, value);
            SaveSettings();
        }
    }

    private bool _isWindsurfRunning;
    public bool IsWindsurfRunning
    {
        get => _isWindsurfRunning;
        private set => this.RaiseAndSetIfChanged(ref _isWindsurfRunning, value);
    }

    public bool CanStartDefaultProfile => !string.IsNullOrWhiteSpace(DefaultProfile);

    public event EventHandler? DefaultProfileChanged;

    private string _selectedProfile = "default";
    public string SelectedProfile
    {
        get => _selectedProfile;
        set => this.RaiseAndSetIfChanged(ref _selectedProfile, value);
    }

    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => this.RaiseAndSetIfChanged(ref _isUpdateAvailable, value);
    }

    private bool _isLauncherUpdateAvailable;
    public bool IsLauncherUpdateAvailable
    {
        get => _isLauncherUpdateAvailable;
        set => this.RaiseAndSetIfChanged(ref _isLauncherUpdateAvailable, value);
    }

    private string _launcherUpdateMessage = "";
    public string LauncherUpdateMessage
    {
        get => _launcherUpdateMessage;
        set => this.RaiseAndSetIfChanged(ref _launcherUpdateMessage, value);
    }

    private string _launcherUpdateRepoUrl = "";
    public string LauncherUpdateRepoUrl
    {
        get => _launcherUpdateRepoUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _launcherUpdateRepoUrl, value);
            SaveSettings();
        }
    }

    private string _updateMessage = "";
    public string UpdateMessage
    {
        get => _updateMessage;
        set => this.RaiseAndSetIfChanged(ref _updateMessage, value);
    }

    private bool _isWindsurfMissing;
    public bool IsWindsurfMissing
    {
        get => _isWindsurfMissing;
        set => this.RaiseAndSetIfChanged(ref _isWindsurfMissing, value);
    }

    private bool _enableSingleInstancePatch = true;
    public bool EnableSingleInstancePatch
    {
        get => _enableSingleInstancePatch;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableSingleInstancePatch, value);
            SaveSettings();
        }
    }

    private bool _enableMcpSyncIsolation = true;
    public bool EnableMcpSyncIsolation
    {
        get => _enableMcpSyncIsolation;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableMcpSyncIsolation, value);
            SaveSettings();
        }
    }

    private bool _enableGlobalRecentsPatch = true;
    public bool EnableGlobalRecentsPatch
    {
        get => _enableGlobalRecentsPatch;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableGlobalRecentsPatch, value);
            SaveSettings();
        }
    }

    public ReactiveCommand<Unit, Unit> LaunchCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipUpdateCommand { get; }
    public ReactiveCommand<string, Unit> DownloadInitialCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckLauncherUpdatesCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyLauncherUpdateCommand { get; }

    private UpdateManager? _updateManager;
    private string _pendingExtractPath = "";
    private string _pendingNewVersion = "";

    private Velopack.UpdateManager? _launcherUpdateManager;
    private Velopack.UpdateInfo? _launcherUpdateInfo;

    public MainWindowViewModel()
    {
        LoadSettings();

        if (string.IsNullOrWhiteSpace(LauncherUpdateRepoUrl))
            LauncherUpdateRepoUrl = DefaultLauncherUpdateRepoUrl;

        CheckIfWindsurfMissing();
        LoadProfiles();

        LaunchCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            IsBusy = true;
            IsWindsurfRunning = true;
            StatusMessage = $"Launching Windsurf ({SelectedProfile} profile)...";

            if (ShouldAutoHideImmediately())
                HideToTrayRequested?.Invoke(this, EventArgs.Empty);

            try
            {
                Process? proc = null;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var options = new LegacyConsoleProgram.LauncherOptions(
                        AppContext.BaseDirectory,
                        SelectedProfile,
                        Array.Empty<string>(),
                        EnableSingleInstancePatch,
                        EnableMcpSyncIsolation,
                        EnableGlobalRecentsPatch
                    );

                    proc = LegacyConsoleProgram.Launch(options, waitForExit: false);
                });

                if (proc == null)
                {
                    StatusMessage = "Failed to start Windsurf.";
                    IsWindsurfRunning = false;
                    return;
                }

                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            IsWindsurfRunning = false;
                            StatusMessage = "Windsurf exited.";
                        });
                    };
                }
                catch
                {
                    // If we can't subscribe, we still keep the UI responsive.
                }

                StatusMessage = "Windsurf started.";

                if (ShouldAutoHideOnceRunning())
                    HideToTrayRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                IsWindsurfRunning = false;
            }
            finally
            {
                IsBusy = false;
            }
        });

        CreateProfileCommand = ReactiveCommand.Create(() =>
        {
            if (!Profiles.Contains("work"))
            {
                Profiles.Add("work");
                SelectedProfile = "work";
            }
        });

        ApplyUpdateCommand = ReactiveCommand.Create(() =>
        {
            if (_updateManager != null && !string.IsNullOrEmpty(_pendingExtractPath))
            {
                _updateManager.ApplyUpdateAndRestart(_pendingExtractPath, AppContext.BaseDirectory);
            }
        });

        SkipUpdateCommand = ReactiveCommand.Create(() =>
        {
            if (_updateManager != null && !string.IsNullOrEmpty(_pendingNewVersion))
            {
                _updateManager.SkipUpdate(_pendingNewVersion);
            }
            IsUpdateAvailable = false;
        });

        DownloadInitialCommand = ReactiveCommand.CreateFromTask<string>(async channel =>
        {
            await DownloadInitialWindsurfAsync(channel);
        });

        CheckLauncherUpdatesCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await CheckForLauncherUpdatesAsync();
        });

        ApplyLauncherUpdateCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyLauncherUpdateAsync();
        });

        if (!IsWindsurfMissing)
        {
            InitializeUpdateChecker();
        }
    }

    private void LoadSettings()
    {
        string patchStateFile = Path.Combine(AppContext.BaseDirectory, "resources", ".portable_patch_state.json");
        if (File.Exists(patchStateFile))
        {
            try
            {
                var stateStr = File.ReadAllText(patchStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString);
                if (state != null)
                {
                    if (state.TryGetValue("enable_single_instance_patch", out var sip)) _enableSingleInstancePatch = sip == "true";
                    if (state.TryGetValue("enable_mcp_sync_isolation", out var msi)) _enableMcpSyncIsolation = msi == "true";
                    if (state.TryGetValue("enable_global_recents_patch", out var grp)) _enableGlobalRecentsPatch = grp == "true";
                    if (state.TryGetValue("default_profile", out var dp) && !string.IsNullOrWhiteSpace(dp)) _defaultProfile = dp;
                    if (state.TryGetValue("auto_start_default_profile", out var asdp)) _autoStartDefaultProfile = asdp == "true";
                    if (state.TryGetValue("auto_hide_mode", out var ahm) && !string.IsNullOrWhiteSpace(ahm)) _autoHideMode = NormalizeAutoHideMode(ahm);
                    if (state.TryGetValue("launcher_update_repo_url", out var repo) && !string.IsNullOrWhiteSpace(repo)) _launcherUpdateRepoUrl = repo;
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        string patchStateFile = Path.Combine(AppContext.BaseDirectory, "resources", ".portable_patch_state.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(patchStateFile)!);
            System.Collections.Generic.Dictionary<string, string> state = new();
            if (File.Exists(patchStateFile))
            {
                var stateStr = File.ReadAllText(patchStateFile);
                state = System.Text.Json.JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString) ?? new();
            }

            state["enable_single_instance_patch"] = _enableSingleInstancePatch ? "true" : "false";
            state["enable_mcp_sync_isolation"] = _enableMcpSyncIsolation ? "true" : "false";
            state["enable_global_recents_patch"] = _enableGlobalRecentsPatch ? "true" : "false";
            state["default_profile"] = DefaultProfile;
            state["auto_start_default_profile"] = AutoStartDefaultProfile ? "true" : "false";
            state["auto_hide_mode"] = AutoHideMode;
            state["launcher_update_repo_url"] = LauncherUpdateRepoUrl;

            File.WriteAllText(patchStateFile, System.Text.Json.JsonSerializer.Serialize(state, JsonContext.Default.DictionaryStringString));
        }
        catch { }
    }

    private Velopack.UpdateManager? CreateLauncherUpdateManager()
    {
        if (string.IsNullOrWhiteSpace(LauncherUpdateRepoUrl))
            return null;

        try
        {
            return new Velopack.UpdateManager(new GithubSource(LauncherUpdateRepoUrl.Trim(), string.Empty, prerelease: false));
        }
        catch
        {
            return null;
        }
    }

    private async System.Threading.Tasks.Task CheckForLauncherUpdatesAsync()
    {
        IsLauncherUpdateAvailable = false;
        _launcherUpdateInfo = null;

        var mgr = CreateLauncherUpdateManager();
        _launcherUpdateManager = mgr;

        if (mgr == null)
        {
            LauncherUpdateMessage = "Set a GitHub repo URL to enable launcher updates.";
            return;
        }

        if (!mgr.IsInstalled)
        {
            LauncherUpdateMessage = "Launcher updates are disabled unless installed via Velopack.";
            return;
        }

        try
        {
            LauncherUpdateMessage = "Checking for launcher updates…";
            var updateInfo = await mgr.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                LauncherUpdateMessage = "Launcher is up to date.";
                return;
            }

            _launcherUpdateInfo = updateInfo;
            IsLauncherUpdateAvailable = true;
            LauncherUpdateMessage = $"Launcher update available: {updateInfo.TargetFullRelease.Version}";
        }
        catch (Exception ex)
        {
            LauncherUpdateMessage = $"Launcher update check failed: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task ApplyLauncherUpdateAsync()
    {
        var mgr = _launcherUpdateManager;
        var info = _launcherUpdateInfo;

        if (mgr == null || info == null)
            return;

        try
        {
            LauncherUpdateMessage = "Downloading launcher update…";
            await mgr.DownloadUpdatesAsync(info);
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            LauncherUpdateMessage = $"Launcher update failed: {ex.Message}";
        }
    }

    public void SetSelectedProfileToDefaultIfPossible()
    {
        if (!string.IsNullOrWhiteSpace(DefaultProfile) && Profiles.Contains(DefaultProfile))
            SelectedProfile = DefaultProfile;
    }

    public void StartSelectedProfileInBackground()
    {
        _ = LaunchCommand.Execute();
    }

    public void StartDefaultProfileInBackground()
    {
        if (!string.IsNullOrWhiteSpace(DefaultProfile))
            SelectedProfile = DefaultProfile;

        StartSelectedProfileInBackground();
    }

    private static string NormalizeAutoHideMode(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "immediately" => "Immediately",
            "once running" => "Once running",
            "once_running" => "Once running",
            "oncerunning" => "Once running",
            "never" => "Never",
            _ => "Once running",
        };
    }

    private bool ShouldAutoHideImmediately()
        => string.Equals(AutoHideMode, "Immediately", StringComparison.OrdinalIgnoreCase);

    private bool ShouldAutoHideOnceRunning()
        => string.Equals(AutoHideMode, "Once running", StringComparison.OrdinalIgnoreCase);

    private void CheckIfWindsurfMissing()
    {
        string baseDir = AppContext.BaseDirectory;
        IsWindsurfMissing = !File.Exists(Path.Combine(baseDir, "Windsurf.exe")) &&
                            !File.Exists(Path.Combine(baseDir, "Windsurf - Next.exe"));
    }

    internal class InternalUpdateResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    [JsonSerializable(typeof(InternalUpdateResponse))]
    internal partial class InternalUpdateResponseContext : JsonSerializerContext { }

    private async System.Threading.Tasks.Task DownloadInitialWindsurfAsync(string channel)
    {
        IsBusy = true;
        StatusMessage = $"Fetching download URL for {channel}...";

        try
        {
            string apiUrl = channel == "next"
                ? "https://windsurf-stable.codeium.com/api/update/win32-x64-archive/next/latest"
                : "https://windsurf-stable.codeium.com/api/update/win32-x64-archive/stable/latest";

            using var httpClient = new HttpClient();
            var response = await httpClient.GetFromJsonAsync(apiUrl, InternalUpdateResponseContext.Default.InternalUpdateResponse);

            if (response == null || string.IsNullOrEmpty(response.Url))
            {
                StatusMessage = "Failed to get download URL.";
                return;
            }

            StatusMessage = "Downloading Windsurf...";
            string tempZipPath = Path.Combine(AppContext.BaseDirectory, "windsurf-initial.zip");

            var downloadResponse = await httpClient.GetAsync(response.Url, HttpCompletionOption.ResponseHeadersRead);
            downloadResponse.EnsureSuccessStatusCode();

            using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await downloadResponse.Content.CopyToAsync(fs);
            }

            StatusMessage = "Extracting Windsurf...";
            await System.Threading.Tasks.Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(tempZipPath, AppContext.BaseDirectory, overwriteFiles: true);
                File.Delete(tempZipPath);
            });

            StatusMessage = "Download complete!";
            IsWindsurfMissing = false;
            InitializeUpdateChecker();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InitializeUpdateChecker()
    {
        string baseDir = AppContext.BaseDirectory;
        string appDir = Path.Combine(baseDir, "resources", "app");
        bool isNextBuild = File.Exists(Path.Combine(baseDir, "Windsurf - Next.exe"));
        string patchStateFile = Path.Combine(baseDir, "resources", ".portable_patch_state.json");

        _updateManager = new UpdateManager(baseDir, appDir, isNextBuild, patchStateFile);
        _updateManager.UpdateAvailable += (newVersion, extractPath) =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _pendingNewVersion = newVersion;
                _pendingExtractPath = extractPath;
                UpdateMessage = $"Update available: {newVersion}";
                IsUpdateAvailable = true;
            });
        };

        System.Threading.Tasks.Task.Run(() => _updateManager.CheckForUpdatesAsync());
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        Profiles.Add("default");

        string profilesDir = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.GetDirectories(profilesDir))
            {
                var name = Path.GetFileName(dir);
                if (name != "default" && !string.IsNullOrEmpty(name))
                {
                    Profiles.Add(name);
                }
            }
        }
    }
}
