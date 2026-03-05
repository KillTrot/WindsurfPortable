using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace WindsurfPortable
{
    [JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
    internal partial class JsonContext : JsonSerializerContext { }

    public class UpdateResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; } // Version string e.g. "1.108.2-next"
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    [JsonSerializable(typeof(UpdateResponse))]
    internal partial class UpdateResponseContext : JsonSerializerContext { }

    public class UpdateManager
    {
        private readonly string _baseDir;
        private readonly string _appDir;
        private readonly bool _isNextBuild;
        private readonly string _updateUrl;
        private readonly string _stateFilePath;

        public UpdateManager(string baseDir, string appDir, bool isNextBuild, string stateFilePath)
        {
            _baseDir = baseDir;
            _appDir = appDir;
            _isNextBuild = isNextBuild;
            _stateFilePath = stateFilePath;

            var channel = isNextBuild ? WindsurfUpdateFeed.Channel.Next : WindsurfUpdateFeed.Channel.Stable;
            _updateUrl = WindsurfUpdateFeed.GetLatestUpdateApiUrl(channel, WindsurfUpdateFeed.PackageKind.Portable);
        }

        // UpdateResponse class moved out to fix source generator error

        public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Determine current version
                string currentVersion = GetCurrentVersion();
                if (string.IsNullOrEmpty(currentVersion))
                {
                    return; // Cannot determine local version
                }

                using var httpClient = new HttpClient();
                var response = await httpClient.GetFromJsonAsync(_updateUrl, UpdateResponseContext.Default.UpdateResponse, cancellationToken);
                
                if (response != null && !string.IsNullOrEmpty(response.Name) && !string.IsNullOrEmpty(response.Url))
                {
                    string remoteVersion = response.Name; // Sometimes it's in Name, sometimes in Version

                    if (remoteVersion != currentVersion)
                    {
                        // Check if we already skipped this version or prompted recently
                        if (ShouldSkipUpdate(remoteVersion))
                            return;

                        // Proceed to download and prompt
                        await HandleUpdateAvailableAsync(response.Url, remoteVersion, httpClient, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                // Silently ignore update check failures so it doesn't break the launcher
                Debug.WriteLine($"Update check failed: {ex.Message}");
            }
        }

        private string GetCurrentVersion()
        {
            string packageJsonPath = Path.Combine(_appDir, "package.json");
            if (File.Exists(packageJsonPath))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
                    if (doc.RootElement.TryGetProperty("version", out var versionElement))
                    {
                        return versionElement.GetString() ?? "";
                    }
                }
                catch { }
            }
            return "";
        }

        private bool ShouldSkipUpdate(string remoteVersion)
        {
            if (File.Exists(_stateFilePath))
            {
                try
                {
                    var stateStr = File.ReadAllText(_stateFilePath);
                    var state = JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString);
                    if (state != null && state.TryGetValue("skipped_update_version", out string? skippedVersion))
                    {
                        if (skippedVersion == remoteVersion)
                            return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public void SkipUpdate(string remoteVersion)
        {
            try
            {
                System.Collections.Generic.Dictionary<string, string> state = new();
                if (File.Exists(_stateFilePath))
                {
                    var stateStr = File.ReadAllText(_stateFilePath);
                    state = JsonSerializer.Deserialize(stateStr, JsonContext.Default.DictionaryStringString) ?? new();
                }
                state["skipped_update_version"] = remoteVersion;
                File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, JsonContext.Default.DictionaryStringString));
            }
            catch { }
        }

        public event Action<string, string>? UpdateAvailable;

        private Task HandleUpdateAvailableAsync(string downloadUrl, string newVersion, HttpClient httpClient, CancellationToken cancellationToken)
        {
            UpdateAvailable?.Invoke(newVersion, downloadUrl);
            return Task.CompletedTask;
        }

        public void ApplyUpdateAndRestart(string extractPath, string baseDir)
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return;

            var restartArgs = Environment.GetCommandLineArgs().Skip(1);

            var psi = new ProcessStartInfo
            {
                FileName = currentExe,
                UseShellExecute = false,
            };

            psi.ArgumentList.Add("--apply-windsurf-update");
            psi.ArgumentList.Add(extractPath);
            psi.ArgumentList.Add("--");
            foreach (var a in restartArgs)
                psi.ArgumentList.Add(a);

            Process.Start(psi);
            Environment.Exit(0);
        }
    }
}
