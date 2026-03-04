using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using System.ComponentModel;
using WindsurfPortable.UI.ViewModels;

namespace WindsurfPortable.UI.Views;

public partial class MainWindow : ShadUI.Window
{
    private bool _trayModeEnabled;
    private bool _trayAvailable;
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow
        {
            DataContext = DataContext,
        };

        await dialog.ShowDialog(this);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public void ConfigureCloseToTray(MainWindowViewModel vm, bool trayRequested, bool trayAvailable)
    {
        _vm = vm;
        _trayModeEnabled = trayRequested;
        _trayAvailable = trayAvailable;

        Closing -= OnClosingToTray;
        Closing += OnClosingToTray;

        PropertyChanged -= OnWindowPropertyChanged;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnClosingToTray(object? sender, WindowClosingEventArgs e)
    {
        if (!_trayAvailable)
            return;

        if (_trayModeEnabled || (_vm?.IsWindsurfRunning ?? false))
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_trayAvailable)
            return;

        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
        {
            if (_trayModeEnabled || (_vm?.IsWindsurfRunning ?? false))
            {
                Hide();
            }
        }
    }
}