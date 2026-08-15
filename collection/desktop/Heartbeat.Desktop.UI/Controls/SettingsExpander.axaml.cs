using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;

namespace Heartbeat.Desktop.UI.Controls;

/// <summary>
/// A settings row that expands into a collection of <see cref="SettingsCard"/> items.
/// Header, description, icon, and action use the same vocabulary as SettingsCard.
/// </summary>
public partial class SettingsExpander : UserControl
{
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(Header));

    public static readonly StyledProperty<object?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(Description));

    public static readonly StyledProperty<object?> HeaderIconProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(HeaderIcon));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(ActionContent));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SettingsExpander, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<SettingsExpander, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<object?> ItemsHeaderProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(ItemsHeader));

    public static readonly StyledProperty<object?> ItemsFooterProperty =
        AvaloniaProperty.Register<SettingsExpander, object?>(nameof(ItemsFooter));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsExpander, bool>(nameof(IsExpanded));

    public SettingsExpander() => InitializeComponent();

    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public object? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? HeaderIcon { get => GetValue(HeaderIconProperty); set => SetValue(HeaderIconProperty, value); }
    public object? ActionContent { get => GetValue(ActionContentProperty); set => SetValue(ActionContentProperty, value); }
    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public object? ItemsHeader { get => GetValue(ItemsHeaderProperty); set => SetValue(ItemsHeaderProperty, value); }
    public object? ItemsFooter { get => GetValue(ItemsFooterProperty); set => SetValue(ItemsFooterProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private void ToggleExpanded(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;
}
