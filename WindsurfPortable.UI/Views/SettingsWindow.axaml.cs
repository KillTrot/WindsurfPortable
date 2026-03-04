using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace WindsurfPortable.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
