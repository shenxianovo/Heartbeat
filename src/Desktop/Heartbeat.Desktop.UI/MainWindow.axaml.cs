using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop.UI;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private DesktopViewModel Model => (DesktopViewModel)DataContext!;
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(DesktopViewModel model, string iconName) : this()
    {
        DataContext = model;
        if (!model.IsConfigured) ConnectionTab.IsSelected = true;
        using var asset = AssetLoader.Open(new Uri($"avares://Heartbeat.Desktop.UI/Assets/{iconName}.png"));
        BrandIcon.Source = new Bitmap(asset);
        _refresh.Tick += Refresh;
        _refresh.Start();
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _refresh.Stop();
    }

    private void Refresh(object? sender, EventArgs e) => Model.Refresh();
    private async void ToggleCollection(object? sender, RoutedEventArgs e) => await Model.ToggleAsync();
    private async void SaveConnection(object? sender, RoutedEventArgs e) => await Model.SaveAsync();
    private async void OpenReplay(object? sender, RoutedEventArgs e) => await Model.OpenReplayAsync();
    private async void OpenAccessibility(object? sender, RoutedEventArgs e) => await Model.OpenPermissionAsync(ObservationCapability.WindowTitle);
    private async void OpenInput(object? sender, RoutedEventArgs e) => await Model.OpenPermissionAsync(ObservationCapability.Input);
}
