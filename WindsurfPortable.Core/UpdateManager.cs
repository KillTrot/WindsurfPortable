using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Formats.Tar;
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

        private async Task HandleUpdateAvailableAsync(string downloadUrl, string newVersion, HttpClient httpClient, CancellationToken cancellationToken)
        {
            string safeVersion = MakeSafePathSegment(newVersion);
            string extractPath = Path.Combine(_baseDir, $"windsurf-update-ready-{safeVersion}");

            if (Directory.Exists(extractPath))
            {
                UpdateAvailable?.Invoke(newVersion, extractPath);
                return;
            }

            string tempPackagePath = Path.Combine(_baseDir, GetUpdatePackageFileName(downloadUrl, safeVersion));

            // Download
            var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using (var fs = new FileStream(tempPackagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cancellationToken);
            }

            ExtractUpdatePackage(tempPackagePath, extractPath);

            // Trigger UI Prompt via Event
            UpdateAvailable?.Invoke(newVersion, extractPath);
            
            // Clean up downloaded package
            try
            {
                File.Delete(tempPackagePath);
            }
            catch
            {
            }
        }

        private static string GetUpdatePackageFileName(string downloadUrl, string safeVersion)
        {
            try
            {
                var uri = new Uri(downloadUrl);
                var name = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(name))
                    return $"windsurf-update-{safeVersion}-{name}";
            }
            catch
            {
            }

            return $"windsurf-update-{safeVersion}.pkg";
        }

        private static string MakeSafePathSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (invalid.Contains(chars[i]))
                    chars[i] = '_';
            }
            return new string(chars);
        }

        private static void ExtractUpdatePackage(string packagePath, string extractPath)
        {
            if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
                return;
            }

            if (packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || packagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(packagePath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, extractPath, overwriteFiles: true);
                return;
            }

            throw new NotSupportedException($"Unsupported Windsurf update package format: {packagePath}");
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
