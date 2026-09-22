using AppKit;
using CoreGraphics;
using Foundation;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacSidebar : NSVisualEffectView
{
    private const string PreferenceKey = "sidebarCollapsed";
    public const float ExpandedWidth = 176;
    public const float CollapsedWidth = 56;
    private readonly List<MacSidebarItem> _items = [];
    private readonly NSStackView _itemsStack;
    private readonly NSTextField _appName;
    private readonly MacSidebarItem _timeline;
    private readonly NSLayoutConstraint _width;
    private bool _collapsed = NSUserDefaults.StandardUserDefaults.BoolForKey(PreferenceKey);

    public event Action<string>? SelectionChanged;
    public event Action? TimelineActivated;
    public string SelectedId { get; private set; } = string.Empty;

    public MacSidebar()
    {
        Material = NSVisualEffectMaterial.Sidebar;
        BlendingMode = NSVisualEffectBlendingMode.BehindWindow;
        State = NSVisualEffectState.FollowsWindowActiveState;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.MasksToBounds = true;
        _width = WidthAnchor.ConstraintEqualTo(_collapsed ? CollapsedWidth : ExpandedWidth);
        _width.Active = true;

        var icon = new NSImageView
        {
            Image = NSImage.ImageNamed("heartbeat") ?? NSImage.ImageNamed("NSApplicationIcon"),
            ImageScaling = NSImageScale.ProportionallyDown,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _appName = MacControls.Label("Heartbeat Dev", MacControls.HeadlineFont);
        _appName.AlphaValue = _collapsed ? 0 : 1;
        _appName.UsesSingleLineMode = true;
        _itemsStack = MacControls.VerticalStack(4);
        _timeline = new MacSidebarItem("timeline", "打开时间线", "arrow.up.forward.app", () => TimelineActivated?.Invoke());
        _timeline.SetButtonType(NSButtonType.MomentaryPushIn);
        _timeline.SetCollapsed(_collapsed);
        AddSubview(icon);
        AddSubview(_appName);
        AddSubview(_itemsStack);
        AddSubview(_timeline);
        NSLayoutConstraint.ActivateConstraints([
            icon.WidthAnchor.ConstraintEqualTo(28),
            icon.HeightAnchor.ConstraintEqualTo(28),
            icon.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            icon.TopAnchor.ConstraintEqualTo(TopAnchor, 18),
            _appName.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _appName.TopAnchor.ConstraintEqualTo(icon.BottomAnchor, 8),
            _itemsStack.TopAnchor.ConstraintEqualTo(icon.BottomAnchor, 40),
            _itemsStack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 8),
            _itemsStack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            _timeline.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 8),
            _timeline.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            _timeline.BottomAnchor.ConstraintEqualTo(BottomAnchor, -16),
        ]);
    }

    public void AddItem(string id, string title, string symbol)
    {
        var item = new MacSidebarItem(id, title, symbol, () => Select(id, true));
        item.SetCollapsed(_collapsed);
        _items.Add(item);
        _itemsStack.AddArrangedSubview(item);
        item.WidthAnchor.ConstraintEqualTo(_itemsStack.WidthAnchor).Active = true;
    }

    public void Select(string id, bool notify = false)
    {
        SelectedId = id;
        foreach (var item in _items) item.SetSelected(item.Id == id);
        if (notify) SelectionChanged?.Invoke(id);
    }

    public void SetTimelineEnabled(bool enabled) => _timeline.Enabled = enabled;

    public void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        NSUserDefaults.StandardUserDefaults.SetBool(_collapsed, PreferenceKey);
        // Labels do not constrain rail width. Repeated clicks retarget layout.
        foreach (var item in _items) item.SetCollapsed(_collapsed);
        _timeline.SetCollapsed(_collapsed);
        MacAnimation.Run(_ =>
        {
            _width.Constant = _collapsed ? CollapsedWidth : ExpandedWidth;
            _appName.AlphaValue = _collapsed ? 0 : 1;
            Superview?.LayoutSubtreeIfNeeded();
        }, MacAnimation.Sidebar);
    }
}

// Native buttons retain keyboard activation, focus rings and accessibility.
internal sealed class MacSidebarItem : NSButton
{
    public string Id { get; }
    private readonly string _title;

    public MacSidebarItem(string id, string title, string symbol, Action onClick)
    {
        Id = id;
        _title = title;
        Title = title;
        ToolTip = title;
        AccessibilityTitle = title;
        Image = NSImage.GetSystemSymbol(symbol, title);
        ImageScaling = NSImageScale.ProportionallyDown;
        ImagePosition = NSCellImagePosition.ImageLeft;
        Alignment = NSTextAlignment.Left;
        Font = MacControls.BodyFont;
        Bordered = false;
        SetButtonType(NSButtonType.Toggle);
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo(30).Active = true;
        SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        Activated += (_, _) => onClick();
    }

    public void SetSelected(bool selected)
    {
        State = selected ? NSCellStateValue.On : NSCellStateValue.Off;
        AccessibilitySelected = selected;
        ContentTintColor = selected ? NSColor.ControlAccent : NSColor.SecondaryLabel;
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (State == NSCellStateValue.On)
        {
            NSColor.ControlAccent.ColorWithAlphaComponent(0.16f).SetFill();
            using var selection = NSBezierPath.FromRoundedRect(Bounds, 6, 6);
            selection.Fill();
        }
        base.DrawRect(dirtyRect);
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    public void SetCollapsed(bool collapsed)
    {
        Title = collapsed ? string.Empty : _title;
        ImagePosition = collapsed ? NSCellImagePosition.ImageOnly : NSCellImagePosition.ImageLeft;
        Alignment = collapsed ? NSTextAlignment.Center : NSTextAlignment.Left;
    }
}
