using System.Diagnostics;
using System.Security.Cryptography;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace WindsurfPortable
{
    /// <summary>
    /// WindsurfPortable.exe — drop-in portable launcher + auto-patcher for Windsurf (stable or Next).
    ///
    /// USAGE
    ///   Drop WindsurfPortable.exe into the extracted Windsurf zip folder, next to Windsurf.exe.
    ///   That's it. On first run it sets everything up automatically.
    ///
    ///   WindsurfPortable.exe                    → default profile
    ///   WindsurfPortable.exe work               → "work" profile
    ///   WindsurfPortable.exe work C:\myproject  → "work" profile, open folder
    ///   WindsurfPortable.exe --windsurf-dir "C:\Users\me\AppData\Local\Programs\Windsurf Next"
    ///
    /// WHAT IT DOES ON FIRST RUN
    ///   1. Creates profiles\<profile>\vscode-data\data\ and
    ///      profiles\<profile>\vscode-data\extensions\
    ///   2. Creates profiles\<profile>\windsurf-data\
    ///   3. Unpacks  resources\app.asar → resources\app\  (if app\ doesn't exist yet)
    ///   4. Patches the JS source to redirect ~/.codeium to a profile-local dir
    ///   5. Patches app-update.yml / update-related JS so the in-app updater relaunches
    ///      WindsurfPortable.exe instead of Windsurf.exe after a self-update
    ///   6. Launches Windsurf with explicit --user-data-dir / --extensions-dir per profile
    ///
    /// SUBSEQUENT RUNS
    ///   - If the app fingerprint changed (Windsurf self-updated), re-patches automatically
    ///   - Otherwise skips patching and launches immediately
    ///
    /// DATA LOCATIONS REDIRECTED
    ///   ~/.codeium/windsurf/    Codeium layer: MCP config, tokens, indexes, memories
    ///                           → profiles\<profile>\windsurf-data\codeium\ via JS patch + env
    ///   %APPDATA%\Windsurf\     Electron appData: machineid, globalStorage, settings
    ///                           → profiles\<profile>\windsurf-data\appdata\ via JS patch + APPDATA env override
    ///   %LOCALAPPDATA%\Windsurf\ Electron cache: GPU/V8/network caches
    ///                           → profiles\<profile>\windsurf-data\localappdata\ via LOCALAPPDATA env override
    ///   VS Code state/extensions
    ///                           → profiles\<profile>\vscode-data\data and
    ///                             profiles\<profile>\vscode-data\extensions via launch args
    ///
    /// SELF-UPDATE BEHAVIOUR
    ///   Windsurf's electron-updater downloads a new package, unpacks it over the current
    ///   directory, then relaunches.  We patch the relaunch target in the updater JS so it
    ///   calls WindsurfPortable.exe instead of Windsurf.exe, preserving the active profile
    ///   across updates without any user action.
    /// </summary>
    public class Launcher
    {
        // ── Paths (all relative to the directory containing this exe) ────────────

        const string VscodeDataDir = "vscode-data";
        const string VscodeUserDataSubdir = "data";
        const string VscodeExtensionsSubdir = "extensions";
        const string ProfilesDir = "profiles";
        const string WindsurfDataDir = "windsurf-data";
        const string ResourcesDir = "resources";
        const string AppAsarPath = "resources/app.asar";
        const string AppDirPath = "resources/app";
        const string PatchStatePath = "resources/.portable_patch_state.json";
        const string PatchStateVersion = "27";
        const string GlobalSignatureKey = "__global_signature";
        const string FileHashPrefix = "__filehash::";
        const string RuntimeRouterMarker = "__WINDSURF_PORTABLE_RUNTIME_ROUTER_V1__";

        static readonly string[] RuntimeRouterEntryFiles =
        [
            "out/main.js",
            "out/vs/code/electron-utility/sharedProcess/sharedProcessMain.js",
            "out/vs/workbench/api/node/extensionHostProcess.js",
            "out/vs/workbench/api/worker/extensionHostWorkerMain.js",
        ];

        public sealed record LauncherOptions(
            string BaseDir, 
            string Profile, 
            string[] ForwardArgs,
            bool EnableSingleInstancePatch = true,
            bool EnableMcpSyncIsolation = true,
            bool EnableGlobalRecentsPatch = true);

        sealed record PatchFamily(
            string Name,
            string StatusLabel,
            (string Pattern, string Replacement)[] Patches,
            string SearchKeyword,
            string[] DetectorLiterals,
            string[] IndicatorLiterals,
            string[] IndicatorRegexes,
            bool IsCritical,
            string MissingMessage,
            string SuccessLabel,
            string[] UnresolvedRegexes);

        sealed record PatchFamilyResult(
            PatchFamily Family,
            int CandidateFiles,
            int Patched,
            int AlreadyDone,
            int Scanned,
            int IndicatorFiles,
            int UnresolvedFiles,
            bool Missing);

        static readonly (string Pattern, string Replacement)[] McpSyncIsolationPatches =
        [
            (
                @"name:\s*""NotifyMcpStateChanged""",
                @"name:(process.env.WINDSURF_PROFILE_IPC_SUFFIX?""NotifyMcpStateChanged_""+process.env.WINDSURF_PROFILE_IPC_SUFFIX:""NotifyMcpStateChanged"")"
            ),
        ];

        static readonly (string Pattern, string Replacement)[] SecondInstancePatches =
        [
            (
                @"app\.on\((['""])second-instance\1\s*,",
                @"app.on((process.env.WINDSURF_DISABLE_SINGLE_INSTANCE===""1""?$1windsurfportable-second-instance-disabled$1:$1second-instance$1),"
            ),
        ];

        static readonly (string Pattern, string Replacement)[] InstallModifiedWarningPatches =
        [
            (
                @"Installation\s+has\s+been\s+modified\s+on\s+disk",
                @"Installation integrity warning suppressed by portable launcher"
            ),
            (
                @"Your\s+Windsurf(?:\s*-\s*Next)?\s+installation\s+appears\s+to\s+be\s+corrupt\.\s+Please\s+reinstall\.",
                @"Installation integrity warning suppressed by portable launcher"
            ),
            (
                @"C\?\.dontShowPrompt&&C\.commit===this\.f\.commit\|\|this\.n\(\)",
                @"C?.dontShowPrompt&&C.commit===this.f.commit||void 0"
            ),
            (
                @"\*\*\*\s*Installation\s+integrity\s+verification\s+failed\s*\*\*",
                @""
            ),
        ];

        static readonly (string Pattern, string Replacement)[] MutexPatches =
        [
            (
                @"app\.requestSingleInstanceLock\(\)",
                @"(process.env.WINDSURF_DISABLE_SINGLE_INSTANCE===""1""?true:app.requestSingleInstanceLock())"
            ),
            (
                @"app\.requestSingleInstanceLock\(([^\)]*)\)",
                @"(process.env.WINDSURF_DISABLE_SINGLE_INSTANCE===""1""?true:app.requestSingleInstanceLock($1))"
            ),
        ];

        static readonly (string Pattern, string Replacement)[] GlobalRecentsPatches =
        [
            (
                @"Hn\.addRecentDocument\(([^\)]*)\)",
                @"(process.env.WINDSURF_DISABLE_GLOBAL_RECENTS===""1""?void 0:Hn.addRecentDocument($1))"
            ),
        ];

        static PatchFamily[] BuildPatchFamilies(LauncherOptions options)
        {
            var families = new List<PatchFamily>();

            if (options.EnableSingleInstancePatch)
            {
                families.Add(new PatchFamily(
                    Name: "single-instance",
                    StatusLabel: "Patching single-instance lock...",
                    Patches: MutexPatches,
                    SearchKeyword: "single-instance",
                    DetectorLiterals: ["requestSingleInstanceLock"],
                    IndicatorLiterals: ["requestSingleInstanceLock"],
                    IndicatorRegexes: [],
                    IsCritical: false,
                    MissingMessage: "",
                    SuccessLabel: "Single-instance patches",
                    UnresolvedRegexes: []
                ));

                families.Add(new PatchFamily(
                    Name: "second-instance",
                    StatusLabel: "Patching second-instance routing...",
                    Patches: SecondInstancePatches,
                    SearchKeyword: "second-instance",
                    DetectorLiterals: ["second-instance"],
                    IndicatorLiterals: ["second-instance"],
                    IndicatorRegexes: [],
                    IsCritical: false,
                    MissingMessage: "",
                    SuccessLabel: "Second-instance patches",
                    UnresolvedRegexes: []
                ));
            }

            families.Add(new PatchFamily(
                Name: "install-modified-warning",
                StatusLabel: "Patching installation warnings...",
                Patches: InstallModifiedWarningPatches,
                SearchKeyword: "install-modified-warning",
                DetectorLiterals: ["Installation has been modified on disk", "Installation integrity", "appears to be corrupt", "Please reinstall", "dontShowPrompt", "checksumFailMoreInfoUrl"],
                IndicatorLiterals: ["Installation has been modified on disk", "Installation integrity", "appears to be corrupt", "Please reinstall"],
                IndicatorRegexes: [],
                IsCritical: false,
                MissingMessage: "",
                SuccessLabel: "Install-warning patches",
                UnresolvedRegexes: []
            ));

            if (options.EnableMcpSyncIsolation)
            {
                families.Add(new PatchFamily(
                    Name: "mcp-sync-isolation",
                    StatusLabel: "Patching MCP cross-instance sync...",
                    Patches: McpSyncIsolationPatches,
                    SearchKeyword: "mcp-sync-isolation",
                    DetectorLiterals: ["NotifyMcpStateChanged", "updateMcpServers"],
                    IndicatorLiterals: ["NotifyMcpStateChanged", "updateMcpServers"],
                    IndicatorRegexes:
                    [
                        @"name\s*:\s*""NotifyMcpStateChanged""",
                    ],
                    IsCritical: true,
                    MissingMessage: "No MCP sync-isolation patch sites matched, but MCP sync indicators still exist — patterns likely drifted in this Windsurf build.",
                    SuccessLabel: "MCP sync-isolation patches",
                    UnresolvedRegexes:
                    [
                        @"name\s*:\s*""NotifyMcpStateChanged""",
                    ]
                ));
            }

            if (options.EnableGlobalRecentsPatch)
            {
                families.Add(new PatchFamily(
                    Name: "global-recents",
                    StatusLabel: "Patching global recent-doc hooks...",
                    Patches: GlobalRecentsPatches,
                    SearchKeyword: "global-recents",
                    DetectorLiterals: ["addRecentDocument", "history.recentlyOpenedPathsList"],
                    IndicatorLiterals: ["addRecentDocument", "history.recentlyOpenedPathsList"],
                    IndicatorRegexes: [],
                    IsCritical: false,
                    MissingMessage: "",
                    SuccessLabel: "Global-recents patches",
                    UnresolvedRegexes: []
                ));
            }

            return families.ToArray();
        }

        public static int LegacyMain(string[] args)
        {
            var profileArgument = new Argument<string?>("profile", () => "default")
            {
                Description = "Profile name. Defaults to 'default'."
            };

            var windsurfDirOption = new Option<string?>("--windsurf-dir")
            {
                Description = "Path to Windsurf installation directory (directory containing Windsurf.exe)."
            };

            var rootCommand = new RootCommand("Windsurf portable launcher + patcher")
            {
                profileArgument,
                windsurfDirOption,
            };

            // Allow forwarding unknown args/options directly to Windsurf.
            rootCommand.TreatUnmatchedTokensAsErrors = false;

            ParseResult parseResult = rootCommand.Parse(args);

            // Let System.CommandLine render generated help text.
            if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
                return rootCommand.Invoke(args);

            string profile = (parseResult.GetValueForArgument(profileArgument) ?? "default").Trim();
            string? windsurfDir = parseResult.GetValueForOption(windsurfDirOption);
            string[] forwardArgs = parseResult.UnmatchedTokens.ToArray();

            var options = new LauncherOptions(
                string.IsNullOrWhiteSpace(windsurfDir)
                    ? AppContext.BaseDirectory
                    : windsurfDir.Trim(),
                profile,
                forwardArgs
            );

            return Run(options);
        }

        public static int Run(LauncherOptions options)
        {
            var proc = Launch(options, waitForExit: true);
            return proc?.ExitCode ?? 1;
        }

        public static Process? Launch(LauncherOptions options, bool waitForExit)
        {
            string baseDir = options.BaseDir;
            baseDir = Path.GetFullPath(baseDir);
            string profile = options.Profile;
            string profileIpcSuffix = ToIpcSafeProfileId(profile);

            // Detect which exe Windsurf itself is (supports both stable and Next)
            string? windsurfExe = FindWindsurfExe(baseDir);
            if (windsurfExe is null)
            {
                Error("Could not find Windsurf.exe or \"Windsurf - Next.exe\" next to this launcher.");
                Error("Drop WindsurfPortable.exe into the extracted Windsurf zip folder.");
                return null;
            }

            AnsiConsole.Write(new Rule("WindsurfPortable").LeftJustified().RuleStyle("grey"));
            Info($"Windsurf : {Path.GetFileName(windsurfExe)}");
            Info($"Profile  : {profile}");
            Info($"Base dir : {baseDir}");

            // ── First-run setup ───────────────────────────────────────────────────

            // 1. Create profile-specific dirs
            string profileRoot = EnsureDir(Path.Combine(baseDir, ProfilesDir, profile));
            string vscodeDataRoot = EnsureDir(Path.Combine(profileRoot, VscodeDataDir));
            string profileUserDataDir = EnsureDir(Path.Combine(vscodeDataRoot, VscodeUserDataSubdir));
            string profileExtensionsDir = EnsureDir(Path.Combine(vscodeDataRoot, VscodeExtensionsSubdir));

            // 2. Windsurf-specific redirected data dirs
            string windsurfDataRoot = EnsureDir(Path.Combine(profileRoot, WindsurfDataDir));
            string userProfileDir = EnsureDir(Path.Combine(windsurfDataRoot, "userprofile"));
            string codeiumRootDir = EnsureDir(Path.Combine(userProfileDir, ".codeium"));
            string codeiumDatabaseDir = EnsureDir(Path.Combine(codeiumRootDir, "database"));
            string appDataDir = EnsureDir(Path.Combine(windsurfDataRoot, "appdata"));
            _ = EnsureDir(Path.Combine(appDataDir, "Windsurf"));
            _ = EnsureDir(Path.Combine(appDataDir, "Windsurf - Next"));
            string localDataDir = EnsureDir(Path.Combine(windsurfDataRoot, "localappdata"));
            string globalUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string globalAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string globalLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _ = EnsureDir(Path.Combine(userProfileDir, ".windsurf"));
            _ = EnsureDir(Path.Combine(userProfileDir, ".windsurf-next"));
            bool isNextBuild = Path.GetFileName(windsurfExe).Contains("Next", StringComparison.OrdinalIgnoreCase);
            string codeiumBaseSegment = isNextBuild ? "windsurf-next" : "windsurf";
            string codeiumSegments = $".codeium/{codeiumBaseSegment}";

            // 3. Unpack app.asar if resources\app\ doesn't exist yet
            string appDir = Path.Combine(baseDir, AppDirPath);
            string asarFile = Path.Combine(baseDir, AppAsarPath);
            bool needUnpack = !Directory.Exists(appDir) || !Directory.EnumerateFiles(appDir).Any();

            if (needUnpack)
            {
                if (!File.Exists(asarFile))
                {
                    Error($"Neither resources/app/ nor resources/app.asar found in: {baseDir}");
                    return null;
                }
                Info("Unpacking app.asar (first run)...");
                if (!UnpackAsar(asarFile, appDir))
                    return null;
            }


            // ── Patch detection & application ─────────────────────────────────────
            string patchStateFile = Path.Combine(baseDir, PatchStatePath);
            var patchState = LoadPatchState(patchStateFile);
            bool hasStoredPatchVersion = patchState.TryGetValue("__patch_version", out var stateVersion);
            bool patchVersionChanged = hasStoredPatchVersion &&
                                       !string.Equals(stateVersion, PatchStateVersion, StringComparison.Ordinal);
            if (patchVersionChanged)
            {
                Info($"Patch version changed ({stateVersion} -> {PatchStateVersion}) — restoring .orig backups before reapplying.");
                int restoredBackups = RestoreOrigBackups(appDir);
                Info($"Restored {restoredBackups} .orig backup file(s).");
            }

            if (!hasStoredPatchVersion || patchVersionChanged)
            {
                patchState.Clear();
                patchState["__patch_version"] = PatchStateVersion;
            }

            string currentGlobalSignature = ComputeAppGlobalSignature(appDir);
            patchState.TryGetValue(GlobalSignatureKey, out string? savedGlobalSignature);

            if (string.Equals(savedGlobalSignature, currentGlobalSignature, StringComparison.Ordinal))
            {
                Info("App signature unchanged — skipping patch phase.");
            }
            else
            {
                Info("App signature changed — scanning JS files for changed patch candidates...");
                var changedJsFiles = GetChangedJsFiles(appDir, patchState);
                Info($"Changed JS files: {changedJsFiles.Count}");

                var patchFamilies = BuildPatchFamilies(options);
                var patchResults = new List<PatchFamilyResult>();
                bool missingRequiredIsolationPatches = false;
                int routerBootstrapPatched = 0;
                int routerBootstrapDone = 0;
                int routerBootstrapPresentEntries = 0;
                int routerBootstrapMissingEntries = 0;

                if (changedJsFiles.Count > 0)
                {
                    (routerBootstrapPatched, routerBootstrapDone, routerBootstrapPresentEntries, routerBootstrapMissingEntries) =
                        InjectRuntimeRouterBootstrap(appDir, changedJsFiles);

                    AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .Start("Patching application scripts...", ctx =>
                        {
                            foreach (var family in patchFamilies)
                            {
                                ctx.Status(family.StatusLabel);
                                var candidateFiles = DiscoverCandidateJsFiles(changedJsFiles, family.DetectorLiterals);
                                var (patched, alreadyDone, scanned) = ApplyPatchesAdaptive(
                                    appDir,
                                    candidateFiles,
                                    family.Patches,
                                    family.SearchKeyword,
                                    null);

                                patchResults.Add(new PatchFamilyResult(
                                    Family: family,
                                    CandidateFiles: candidateFiles.Count,
                                    Patched: patched,
                                    AlreadyDone: alreadyDone,
                                    Scanned: scanned,
                                    IndicatorFiles: 0,
                                    UnresolvedFiles: 0,
                                    Missing: false));
                            }
                        });

                    SaveJsFileHashes(appDir, changedJsFiles, patchState);
                }
                else
                {
                    Info("No JS content changes detected — skipping patch rule passes.");
                }

                if (changedJsFiles.Count > 0)
                {
                    Info($"Runtime-router bootstrap: {routerBootstrapPatched} injected, {routerBootstrapDone} already present (entry files found: {routerBootstrapPresentEntries}, missing: {routerBootstrapMissingEntries}).");
                    if (routerBootstrapPresentEntries == 0 || routerBootstrapMissingEntries > 0)
                    {
                        Warn("Runtime-router bootstrap markers are missing from one or more required entry bundles.");
                        missingRequiredIsolationPatches = true;
                    }

                    for (int i = 0; i < patchResults.Count; i++)
                    {
                        var result = patchResults[i];
                        int indicatorFiles = result.Family.IndicatorRegexes.Length > 0
                            ? CountJsFilesMatchingAnyRegex(appDir, result.Family.IndicatorRegexes)
                            : (result.Family.IndicatorLiterals.Length == 0
                                ? 0
                                : CountJsFilesContainingAnyLiteral(appDir, result.Family.IndicatorLiterals));
                        int unresolvedFiles = result.Family.IsCritical && result.Family.UnresolvedRegexes.Length > 0
                            ? CountJsFilesMatchingAnyRegex(appDir, result.Family.UnresolvedRegexes)
                            : 0;
                        bool indicatorOnlyDrift = result.Family.IsCritical &&
                                                  result.Patched == 0 &&
                                                  result.AlreadyDone == 0 &&
                                                  indicatorFiles > 0;
                        bool missing = result.Family.IsCritical && unresolvedFiles > 0;

                        patchResults[i] = result with
                        {
                            IndicatorFiles = indicatorFiles,
                            UnresolvedFiles = unresolvedFiles,
                            Missing = missing,
                        };

                        if (result.Patched == 0 && result.AlreadyDone == 0)
                        {
                            if (result.Family.IsCritical)
                            {
                                if (indicatorOnlyDrift)
                                    Warn($"{result.Family.MissingMessage} Indicator files: {indicatorFiles}.");
                                else
                                    Warn($"No critical {result.Family.Name} patch sites found in this build.");
                            }
                            else
                            {
                                Info($"No {result.Family.Name} patch sites found in this build — skipping optional rewrite.");
                            }
                        }
                        else
                        {
                            Info($"{result.Family.SuccessLabel}: {result.Patched} new, {result.AlreadyDone} already done (candidates {result.CandidateFiles}, checked {result.Scanned} changed file(s)).");
                        }

                        if (unresolvedFiles > 0)
                            Warn($"{result.Family.Name}: {unresolvedFiles} JS file(s) still match unresolved critical patterns after patching.");

                        if (missing)
                            missingRequiredIsolationPatches = true;
                    }

                    int totalPatched = patchResults.Sum(r => r.Patched);
                    int totalAlreadyDone = patchResults.Sum(r => r.AlreadyDone);
                    int totalCandidates = patchResults.Sum(r => r.CandidateFiles);
                    int criticalUnresolved = patchResults.Where(r => r.Family.IsCritical).Sum(r => r.UnresolvedFiles);
                    Info($"Patch audit summary: families={patchResults.Count}, candidates={totalCandidates}, patched={totalPatched}, already-done={totalAlreadyDone}, critical-unresolved={criticalUnresolved}.");

                    int runtimeBootstrapFilesPresent = RuntimeRouterEntryFiles.Count(rel => File.Exists(Path.Combine(appDir, rel.Replace('/', Path.DirectorySeparatorChar))));
                    int runtimeBootstrapMissingMarkers = CountRuntimeRouterMarkerMissing(appDir);
                    if (runtimeBootstrapFilesPresent == 0 || runtimeBootstrapMissingMarkers > 0)
                    {
                        Warn($"Runtime-router marker verification failed: entry-files-present={runtimeBootstrapFilesPresent}, missing-markers={runtimeBootstrapMissingMarkers}.");
                        missingRequiredIsolationPatches = true;
                    }

                    if (missingRequiredIsolationPatches)
                    {
                        Error("Required profile-isolation patches are missing in this Windsurf build. Launch aborted to prevent cross-profile state sharing.");
                        Error("Please update patch patterns for this client version and rerun.");
                        return null;
                    }
                }

                patchState[GlobalSignatureKey] = currentGlobalSignature;
                SavePatchState(patchStateFile, patchState);
            }

            // ── Background Auto-Update Check ───────────────────────────────────────
            var updateManager = new UpdateManager(baseDir, appDir, isNextBuild, patchStateFile);
            _ = Task.Run(() => updateManager.CheckForUpdatesAsync());

            // ── Launch ────────────────────────────────────────────────────────────

            var psi = new ProcessStartInfo
            {
                FileName = windsurfExe,
                UseShellExecute = false,
            };

            psi.Environment["WINDSURF_CODEIUM_HOME"] = userProfileDir;
            psi.Environment["WINDSURF_PROFILE_HOME"] = userProfileDir;
            psi.Environment["WINDSURF_APPDATA"] = appDataDir;
            psi.Environment["WINDSURF_APPDATA_DIR"] = "Windsurf";
            psi.Environment["WINDSURF_APPDATA_DIR_NEXT"] = "Windsurf - Next";
            psi.Environment["WINDSURF_WINDSURF_DIR"] = ".windsurf";
            psi.Environment["WINDSURF_WINDSURF_NEXT_DIR"] = ".windsurf-next";
            psi.Environment["WINDSURF_WINDSURF_INSIDERS_DIR"] = ".windsurf-insiders";
            psi.Environment["WINDSURF_CODEIUM_SEGMENTS"] = codeiumSegments;
            psi.Environment["WINDSURF_CODEIUM_DATABASE_DIR"] = codeiumDatabaseDir;
            if (options.EnableSingleInstancePatch) psi.Environment["WINDSURF_DISABLE_SINGLE_INSTANCE"] = "1";
            psi.Environment["WINDSURF_PROFILE_IPC_SUFFIX"] = profileIpcSuffix;
            if (options.EnableGlobalRecentsPatch) psi.Environment["WINDSURF_DISABLE_GLOBAL_RECENTS"] = "1";
            psi.Environment["WINDSURF_GLOBAL_USERPROFILE"] = globalUserProfile;
            psi.Environment["WINDSURF_GLOBAL_APPDATA"] = globalAppData;
            psi.Environment["WINDSURF_GLOBAL_LOCALAPPDATA"] = globalLocalAppData;
            psi.Environment["APPDATA"] = appDataDir;
            psi.Environment["LOCALAPPDATA"] = localDataDir;

            psi.ArgumentList.Add("--user-data-dir");
            psi.ArgumentList.Add(profileUserDataDir);
            psi.ArgumentList.Add("--extensions-dir");
            psi.ArgumentList.Add(profileExtensionsDir);

            // Forward remaining args (after profile + launcher-specific args removed)
            foreach (var arg in options.ForwardArgs)
                psi.ArgumentList.Add(arg);

            Info($"WINDSURF_CODEIUM_HOME = {userProfileDir}");
            Info($"WINDSURF_PROFILE_HOME = {userProfileDir}");
            Info($"APPDATA               = {appDataDir}");
            Info($"LOCALAPPDATA          = {localDataDir}");
            Info($"--user-data-dir       = {profileUserDataDir}");
            Info($"--extensions-dir      = {profileExtensionsDir}");
            Info("Launching...");

            var proc = Process.Start(psi);
            if (proc == null)
                return null;

            if (waitForExit)
            {
                proc.WaitForExit();
            }

            return proc;
        }

        // ── ASAR unpacking ────────────────────────────────────────────────────────

        static bool UnpackAsar(string asarPath, string outDir)
        {
            // Try npx @electron/asar (requires Node.js on PATH)
            // Fall back to a note telling the user to do it manually.
            try
            {
                EnsureDir(outDir);
                var psi = new ProcessStartInfo
                {
                    FileName = "npx",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--yes");
                psi.ArgumentList.Add("@electron/asar");
                psi.ArgumentList.Add("extract");
                psi.ArgumentList.Add(asarPath);
                psi.ArgumentList.Add(outDir);

                var proc = Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0 || !Directory.EnumerateFiles(outDir).Any())
                {
                    Warn("npx @electron/asar failed. Trying local asar binary...");
                    return UnpackAsarFallback(asarPath, outDir);
                }

                Console.WriteLine("[WindsurfPortable] app.asar unpacked successfully.");
                return true;
            }
            catch
            {
                return UnpackAsarFallback(asarPath, outDir);
            }
        }

        static bool UnpackAsarFallback(string asarPath, string outDir)
        {
            // Try to find asar.cmd in common node_modules locations
            var candidates = new[]
            {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "asar.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules", "@electron", "asar", "bin", "asar.js"),
        };

            foreach (var c in candidates.Where(File.Exists))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = c.EndsWith(".cmd") ? c : "node",
                    UseShellExecute = false,
                };
                if (!c.EndsWith(".cmd")) psi.ArgumentList.Add(c);
                psi.ArgumentList.Add("extract");
                psi.ArgumentList.Add(asarPath);
                psi.ArgumentList.Add(outDir);

                var proc = Process.Start(psi)!;
                proc.WaitForExit();
                if (proc.ExitCode == 0) return true;
            }

            Error("Could not unpack app.asar automatically. Please run:");
            Error($"  npx @electron/asar extract \"{asarPath}\" \"{outDir}\"");
            Error("Then re-run WindsurfPortable.exe.");
            return false;
        }

        // ── app-update.yml patch ──────────────────────────────────────────────────

        static void PatchAppUpdateYml(string baseDir, string launcherExe)
        {
            // electron-updater reads app-update.yml to know the exe name it should relaunch.
            // We rewrite the executablePath / releaseType fields to point at our launcher.
            var ymlPath = Path.Combine(baseDir, ResourcesDir, "app-update.yml");
            if (!File.Exists(ymlPath)) return;

            string content = File.ReadAllText(ymlPath);
            string original = content;

            // The field looks like:   executablePath: Windsurf.exe
            // or:                     executablePath: Windsurf - Next.exe
            string launcherName = Path.GetFileName(launcherExe);
            content = Regex.Replace(
                content,
                @"(executablePath\s*:\s*)(Windsurf(?:\s*-\s*Next)?\.exe)",
                $"$1{launcherName}"
            );

            if (content == original) return;

            string backup = ymlPath + ".orig";
            if (!File.Exists(backup)) File.Copy(ymlPath, backup);
            File.WriteAllText(ymlPath, content);
            Console.WriteLine($"  Patched: {Path.GetRelativePath(baseDir, ymlPath)}");
        }

        // ── JS patching ───────────────────────────────────────────────────────────

        static (int patched, int alreadyDone, int scanned) ApplyPatches(
            string appDir,
            IReadOnlyList<string> jsFiles,
            (string Pattern, string Replacement)[] patches,
            string searchKeyword,
            IReadOnlyList<string>? hintLiterals = null)
        {
            int patched = 0, alreadyDone = 0, scanned = 0;

            // Build a quick pre-filter string from the first pattern's literal prefix
            // (everything before the first regex metacharacter)
            string preFilter = ExtractLiteralPrefix(patches[0].Pattern);

            foreach (var file in jsFiles)
            {
                string original = File.ReadAllText(file, Encoding.UTF8);

                if (hintLiterals is { Count: > 0 } &&
                    !hintLiterals.Any(h => !string.IsNullOrEmpty(h) && original.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Quick pre-filter
                if (!string.IsNullOrEmpty(preFilter) &&
                    !original.Contains(preFilter, StringComparison.Ordinal))
                    continue;

                scanned++;

                bool hadMarker = patches.Any(p =>
                    original.Contains(p.Replacement.Split('(')[0], StringComparison.Ordinal));

                string modified = original;
                foreach (var (pattern, replacement) in patches)
                    modified = Regex.Replace(modified, pattern, replacement);

                if (modified == original)
                {
                    if (hadMarker) alreadyDone++;
                    continue;
                }

                string backup = file + ".orig";
                if (!File.Exists(backup)) File.Copy(file, backup);
                File.WriteAllText(file, modified, Encoding.UTF8);
                patched++;
            }

            return (patched, alreadyDone, scanned);
        }

        static (int patched, int alreadyDone, int scanned) ApplyPatchesAdaptive(
            string appDir,
            IReadOnlyList<string> jsFiles,
            (string Pattern, string Replacement)[] patches,
            string searchKeyword,
            IReadOnlyList<string>? hintLiterals = null)
        {
            var result = ApplyPatches(appDir, jsFiles, patches, searchKeyword, hintLiterals);
            if (result.scanned > 0 || hintLiterals is not { Count: > 0 })
                return result;

            // Hint-gating is an optimization; if a minor upstream update renamed literals,
            // retry once without hints so pattern-based matching can still succeed.
            return ApplyPatches(appDir, jsFiles, patches, searchKeyword, null);
        }

        static (int patched, int alreadyDone, int presentEntries, int missingEntries) InjectRuntimeRouterBootstrap(string appDir, List<string> changedJsFiles)
        {
            string bootstrapPath = Path.Combine(appDir, "windsurf-portable-router.cjs");
            File.WriteAllText(bootstrapPath, BuildRuntimeRouterBootstrap(), Encoding.UTF8);

            int patched = 0;
            int alreadyDone = 0;
            int presentEntries = 0;
            int missingEntries = 0;

            foreach (var relative in RuntimeRouterEntryFiles)
            {
                string file = Path.Combine(appDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    missingEntries++;
                    continue;
                }

                presentEntries++;
                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    missingEntries++;
                    continue;
                }

                // Relative path from `appDir/relative` to `appDir/windsurf-portable-router.cjs`
                // E.g. "out/main.js" -> depth=1 -> "../windsurf-portable-router.cjs"
                // "out/vs/workbench/api/node/extensionHostProcess.js" -> depth=5 -> "../../../../../windsurf-portable-router.cjs"
                int depth = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Length - 1;
                string relPath = depth == 0 ? "./windsurf-portable-router.cjs" : string.Join("", Enumerable.Repeat("../", depth)) + "windsurf-portable-router.cjs";
                
                string injectStmt = $"import \"{relPath}\";\n";

                if (content.Contains("windsurf-portable-router.cjs", StringComparison.Ordinal))
                {
                    alreadyDone++;
                }
                else
                {
                    string backup = file + ".orig";
                    if (!File.Exists(backup)) File.Copy(file, backup);

                    File.WriteAllText(file, injectStmt + content, Encoding.UTF8);
                    patched++;
                }

                if (!changedJsFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                    changedJsFiles.Add(file);
            }

            return (patched, alreadyDone, presentEntries, missingEntries);
        }

        static int CountRuntimeRouterMarkerMissing(string appDir)
        {
            int missing = 0;
            foreach (var relative in RuntimeRouterEntryFiles)
            {
                string file = Path.Combine(appDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    missing++;
                    continue;
                }

                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    missing++;
                    continue;
                }

                if (!content.Contains("windsurf-portable-router.cjs", StringComparison.Ordinal))
                    missing++;
            }

            return missing;
        }

        static string BuildRuntimeRouterBootstrap()
        {
            const string template = """
try {
if(!globalThis.__RUNTIME_ROUTER_MARKER__){
  globalThis.__RUNTIME_ROUTER_MARKER__=1;

  let _debugLog = null;
  try {
    const fs=require("fs");
    const path=require("path");
    if(process.env.WINDSURF_PATCH_DEBUG==="1"){
      const logFile = path.join(process.env.WINDSURF_PROFILE_HOME || process.env.USERPROFILE || "", "windsurf_portable_router.log");
      _debugLog = (msg) => { try { fs.appendFileSync(logFile, `[${process.pid}] ${msg}\n`); }catch{} };
      _debugLog("Router bootstrap init start");
    }
  }catch{}
  const debug = (msg) => { if(_debugLog) _debugLog(msg); };

  const fs=require("fs");
  const os=require("os");
  const path=require("path");
  const cp=require("child_process");
  const url=require("url");

  debug("Modules loaded");

  if(process.env.WINDSURF_PROFILE_HOME){
    process.env.USERPROFILE=process.env.WINDSURF_PROFILE_HOME;
    process.env.HOME=process.env.WINDSURF_PROFILE_HOME;
  }

  const origHomedir=os.homedir&&os.homedir.bind(os);
  const norm=(p)=>typeof p==="string"&&p?p.replace(/\//g,"\\").toLowerCase():"";
  
  const route=(p)=>{
    if(typeof p!=="string"||!p) return p;
    const win=p.replace(/\//g,"\\");
    const low=win.toLowerCase();
    const profileHome=process.env.WINDSURF_PROFILE_HOME||"";
    const profileAppData=process.env.WINDSURF_APPDATA||"";
    const globalHome=(process.env.WINDSURF_GLOBAL_USERPROFILE||process.env.USERPROFILE||(origHomedir?origHomedir():""));
    const globalAppData=(process.env.WINDSURF_GLOBAL_APPDATA||path.join(globalHome,"AppData","Roaming"));

    const pairs=[
      [path.join(globalHome,".codeium"),path.join(profileHome,".codeium")],
      [path.join(globalHome,".windsurf-next"),path.join(profileHome,".windsurf-next")],
      [path.join(globalHome,".windsurf"),path.join(profileHome,".windsurf")],
      [path.join(globalAppData,"Windsurf - Next"),path.join(profileAppData,"Windsurf - Next")],
      [path.join(globalAppData,"Windsurf"),path.join(profileAppData,"Windsurf")]
    ];

    for(const [rawSrc,dst] of pairs){
      const src=norm(rawSrc);
      if(!src||!dst) continue;
      if(low===src||low.startsWith(src+"\\")) return dst+win.slice(src.length);
    }
    return p;
  };

  const routePathLike=(v)=>{
    if(typeof v==="string") return route(v);
    if(v&&typeof v==="object"){
      try{
        if(v.protocol==="file:"&&url&&typeof url.fileURLToPath==="function") return route(url.fileURLToPath(v));
      }catch{}
    }
    return v;
  };

  const wrap=(obj,name,idxs)=>{
    const f=obj&&obj[name];
    if(typeof f!=="function") return;
    const wrapped=function(...args){
      for(const i of idxs){
        if(i<args.length) args[i]=routePathLike(args[i]);
      }
      return f.apply(this,args);
    };
    if(name==="realpath"&&typeof f.native==="function"){
      wrapped.native=function(...args){
        if(args.length>0) args[0]=routePathLike(args[0]);
        return f.native.apply(this,args);
      };
    }
    obj[name]=wrapped;
  };

  if(origHomedir){
    os.homedir=()=>process.env.WINDSURF_PROFILE_HOME||origHomedir();
  }
  const origUserInfo=os.userInfo&&os.userInfo.bind(os);
  if(origUserInfo){
    os.userInfo=(...args)=>{
      const info=origUserInfo(...args);
      const home=process.env.WINDSURF_PROFILE_HOME||"";
      if(info&&typeof info==="object"&&home) info.homedir=home;
      return info;
    };
  }

  debug("OS homedir/userInfo patched");

  for(const n of ["readFile","writeFile","appendFile","open","mkdir","stat","lstat","readdir","realpath","unlink","rm","rmdir","opendir","createReadStream","createWriteStream","readFileSync","writeFileSync","appendFileSync","openSync","mkdirSync","statSync","lstatSync","readdirSync","realpathSync","unlinkSync","rmSync","rmdirSync","opendirSync","existsSync"]) wrap(fs,n,[0]);
  for(const n of ["rename","copyFile","link","symlink","renameSync","copyFileSync","linkSync","symlinkSync"]) wrap(fs,n,[0,1]);

  if(fs.promises){
    for(const n of ["readFile","writeFile","appendFile","open","mkdir","stat","lstat","readdir","realpath","unlink","rm","rmdir","opendir"]) wrap(fs.promises,n,[0]);
    for(const n of ["rename","copyFile","link","symlink"]) wrap(fs.promises,n,[0,1]);
  }

  debug("FS patched");

  const patchArgs=(x)=>Array.isArray(x)?x.map(a=>routePathLike(a)):x;
  const patchOpts=(c, o)=>{
    if(!o||typeof o!=="object") return o;
    const n={...o};
    if(typeof n.cwd==="string") n.cwd=route(n.cwd);
    
    // Restore global USERPROFILE/HOME for typical spawned shells/tools so user environments aren't broken.
    // We keep the isolated one for the Codeium language server and Windsurf's internal Node processes.
    const isLangServer = typeof c === "string" && (c.includes("language_server") || c.includes("mcp"));
    if (!isLangServer && (process.env.WINDSURF_GLOBAL_USERPROFILE || process.env.WINDSURF_GLOBAL_HOME)) {
      n.env = { ...process.env, ...n.env };
      if (process.env.WINDSURF_GLOBAL_USERPROFILE) n.env.USERPROFILE = process.env.WINDSURF_GLOBAL_USERPROFILE;
      if (process.env.WINDSURF_GLOBAL_HOME) n.env.HOME = process.env.WINDSURF_GLOBAL_HOME;
    }
    return n;
  };

  const wrapSpawnLike=(name)=>{
    const f=cp&&cp[name];
    if(typeof f!=="function") return;
    cp[name]=function(command,args,options){
      const c=routePathLike(command);
      debug("Spawned [" + name + "]: " + c);
      let a=args;
      let o=options;
      if(Array.isArray(args)){
        a=patchArgs(args);
        o=patchOpts(c, options);
      }else{
        o=patchOpts(c, args);
      }
      return f.call(this,c,a,o);
    };
  };

  wrapSpawnLike("spawn");
  wrapSpawnLike("spawnSync");
  wrapSpawnLike("execFile");
  wrapSpawnLike("execFileSync");

  debug("Spawn patched");

  const fork=cp&&cp.fork;
  if(typeof fork==="function"){
    cp.fork=function(modulePath,args,options){
      const m=typeof modulePath==="string"?route(modulePath):modulePath;
      debug("Forked: " + m);
      const a=patchArgs(args);
      const o=patchOpts(m, options);
      return fork.call(this,m,a,o);
    };
  }

  debug("Fork patched");

  try{
    const electron=require("electron");
    const app=electron&&electron.app;
    if(app&&typeof app.getPath==="function"){
      const origGetPath=app.getPath.bind(app);
      app.getPath=(name)=>route(origGetPath(name));
    }
    if(app&&process.env.WINDSURF_DISABLE_SINGLE_INSTANCE==="1"){
      if(typeof app.requestSingleInstanceLock==="function") app.requestSingleInstanceLock=()=>true;
      if(typeof app.acquireSingleInstanceLock==="function") app.acquireSingleInstanceLock=()=>true;
    }
  }catch(e){
    debug("Electron patch error: " + (e&&e.message));
  }
  debug("Router bootstrap init complete");
}
} catch(e) {
  try {
    const fs=require("fs");
    const path=require("path");
    const logFile = path.join(process.env.WINDSURF_PROFILE_HOME || process.env.USERPROFILE || "", "windsurf_portable_router_error.log");
    fs.appendFileSync(logFile, `[${process.pid}] Fatal bootstrap error: ${e&&e.stack}\n`);
  }catch{}
}
""";
            return template;
        }

        static List<string> DiscoverCandidateJsFiles(IReadOnlyList<string> jsFiles, IReadOnlyList<string> detectorLiterals)
        {
            if (detectorLiterals.Count == 0)
                return jsFiles.ToList();

            var candidates = new List<string>(jsFiles.Count);
            foreach (var file in jsFiles)
            {
                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (detectorLiterals.Any(l => !string.IsNullOrWhiteSpace(l) && content.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(file);
            }

            return candidates;
        }

        static int CountJsFilesContainingAnyLiteral(string appDir, IReadOnlyList<string> literals)
        {
            if (literals.Count == 0)
                return 0;

            int count = 0;
            foreach (var file in Directory.EnumerateFiles(appDir, "*.js", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
                                  && !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase)))
            {
                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (literals.Any(l => !string.IsNullOrWhiteSpace(l) && content.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    count++;
            }

            return count;
        }

        static int CountJsFilesMatchingAnyRegex(string appDir, IReadOnlyList<string> regexPatterns)
        {
            if (regexPatterns.Count == 0)
                return 0;

            int count = 0;
            foreach (var file in Directory.EnumerateFiles(appDir, "*.js", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
                                  && !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase)))
            {
                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (regexPatterns.Any(pattern => Regex.IsMatch(content, pattern)))
                    count++;
            }

            return count;
        }

        static string ExtractLiteralPrefix(string pattern)
        {
            var sb = new StringBuilder();
            foreach (char c in pattern)
            {
                if ("\\^$.|?*+()[]{}".Contains(c)) break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static List<string> GetChangedJsFiles(string appDir, Dictionary<string, string> patchState)
        {
            var changed = new List<string>();

            foreach (var file in Directory.EnumerateFiles(appDir, "*.js", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
                                  && !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase)))
            {
                string content = File.ReadAllText(file, Encoding.UTF8);
                string hash = HashString(content);
                string relativePath = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                string key = FileHashPrefix + relativePath;

                if (!patchState.TryGetValue(key, out var savedHash))
                    patchState.TryGetValue(relativePath, out savedHash);

                if (!string.Equals(savedHash, hash, StringComparison.Ordinal))
                {
                    changed.Add(file);
                }
            }

            return changed;
        }

        static void SaveJsFileHashes(string appDir, IEnumerable<string> files, Dictionary<string, string> patchState)
        {
            foreach (var file in files)
            {
                if (!File.Exists(file))
                    continue;

                string content = File.ReadAllText(file, Encoding.UTF8);
                string hash = HashString(content);
                string relativePath = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                patchState[FileHashPrefix + relativePath] = hash;
            }
        }

        static int RestoreOrigBackups(string appDir)
        {
            int restored = 0;

            foreach (var backup in Directory.EnumerateFiles(appDir, "*.orig", SearchOption.AllDirectories))
            {
                string target = backup[..^".orig".Length];
                try
                {
                    EnsureDir(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, overwrite: true);
                    File.Delete(backup);
                    restored++;
                }
                catch (Exception ex)
                {
                    string relative = Path.GetRelativePath(appDir, backup).Replace('\\', '/');
                    Warn($"Failed to restore backup '{relative}': {ex.Message}");
                }
            }

            return restored;
        }

        // ── Patch state / hashing ────────────────────────────────────────────────

        static Dictionary<string, string> LoadPatchState(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        static void SavePatchState(string path, Dictionary<string, string> patchState)
        {
            try
            {
                EnsureDir(Path.GetDirectoryName(path)!);
                string json = JsonSerializer.Serialize(patchState, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Warn($"Could not save patch state: {ex.Message}");
            }
        }

        static string HashString(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))[..16];
        }

        static string ComputeAppGlobalSignature(string appDir)
        {
            var sb = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(appDir, "*.js", SearchOption.AllDirectories)
                         .Where(f => !f.EndsWith(".orig", StringComparison.OrdinalIgnoreCase)
                                  && !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                string relative = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                sb.Append(relative).Append('|')
                  .Append(info.Length).Append('|')
                  .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }

            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        static string ToIpcSafeProfileId(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return "default";

            string safe = Regex.Replace(profile, "[^A-Za-z0-9_-]", "_");
            return string.IsNullOrWhiteSpace(safe) ? "default" : safe;
        }

        static string? FindWindsurfExe(string dir)
        {
            // Stable: Windsurf.exe  /  Next: "Windsurf - Next.exe" (spaces around the dash)
            foreach (var name in new[] { "Windsurf - Next.exe", "Windsurf.exe" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        static string EnsureDir(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }

        static void Info(string msg) => AnsiConsole.MarkupLine($"[deepskyblue2][[WindsurfPortable]][/][grey] {Markup.Escape(msg)}[/]");

        static void Error(string msg) => AnsiConsole.MarkupLine($"[red][[ERROR]][/] {Markup.Escape(msg)}");
        static void Warn(string msg) => AnsiConsole.MarkupLine($"[yellow][[WARN]][/] {Markup.Escape(msg)}");
    }
}
