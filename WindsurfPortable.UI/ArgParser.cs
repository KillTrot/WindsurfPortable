using System;

namespace WindsurfPortable.UI;

public static class ArgParser
{
    public static StartupOptions Parse(string[] args)
    {
        string? profile = null;
        bool autostart = false;
        bool tray = false;
        var forwardArgs = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--wp-profile=", StringComparison.OrdinalIgnoreCase))
            {
                profile = arg[13..];
                continue;
            }

            if (string.Equals(arg, "--wp-profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                profile = args[i + 1];
                i++;
                continue;
            }

            if (string.Equals(arg, "--wp-autostart", StringComparison.OrdinalIgnoreCase))
            {
                autostart = true;
                continue;
            }

            if (string.Equals(arg, "--wp-tray", StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
                continue;
            }

            forwardArgs.Add(arg);
        }

        var forwarded = forwardArgs.ToArray();
        return new StartupOptions(profile, autostart, tray, forwarded, forwarded.Length > 0);
    }
}
