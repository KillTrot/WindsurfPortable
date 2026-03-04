using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive;
using System.Text.Json.Serialization;
using System.Threading;
using Avalonia.Threading;
using ReactiveUI;
using ShadUI;
using Velopack;
using Velopack.Sources;
using WindsurfPortable;
using WindsurfPortable.UI;

namespace WindsurfPortable.UI.ViewModels;

[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
internal partial class JsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(UpdateResponse))]
internal partial class UpdateResponseContext : JsonSerializerContext { }

public partial class MainWindowViewModel : ReactiveObject
{
    private const string DefaultLauncherUpdateRepoUrl = "https://github.com/KillTrot/WindsurfPortable";
    private const string OfficialLauncherUpdateRepoUrl = "https://github.com/KillTrot/WindsurfPortable";

    public DialogManager DialogManager { get; } = new();

    public bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public bool IsWindows => OperatingSystem.IsWindows();

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

    private bool _autoDownloadWindsurfUpdates = true;
    public bool AutoDownloadWindsurfUpdates
    {
        get => _autoDownloadWindsurfUpdates;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoDownloadWindsurfUpdates, value);
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

    public bool CanApplyWindsurfUpdate => IsUpdateAvailable && !IsWindsurfRunning && !string.IsNullOrEmpty(_pendingExtractPath);
    public bool IsUpdateAvailableAndWindsurfRunning => IsUpdateAvailable && IsWindsurfRunning;

    public bool IsUpdateOverlayVisible => IsLauncherRestartUpdateAvailable || IsUpdateAvailable;

    public string UpdateOverlayMessage
    {
        get
        {
            if (IsLauncherRestartUpdateAvailable)
                return LauncherRestartUpdateMessage;

            return UpdateMessage;
        }
    }

    public bool ShowLauncherRestartButton => IsLauncherRestartUpdateAvailable;
    public bool ShowWindsurfUpdateButtons => IsUpdateAvailable;

    private bool _isLauncherUpdateAvailable;
    public bool IsLauncherUpdateAvailable
    {
        get => _isLauncherUpdateAvailable;
        set => this.RaiseAndSetIfChanged(ref _isLauncherUpdateAvailable, value);
    }

    private bool _isLauncherRestartUpdateAvailable;
    public bool IsLauncherRestartUpdateAvailable
    {
        get => _isLauncherRestartUpdateAvailable;
        set => this.RaiseAndSetIfChanged(ref _isLauncherRestartUpdateAvailable, value);
    }

    private string _launcherRestartUpdateMessage = "";
    public string LauncherRestartUpdateMessage
    {
        get => _launcherRestartUpdateMessage;
        set => this.RaiseAndSetIfChanged(ref _launcherRestartUpdateMessage, value);
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

    private bool _enableLauncherStartMenuShortcut;
    public bool EnableLauncherStartMenuShortcut
    {
        get => _enableLauncherStartMenuShortcut;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableLauncherStartMenuShortcut, value);
            SaveSettings();
            ApplyLauncherShortcuts();
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
    public ReactiveCommand<Unit, Unit> RestartToApplyLauncherUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateDesktopShortcutCommand { get; }
    public ReactiveCommand<Unit, Unit> DebugCycleBannersCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissUpdateOverlayCommand { get; }

    private UpdateManager? _updateManager;
    private string _pendingExtractPath = "";
    private string _pendingNewVersion = "";

    private Velopack.UpdateManager? _launcherUpdateManager;
    private Velopack.UpdateInfo? _launcherUpdateInfo;

    private CancellationTokenSource? _updateLoopCts;

    private int _debugBannerState;

    public MainWindowViewModel()
    {
        LoadSettings();

        if (string.IsNullOrWhiteSpace(LauncherUpdateRepoUrl))
            LauncherUpdateRepoUrl = DefaultLauncherUpdateRepoUrl;

        if (string.Equals(LauncherUpdateRepoUrl, DefaultLauncherUpdateRepoUrl, StringComparison.OrdinalIgnoreCase))
            LauncherUpdateRepoUrl = OfficialLauncherUpdateRepoUrl;

        ApplyLauncherShortcuts();

        CheckIfWindsurfMissing();
        LoadProfiles();

        LaunchCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            IsBusy = true;
            StatusMessage = $"Launching Windsurf ({SelectedProfile} profile)...";

            if (ShouldAutoHideImmediately())
                HideToTrayRequested?.Invoke(this, EventArgs.Empty);

            try
            {
                Process? proc = null;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var options = new Launcher.LauncherOptions(
                        AppContext.BaseDirectory,
                        null,
                        SelectedProfile,
                        Array.Empty<string>(),
                        EnableSingleInstancePatch,
                        EnableMcpSyncIsolation,
                        EnableGlobalRecentsPatch
                    );

                    proc = Launcher.Launch(options, waitForExit: false);
                });

                if (proc == null)
                {
                    StatusMessage = "Failed to start Windsurf.";
                    IsWindsurfRunning = false;
                    return;
                }

                IsWindsurfRunning = true;

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

        OpenGitHubCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/KillTrot/WindsurfPortable",
                    UseShellExecute = true,
                });
            }
            catch
            {
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
            if (IsWindsurfRunning)
            {
                DialogManager
                    .CreateDialog("Close Windsurf", "Windsurf is currently running. Close it first, then click Update again.")
                    .WithPrimaryButton("OK", () => { })
                    .Dismissible()
                    .Show();
                return;
            }

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

        RestartToApplyLauncherUpdateCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await RestartToApplyLauncherUpdateAsync();
        });

        CreateDesktopShortcutCommand = ReactiveCommand.Create(() =>
        {
            CreateDesktopShortcut();
        });

        DismissUpdateOverlayCommand = ReactiveCommand.Create(() =>
        {
            if (IsLauncherRestartUpdateAvailable)
            {
                IsLauncherRestartUpdateAvailable = false;
                LauncherRestartUpdateMessage = "";
            }
            else if (IsUpdateAvailable)
            {
                IsUpdateAvailable = false;
                UpdateMessage = "";
                _pendingExtractPath = "";
                _pendingNewVersion = "";
            }

            this.RaisePropertyChanged(nameof(IsUpdateOverlayVisible));
            this.RaisePropertyChanged(nameof(UpdateOverlayMessage));
            this.RaisePropertyChanged(nameof(ShowLauncherRestartButton));
            this.RaisePropertyChanged(nameof(ShowWindsurfUpdateButtons));
        });

        DebugCycleBannersCommand = ReactiveCommand.Create(() =>
        {
            _debugBannerState = (_debugBannerState + 1) % 5;

            if (_debugBannerState == 0)
            {
                IsUpdateAvailable = false;
                UpdateMessage = "";
                _pendingExtractPath = "";
                _pendingNewVersion = "";
                IsWindsurfRunning = false;
                IsLauncherRestartUpdateAvailable = false;
                LauncherRestartUpdateMessage = "";
            }
            else if (_debugBannerState == 1)
            {
                IsUpdateAvailable = true;
                UpdateMessage = "Update available: 0.0.0-debug";
                _pendingNewVersion = "0.0.0-debug";
                _pendingExtractPath = Path.Combine(AppContext.BaseDirectory, "windsurf-update-ready-0.0.0-debug");
                IsWindsurfRunning = false;
                IsLauncherRestartUpdateAvailable = false;
                LauncherRestartUpdateMessage = "";
            }
            else if (_debugBannerState == 2)
            {
                IsUpdateAvailable = true;
                UpdateMessage = "Update available: 0.0.0-debug";
                _pendingNewVersion = "0.0.0-debug";
                _pendingExtractPath = Path.Combine(AppContext.BaseDirectory, "windsurf-update-ready-0.0.0-debug");
                IsWindsurfRunning = true;
                IsLauncherRestartUpdateAvailable = false;
                LauncherRestartUpdateMessage = "";
            }
            else if (_debugBannerState == 3)
            {
                IsUpdateAvailable = false;
                UpdateMessage = "";
                _pendingExtractPath = "";
                _pendingNewVersion = "";
                IsWindsurfRunning = false;
                IsLauncherRestartUpdateAvailable = true;
                LauncherRestartUpdateMessage = "Launcher update available: 0.0.0-debug — restart to update";
            }
            else
            {
                IsUpdateAvailable = true;
                UpdateMessage = "Update available: 0.0.0-debug";
                _pendingNewVersion = "0.0.0-debug";
                _pendingExtractPath = Path.Combine(AppContext.BaseDirectory, "windsurf-update-ready-0.0.0-debug");
                IsWindsurfRunning = false;
                IsLauncherRestartUpdateAvailable = true;
                LauncherRestartUpdateMessage = "Launcher update available: 0.0.0-debug — restart to update";
            }

            this.RaisePropertyChanged(nameof(CanApplyWindsurfUpdate));
            this.RaisePropertyChanged(nameof(IsUpdateAvailableAndWindsurfRunning));
            this.RaisePropertyChanged(nameof(IsUpdateOverlayVisible));
            this.RaisePropertyChanged(nameof(UpdateOverlayMessage));
            this.RaisePropertyChanged(nameof(ShowLauncherRestartButton));
            this.RaisePropertyChanged(nameof(ShowWindsurfUpdateButtons));
        });

        if (!IsWindsurfMissing)
        {
            InitializeUpdateChecker();
        }

        this.WhenAnyValue(x => x.IsWindsurfRunning)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanApplyWindsurfUpdate));
                this.RaisePropertyChanged(nameof(IsUpdateAvailableAndWindsurfRunning));
            });

        this.WhenAnyValue(x => x.IsUpdateAvailable)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanApplyWindsurfUpdate));
                this.RaisePropertyChanged(nameof(IsUpdateAvailableAndWindsurfRunning));
            });

        this.WhenAnyValue(x => x.IsUpdateAvailable, x => x.IsLauncherRestartUpdateAvailable)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsUpdateOverlayVisible));
                this.RaisePropertyChanged(nameof(UpdateOverlayMessage));
                this.RaisePropertyChanged(nameof(ShowLauncherRestartButton));
                this.RaisePropertyChanged(nameof(ShowWindsurfUpdateButtons));
            });

        this.WhenAnyValue(x => x.UpdateMessage, x => x.LauncherRestartUpdateMessage)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(UpdateOverlayMessage)));
    }

    public void StartBackgroundUpdateLoops()
    {
        StopBackgroundUpdateLoops();
        _updateLoopCts = new CancellationTokenSource();
        var token = _updateLoopCts.Token;

        _ = System.Threading.Tasks.Task.Run(() => LauncherUpdateLoopAsync(token));
        _ = System.Threading.Tasks.Task.Run(() => WindsurfUpdateLoopAsync(token));
    }

    public void StopBackgroundUpdateLoops()
    {
        try
        {
            _updateLoopCts?.Cancel();
            _updateLoopCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _updateLoopCts = null;
        }
    }

    private void LoadSettings()
    {
        string patchStateFile = Path.Combine(AppContext.BaseDirectory, "windsurf", "resources", ".portable_patch_state.json");
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
                    if (state.TryGetValue("auto_download_windsurf_updates", out var adwu)) _autoDownloadWindsurfUpdates = adwu == "true";

                    if (state.TryGetValue("launcher_shortcut_start_menu", out var lssm))
                        _enableLauncherStartMenuShortcut = lssm == "true";
                    else if (state.TryGetValue("launcher_shortcut_startup", out var legacyStartup))
                        _enableLauncherStartMenuShortcut = legacyStartup == "true";
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        string patchStateFile = Path.Combine(AppContext.BaseDirectory, "windsurf", "resources", ".portable_patch_state.json");
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
            state["launcher_shortcut_start_menu"] = EnableLauncherStartMenuShortcut ? "true" : "false";
            state["auto_download_windsurf_updates"] = AutoDownloadWindsurfUpdates ? "true" : "false";

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

    private void ApplyLauncherShortcuts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (EnableLauncherStartMenuShortcut)
                WindowsShortcutService.CreateOrUpdateStartMenuShortcut();
            else
                WindowsShortcutService.RemoveStartMenuShortcut();
        }
        catch
        {
        }
    }

    private void CreateDesktopShortcut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            WindowsShortcutService.CreateOrUpdateDesktopShortcut();
            StatusMessage = "Desktop shortcut created.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create desktop shortcut: {ex.Message}";
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
            IsLauncherRestartUpdateAvailable = true;
            LauncherRestartUpdateMessage = $"Launcher update available: {info.TargetFullRelease.Version} — restart to update";
        }
        catch (Exception ex)
        {
            LauncherUpdateMessage = $"Launcher update failed: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task RestartToApplyLauncherUpdateAsync()
    {
        var mgr = _launcherUpdateManager ?? CreateLauncherUpdateManager();
        if (mgr == null || !mgr.IsInstalled)
            return;

        var pending = mgr.UpdatePendingRestart;
        if (pending == null)
            return;

        mgr.ApplyUpdatesAndRestart(pending, Program.RestartArgs);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task LauncherUpdateLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var mgr = CreateLauncherUpdateManager();
                _launcherUpdateManager = mgr;
                if (mgr != null && mgr.IsInstalled)
                {
                    var pending = mgr.UpdatePendingRestart;
                    if (pending != null)
                    {
                        mgr.ApplyUpdatesAndRestart(pending, Program.RestartArgs);
                        return;
                    }

                    var info = await mgr.CheckForUpdatesAsync();
                    if (info != null)
                    {
                        _launcherUpdateInfo = info;
                        await mgr.DownloadUpdatesAsync(info);
                        IsLauncherRestartUpdateAvailable = true;
                        LauncherRestartUpdateMessage = $"Launcher update available: {info.TargetFullRelease.Version} — restart to update";
                    }
                }
            }
            catch
            {
            }

            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(15), token);
            }
            catch
            {
                return;
            }
        }
    }

    private async System.Threading.Tasks.Task WindsurfUpdateLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsWindsurfMissing && AutoDownloadWindsurfUpdates)
                {
                    if (_updateManager != null)
                        await _updateManager.CheckForUpdatesAsync(token);
                }
            }
            catch
            {
            }

            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromHours(1), token);
            }
            catch
            {
                return;
            }
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
        string windsurfDir = Path.Combine(baseDir, "windsurf");
        IsWindsurfMissing = !File.Exists(Path.Combine(windsurfDir, "Windsurf.exe")) &&
                            !File.Exists(Path.Combine(windsurfDir, "Windsurf - Next.exe"));
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
            var updateChannel = string.Equals(channel, "next", StringComparison.OrdinalIgnoreCase)
                ? WindsurfUpdateFeed.Channel.Next
                : WindsurfUpdateFeed.Channel.Stable;

            string apiUrl = WindsurfUpdateFeed.GetLatestUpdateApiUrl(updateChannel, WindsurfUpdateFeed.PackageKind.Portable);

            using var httpClient = new HttpClient();
            var response = await httpClient.GetFromJsonAsync(apiUrl, InternalUpdateResponseContext.Default.InternalUpdateResponse);

            if (response == null || string.IsNullOrEmpty(response.Url))
            {
                StatusMessage = "Failed to get download URL.";
                return;
            }

            StatusMessage = "Downloading Windsurf...";
            string tempPackagePath = Path.Combine(AppContext.BaseDirectory, GetInitialPackageFileName(response.Url));

            var downloadResponse = await httpClient.GetAsync(response.Url, HttpCompletionOption.ResponseHeadersRead);
            downloadResponse.EnsureSuccessStatusCode();

            using (var fs = new FileStream(tempPackagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await downloadResponse.Content.CopyToAsync(fs);
            }

            StatusMessage = "Extracting Windsurf...";
            await System.Threading.Tasks.Task.Run(() =>
            {
                var windsurfDir = Path.Combine(AppContext.BaseDirectory, "windsurf");
                Directory.CreateDirectory(windsurfDir);
                ExtractInitialPackage(tempPackagePath, windsurfDir);
                File.Delete(tempPackagePath);
            });

            StatusMessage = "Download complete!";
            IsWindsurfMissing = false;
            SaveInstalledWindsurfChannel(channel);
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

    private static string GetInitialPackageFileName(string downloadUrl)
    {
        try
        {
            var uri = new Uri(downloadUrl);
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        return "windsurf-initial.pkg";
    }

    private static void ExtractInitialPackage(string packagePath, string destinationDir)
    {
        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(packagePath, destinationDir, overwriteFiles: true);
            return;
        }

        if (packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || packagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(packagePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
            return;
        }

        throw new NotSupportedException($"Unsupported Windsurf package format: {packagePath}");
    }

    private void InitializeUpdateChecker()
    {
        string baseDir = AppContext.BaseDirectory;
        string windsurfDir = Path.Combine(baseDir, "windsurf");
        string appDir = Path.Combine(windsurfDir, "resources", "app");
        string patchStateFile = Path.Combine(windsurfDir, "resources", ".portable_patch_state.json");

        bool isNextBuild = IsNextBuildFromStateOrApp(appDir, patchStateFile);

        _updateManager = new UpdateManager(baseDir, appDir, isNextBuild, patchStateFile);
        _updateManager.UpdateAvailable += (newVersion, extractPath) =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _pendingNewVersion = newVersion;
                _pendingExtractPath = extractPath;
                UpdateMessage = $"Update available: {newVersion}";
                IsUpdateAvailable = true;
                this.RaisePropertyChanged(nameof(CanApplyWindsurfUpdate));
                this.RaisePropertyChanged(nameof(IsUpdateAvailableAndWindsurfRunning));
            });
        };

        if (AutoDownloadWindsurfUpdates)
            System.Threading.Tasks.Task.Run(() => _updateManager.CheckForUpdatesAsync());
    }

    private static bool IsNextBuildFromStateOrApp(string appDir, string patchStateFile)
    {
        try
        {
            if (File.Exists(patchStateFile))
            {
                var stateStr = File.ReadAllText(patchStateFile);
                var state = System.Text.Json.JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString);
                if (state != null && state.TryGetValue("windsurf_channel", out var channel) && !string.IsNullOrWhiteSpace(channel))
                {
                    return string.Equals(channel.Trim(), "next", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }

        try
        {
            string packageJsonPath = Path.Combine(appDir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJsonPath));
                if (doc.RootElement.TryGetProperty("version", out var versionElement))
                {
                    var version = versionElement.GetString() ?? "";
                    if (version.Contains("+next", StringComparison.OrdinalIgnoreCase) || version.Contains("-next", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (doc.RootElement.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString() ?? "";
                    if (name.Contains("next", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void SaveInstalledWindsurfChannel(string channel)
    {
        string patchStateFile = Path.Combine(AppContext.BaseDirectory, "windsurf", "resources", ".portable_patch_state.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(patchStateFile)!);
            System.Collections.Generic.Dictionary<string, string> state = new();
            if (File.Exists(patchStateFile))
            {
                var stateStr = File.ReadAllText(patchStateFile);
                state = System.Text.Json.JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString) ?? new();
            }

            state["windsurf_channel"] = channel.Trim().ToLowerInvariant();
            File.WriteAllText(patchStateFile, System.Text.Json.JsonSerializer.Serialize(state, JsonContext.Default.DictionaryStringString));
        }
        catch
        {
        }
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
