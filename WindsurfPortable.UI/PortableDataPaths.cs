using System;
using System.IO;

namespace WindsurfPortable.UI;

internal static class PortableDataPaths
{
    public static string GetDataRoot(string launcherBaseDirectory)
    {
        var launcherDir = Path.GetFullPath(launcherBaseDirectory);

        if (!IsVelopackInstalledLayout(launcherDir))
            return launcherDir;

        var parent = Directory.GetParent(launcherDir);
        return parent?.FullName is { Length: > 0 } parentDir
            ? parentDir
            : launcherDir;
    }

    private static bool IsVelopackInstalledLayout(string launcherDir)
    {
        var trimmed = launcherDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentDirName = Path.GetFileName(trimmed);
        if (!string.Equals(currentDirName, "current", StringComparison.OrdinalIgnoreCase))
            return false;

        var parent = Directory.GetParent(trimmed);
        if (parent == null)
            return false;

        var parentPath = parent.FullName;
        var hasPackagesDirectory = Directory.Exists(Path.Combine(parentPath, "packages"));
        var hasWindowsUpdateExe = File.Exists(Path.Combine(parentPath, "Update.exe"));

        return hasPackagesDirectory || hasWindowsUpdateExe;
    }
}
