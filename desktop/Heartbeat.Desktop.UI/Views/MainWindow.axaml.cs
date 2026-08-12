using Avalonia.Controls;
using Avalonia.Interactivity;
using Heartbeat.Desktop.UI.ViewModels;

namespace Heartbeat.Desktop.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += HandleClosing;
    }

    public MainWindow(MainViewModel viewModel) : this() => DataContext = viewModel;

    public bool AllowClose { get; set; }

    private void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        if (DataContext is MainViewModel viewModel)
            viewModel.CloseSettingsCommand.Execute(null);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (AllowClose && DataContext is IDisposable disposable)
            disposable.Dispose();
        base.OnUnloaded(e);
    }
}
