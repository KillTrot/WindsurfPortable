using System;
using System.IO;

namespace WindsurfPortable;

public static class UpdateApplier
{
    public static void ApplyExtractedUpdateToBaseDirectory(string extractPath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(extractPath) || !Directory.Exists(extractPath))
            throw new DirectoryNotFoundException($"Update extract path not found: {extractPath}");

        if (string.IsNullOrWhiteSpace(baseDir))
            throw new DirectoryNotFoundException($"Base directory not found: {baseDir}");

        Directory.CreateDirectory(baseDir);

        CopyDirectory(extractPath, baseDir, Environment.ProcessPath);

        try
        {
            Directory.Delete(extractPath, recursive: true);
        }
        catch
        {
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, string? runningExePath)
    {
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);

            try
            {
                if (!string.IsNullOrWhiteSpace(runningExePath) &&
                    string.Equals(Path.GetFullPath(dest), Path.GetFullPath(runningExePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            catch
            {
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
