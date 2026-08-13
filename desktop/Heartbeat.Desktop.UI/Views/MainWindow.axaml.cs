using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using System.ComponentModel;
using Heartbeat.Desktop.UI.ViewModels;

namespace Heartbeat.Desktop.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += HandleClosing;
        SizeChanged += HandleSizeChanged;
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        ApplyTheme(viewModel);
        ApplyResponsiveLayout(viewModel);
    }

    public bool AllowClose { get; set; }

    private void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        if (DataContext is MainViewModel viewModel)
            viewModel.CloseSettingsCommand.Execute(null);
    }

    private void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            ApplyResponsiveLayout(viewModel);
    }

    private void ApplyResponsiveLayout(MainViewModel viewModel)
    {
        viewModel.IsSidebarCollapsed = Bounds.Width < 820;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(viewModel.IsSidebarCollapsed ? 68 : 176);
    }

    private static void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedThemeMode) && sender is MainViewModel viewModel)
            ApplyTheme(viewModel);
    }

    private static void ApplyTheme(MainViewModel viewModel)
    {
        if (Application.Current == null) return;
        var mode = viewModel.SelectedThemeMode;
        if (Enum.TryParse<Presentation.DesktopThemeMode>(
                Environment.GetEnvironmentVariable("HEARTBEAT_THEME_OVERRIDE"),
                true,
                out var overrideMode))
            mode = overrideMode;

        Application.Current.RequestedThemeVariant = mode switch
        {
            Presentation.DesktopThemeMode.Light => ThemeVariant.Light,
            Presentation.DesktopThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (AllowClose && DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
            viewModel.Dispose();
        }
        base.OnUnloaded(e);
    }
}
