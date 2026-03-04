using System;

namespace WindsurfPortable.UI;

public static class ArgParser
{
    public static StartupOptions Parse(string[] args)
    {
        string? profile = null;
        bool start = false;
        bool tray = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                profile = args[i + 1];
                i++;
                continue;
            }

            if (string.Equals(arg, "--start", StringComparison.OrdinalIgnoreCase))
            {
                start = true;
                continue;
            }

            if (string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
                continue;
            }
        }

        return new StartupOptions(profile, start, tray);
    }
}
