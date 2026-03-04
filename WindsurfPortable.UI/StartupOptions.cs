namespace WindsurfPortable.UI;

public sealed record StartupOptions(
    string? Profile,
    bool Start,
    bool Tray);
