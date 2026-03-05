namespace WindsurfPortable.UI;

public sealed record StartupOptions(
    string? Profile,
    bool Autostart,
    bool Tray,
    string[] ForwardArgs,
    bool IsForwardedInvocation);
