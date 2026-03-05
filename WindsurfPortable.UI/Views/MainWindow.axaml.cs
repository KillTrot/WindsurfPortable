using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using System.ComponentModel;
using WindsurfPortable.UI.ViewModels;

namespace WindsurfPortable.UI.Views;

public partial class MainWindow : Window
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

    private async void OnCreateProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var profileName = await ShowProfileNameDialogAsync();
        if (profileName == null)
            return;

        if (!vm.TryCreateProfile(profileName, out var error) && !string.IsNullOrWhiteSpace(error))
            await ShowMessageDialogAsync("Create profile failed", error);
    }

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanDeleteSelectedProfile)
            return;

        var profile = vm.SelectedProfile;
        var confirmed = await ShowConfirmationDialogAsync(
            "Delete profile?",
            $"Delete profile '{profile}'? This will remove all associated data from disk.",
            "Delete",
            "Cancel");

        if (!confirmed)
            return;

        if (!vm.TryDeleteSelectedProfile(out var error) && !string.IsNullOrWhiteSpace(error))
            await ShowMessageDialogAsync("Delete profile failed", error);
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

    private async System.Threading.Tasks.Task<string?> ShowProfileNameDialogAsync()
    {
        var dialog = new Window
        {
            Title = "Create Profile",
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var nameBox = new TextBox
        {
            Watermark = "Profile name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        var createButton = new Button { Content = "Create", Classes = { "Primary" }, MinWidth = 90 };

        string? result = null;

        cancelButton.Click += (_, _) => dialog.Close();
        createButton.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            result = name;
            dialog.Close();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Enter a profile name",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                },
                nameBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancelButton, createButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmText, string cancelText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirmButton = new Button { Content = confirmText, Classes = { "Primary" }, MinWidth = 90 };
        var cancelButton = new Button { Content = cancelText, MinWidth = 90 };

        var confirmed = false;
        confirmButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancelButton, confirmButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async System.Threading.Tasks.Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var okButton = new Button { Content = "OK", Classes = { "Primary" }, MinWidth = 90 };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okButton }
                }
            }
        };

        await dialog.ShowDialog(this);
    }
}