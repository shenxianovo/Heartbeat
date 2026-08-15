using Avalonia;
using Avalonia.Controls;

namespace Heartbeat.Desktop.UI.Controls;

/// <summary>
/// A reusable settings row with a leading icon, descriptive text, and a trailing action.
/// The contract mirrors the information architecture of CommunityToolkit SettingsCard
/// without coupling the cross-platform UI to WinUI.
/// </summary>
public partial class SettingsCard : UserControl
{
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Header));

    public static readonly StyledProperty<object?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Description));

    public static readonly StyledProperty<object?> HeaderIconProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(HeaderIcon));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(ActionContent));

    public static readonly StyledProperty<bool> IsNestedProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsNested));

    public SettingsCard() => InitializeComponent();

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public bool IsNested
    {
        get => GetValue(IsNestedProperty);
        set => SetValue(IsNestedProperty, value);
    }
}
