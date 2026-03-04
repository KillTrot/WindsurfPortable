using System;
using System.Runtime.InteropServices;

namespace WindsurfPortable;

public static class WindsurfUpdateFeed
{
    private const string StableHost = "https://windsurf-stable.codeium.com";
    private const string NextHost = "https://windsurf-next.codeium.com";

    public enum Channel
    {
        Stable,
        Next,
    }

    public enum PackageKind
    {
        Portable,
        Installer,
        UserInstaller,
        Dmg,
        Deb,
    }

    public static string GetLatestUpdateApiUrl(Channel channel, PackageKind kind)
    {
        string host = channel == Channel.Next ? NextHost : StableHost;
        string feedId = GetFeedId(kind);
        string channelSegment = channel == Channel.Next ? "next" : "stable";
        return $"{host}/api/update/{feedId}/{channelSegment}/latest";
    }

    public static PackageKind GetDefaultPortableKindForCurrentOs()
    {
        if (OperatingSystem.IsWindows()) return PackageKind.Portable;
        if (OperatingSystem.IsMacOS()) return PackageKind.Portable;
        if (OperatingSystem.IsLinux()) return PackageKind.Portable;
        return PackageKind.Portable;
    }

    private static string GetFeedId(PackageKind kind)
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsWindows())
        {
            bool arm64 = arch == Architecture.Arm64;
            return kind switch
            {
                PackageKind.Portable => arm64 ? "win32-arm64-archive" : "win32-x64-archive",
                PackageKind.Installer => arm64 ? "win32-arm64" : "win32-x64",
                PackageKind.UserInstaller => arm64 ? "win32-arm64-user" : "win32-x64-user",
                _ => throw new NotSupportedException($"Package kind '{kind}' is not supported on Windows.")
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            bool arm64 = arch == Architecture.Arm64;
            return kind switch
            {
                PackageKind.Portable => arm64 ? "darwin-arm64" : "darwin-x64",
                PackageKind.Dmg => arm64 ? "darwin-arm64-dmg" : "darwin-x64-dmg",
                _ => throw new NotSupportedException($"Package kind '{kind}' is not supported on macOS.")
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return kind switch
            {
                PackageKind.Portable => "linux-x64",
                PackageKind.Deb => "linux-x64-deb",
                _ => throw new NotSupportedException($"Package kind '{kind}' is not supported on Linux.")
            };
        }

        throw new NotSupportedException("Unsupported OS for Windsurf update feeds.");
    }
}
