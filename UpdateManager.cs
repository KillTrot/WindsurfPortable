using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace WindsurfPortable
{
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
            
            _updateUrl = isNextBuild 
                ? "https://windsurf-stable.codeium.com/api/update/win32-x64-archive/next/latest"
                : "https://windsurf-stable.codeium.com/api/update/win32-x64-archive/stable/latest";
        }

        private class UpdateResponse
        {
            [JsonPropertyName("url")]
            public string? Url { get; set; }
            [JsonPropertyName("name")]
            public string? Name { get; set; } // Version string e.g. "1.108.2-next"
            [JsonPropertyName("version")]
            public string? Version { get; set; }
        }

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
                var response = await httpClient.GetFromJsonAsync<UpdateResponse>(_updateUrl, cancellationToken);
                
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
                    var state = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(stateStr);
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
                    state = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(stateStr) ?? new();
                }
                state["skipped_update_version"] = remoteVersion;
                File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state));
            }
            catch { }
        }

        public event Action<string, string>? UpdateAvailable;

        private async Task HandleUpdateAvailableAsync(string downloadUrl, string newVersion, HttpClient httpClient, CancellationToken cancellationToken)
        {
            string tempZipPath = Path.Combine(_baseDir, "windsurf-update.zip");
            string extractPath = Path.Combine(_baseDir, "windsurf-update-ready");

            // Download
            var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cancellationToken);
            }

            // Extract
            if (Directory.Exists(extractPath))
                Directory.Delete(extractPath, true);
            
            ZipFile.ExtractToDirectory(tempZipPath, extractPath, overwriteFiles: true);

            // Trigger UI Prompt via Event
            UpdateAvailable?.Invoke(newVersion, extractPath);
            
            // Clean up zip
            File.Delete(tempZipPath);
        }

        public void ApplyUpdateAndRestart(string extractPath, string baseDir)
        {
            string updaterBat = Path.Combine(baseDir, "apply-update.bat");
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "WindsurfPortable.exe";

            string batContent = $@"
@echo off
echo Applying Windsurf update...
timeout /t 2 /nobreak > nul

:: Copy all files from extract path to base dir
xcopy ""{extractPath}\*"" ""{baseDir}\"" /s /e /y

:: Clean up
rmdir /s /q ""{extractPath}""
del ""%~f0""

:: Relaunch portable app
start """" ""{currentExe}""
";
            File.WriteAllText(updaterBat, batContent);

            var psi = new ProcessStartInfo
            {
                FileName = updaterBat,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            Environment.Exit(0);
        }
    }
}
