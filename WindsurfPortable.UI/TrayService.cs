using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace WindsurfPortable.UI;

public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _hideItem;
    private readonly NativeMenuItem _launchDefaultItem;

    public event Action? ShowRequested;
    public event Action? HideRequested;
    public event Action? ExitRequested;
    public event Action? LaunchDefaultRequested;

    public TrayService()
    {
        var iconStream = AssetLoader.Open(new Uri("avares://WindsurfPortable.UI/Assets/avalonia-logo.ico"));
        var windowIcon = new WindowIcon(iconStream);

        _showItem = new NativeMenuItem { Header = "Show" };
        _hideItem = new NativeMenuItem { Header = "Hide" };
        _launchDefaultItem = new NativeMenuItem { Header = "Launch default profile" };
        var exitItem = new NativeMenuItem { Header = "Exit" };

        _showItem.Click += (_, _) => ShowRequested?.Invoke();
        _hideItem.Click += (_, _) => HideRequested?.Invoke();
        _launchDefaultItem.Click += (_, _) => LaunchDefaultRequested?.Invoke();
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new NativeMenu();
        menu.Items.Add(_showItem);
        menu.Items.Add(_hideItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_launchDefaultItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = windowIcon,
            ToolTipText = "Windsurf Portable",
            Menu = menu,
        };

        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

        UpdateMenu(isWindowVisible: true);
    }

    public void UpdateMenu(bool isWindowVisible)
    {
        _showItem.IsEnabled = !isWindowVisible;
        _hideItem.IsEnabled = isWindowVisible;
    }

    public void SetLaunchDefaultEnabled(bool enabled)
    {
        _launchDefaultItem.IsEnabled = enabled;
    }

    public void Dispose()
    {
        try
        {
            TrayIcon.SetIcons(Application.Current!, null);
        }
        catch
        {
        }
    }
}
