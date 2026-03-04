using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WindsurfPortable.UI;

internal static class WindowsShortcutService
{
    private const string ShortcutFileName = "WindsurfPortable.lnk";

    public static void CreateOrUpdateStartMenuShortcut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programsDir))
            throw new InvalidOperationException("Could not resolve Start Menu Programs directory.");

        var lnkPath = Path.Combine(programsDir, ShortcutFileName);
        CreateOrUpdateShortcut(lnkPath);
    }

    public static void RemoveStartMenuShortcut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programsDir))
            return;

        var lnkPath = Path.Combine(programsDir, ShortcutFileName);
        TryDeleteFile(lnkPath);
    }

    public static void CreateOrUpdateDesktopShortcut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopDir))
            throw new InvalidOperationException("Could not resolve Desktop directory.");

        var lnkPath = Path.Combine(desktopDir, ShortcutFileName);
        CreateOrUpdateShortcut(lnkPath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void CreateOrUpdateShortcut(string lnkPath)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("Could not resolve current executable path.");

        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

        var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
        if (shellLinkType == null)
            throw new InvalidOperationException("Could not resolve ShellLink COM type.");

        var shellLink = (IShellLinkW)Activator.CreateInstance(shellLinkType)!;

        shellLink.SetPath(exePath);
        shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
        shellLink.SetIconLocation(exePath, 0);
        shellLink.SetDescription("WindsurfPortable");

        var persist = (IPersistFile)shellLink;
        persist.Save(lnkPath, true);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cchMaxPath,
            out WIN32_FIND_DATAW pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName,
            int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir,
            int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs,
            int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
