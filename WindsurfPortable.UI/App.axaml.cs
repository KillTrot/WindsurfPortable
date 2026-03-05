using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using WindsurfPortable.UI.ViewModels;
using WindsurfPortable.UI.Views;

namespace WindsurfPortable.UI;

public partial class App : Application
{
    private TrayService? _tray;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var vm = new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = vm,
            };

            var startup = Program.StartupOptions;
            vm.ConfigureStartupInvocation(startup.IsForwardedInvocation, startup.ForwardArgs);

            vm.StartBackgroundUpdateLoops();

            desktop.MainWindow = mainWindow;

            desktop.Exit += (_, _) => vm.StopBackgroundUpdateLoops();

            try
            {
                _tray = new TrayService();
                _tray.ShowRequested += () =>
                {
                    mainWindow.Show();
                    mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                    mainWindow.Activate();
                    _tray.UpdateMenu(isWindowVisible: true);
                };
                _tray.HideRequested += () =>
                {
                    mainWindow.Hide();
                    _tray.UpdateMenu(isWindowVisible: false);
                };
                _tray.ExitRequested += () => desktop.Shutdown();
                _tray.LaunchDefaultRequested += () => vm.StartDefaultProfileInBackground();
                _tray.SetLaunchDefaultEnabled(vm.CanStartDefaultProfile);

                vm.DefaultProfileChanged += (_, _) => _tray.SetLaunchDefaultEnabled(vm.CanStartDefaultProfile);
                mainWindow.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Avalonia.Controls.Window.IsVisibleProperty)
                        _tray.UpdateMenu(mainWindow.IsVisible);
                };

                vm.HideToTrayRequested += (_, _) =>
                {
                    if (_tray != null)
                        mainWindow.Hide();
                };
            }
            catch
            {
                _tray = null;
            }

            var trayModeRequested = Program.StartupOptions.Tray || startup.IsForwardedInvocation;
            mainWindow.ConfigureCloseToTray(vm, trayModeRequested, trayAvailable: _tray != null);

            if (!string.IsNullOrWhiteSpace(startup.Profile))
                vm.SelectedProfile = startup.Profile.Trim();

            var shouldStart = startup.Autostart || vm.AutoStartDefaultProfile;

            if (shouldStart)
            {
                if (string.IsNullOrWhiteSpace(startup.Profile) && vm.AutoStartDefaultProfile)
                    vm.SetSelectedProfileToDefaultIfPossible();

                vm.StartSelectedProfileInBackground();
            }

            if (trayModeRequested && _tray != null)
            {
                if (shouldStart)
                {
                    // Only hide after a successful launch. If startup fails (e.g. Windsurf missing),
                    // keep the window visible so the user can see the error.
                    vm.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(MainWindowViewModel.IsWindsurfRunning) && vm.IsWindsurfRunning)
                            mainWindow.Hide();
                    };
                }
                else
                {
                    if (!startup.IsForwardedInvocation)
                        mainWindow.Hide();
                }
            }

            if (startup.IsForwardedInvocation)
            {
                var forwardedLaunchStarted = false;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(MainWindowViewModel.IsWindsurfRunning))
                        return;

                    if (vm.IsWindsurfRunning)
                    {
                        forwardedLaunchStarted = true;
                        if (_tray != null)
                            mainWindow.Hide();
                        return;
                    }

                    if (forwardedLaunchStarted)
                        desktop.Shutdown();
                };
            }

        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}