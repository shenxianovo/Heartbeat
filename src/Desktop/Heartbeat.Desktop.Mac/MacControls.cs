using AppKit;
using CoreGraphics;

namespace Heartbeat.Desktop.Mac;

internal static class MacControls
{
    public static NSFont LargeTitleFont => NSFont.SystemFontOfSize(22, NSFontWeight.Semibold)!;
    public static NSFont Title2Font => NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
    public static NSFont HeadlineFont => NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
    public static NSFont BodyFont => NSFont.SystemFontOfSize(13)!;
    public static NSFont CalloutFont => NSFont.SystemFontOfSize(12)!;
    public static NSFont FootnoteFont => NSFont.SystemFontOfSize(11)!;

    public static NSTextField Label(string text, NSFont font, NSColor? color = null)
    {
        var field = NSTextField.CreateLabel(text);
        field.Font = font;
        field.TextColor = color ?? NSColor.Label;
        field.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        field.UsesSingleLineMode = false;
        field.Cell!.Wraps = true;
        field.MaximumNumberOfLines = 0;
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        return field;
    }

    public static NSTextField Secondary(string text) => Label(text, FootnoteFont, NSColor.SecondaryLabel);

    public static NSImageView Symbol(string symbol, string description, float size = 18)
    {
        var view = new NSImageView
        {
            Image = NSImage.GetSystemSymbol(symbol, description),
            ContentTintColor = NSColor.SecondaryLabel,
            ImageScaling = NSImageScale.ProportionallyDown,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        view.WidthAnchor.ConstraintEqualTo(size).Active = true;
        view.HeightAnchor.ConstraintEqualTo(size).Active = true;
        return view;
    }

    public static NSButton Button(string title, Action action, string? symbolName = null, bool primary = false)
    {
        var button = NSButton.CreateButton(title, action);
        button.BezelStyle = NSBezelStyle.Rounded;
        button.ControlSize = NSControlSize.Regular;
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        button.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        button.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        if (symbolName is not null)
        {
            button.Image = NSImage.GetSystemSymbol(symbolName, title);
            button.ImagePosition = NSCellImagePosition.ImageLeft;
            button.ImageScaling = NSImageScale.ProportionallyDown;
        }
        if (primary) button.BezelColor = NSColor.ControlAccent;
        return button;
    }

    public static NSTextField Field(string placeholder, bool secret = false)
    {
        NSTextField field = secret ? new NSSecureTextField() : new NSTextField();
        field.Cell!.Bezeled = true;
        field.Cell.BezelStyle = NSTextFieldBezelStyle.Rounded;
        field.Cell.Scrollable = true;
        field.Cell.UsesSingleLineMode = true;
        field.PlaceholderString = placeholder;
        field.Font = BodyFont;
        field.TranslatesAutoresizingMaskIntoConstraints = false;
        field.HeightAnchor.ConstraintEqualTo(28).Active = true;
        field.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return field;
    }

    public static NSStackView VerticalStack(nfloat spacing, params NSView[] children) =>
        Stack(NSUserInterfaceLayoutOrientation.Vertical, NSLayoutAttribute.Leading, spacing, children);

    public static NSStackView HorizontalStack(nfloat spacing, params NSView[] children) =>
        Stack(NSUserInterfaceLayoutOrientation.Horizontal, NSLayoutAttribute.CenterY, spacing, children);

    public static void FillWidth(NSView parent, params NSView[] children)
    {
        foreach (var child in children)
        {
            child.LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor).Active = true;
            child.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor).Active = true;
        }
    }

    private static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, NSLayoutAttribute alignment,
        nfloat spacing, NSView[] children)
    {
        var stack = new NSStackView
        {
            Orientation = orientation,
            Spacing = spacing,
            Alignment = alignment,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        foreach (var child in children) stack.AddArrangedSubview(child);
        return stack;
    }

    public static NSView Card(NSView content, float padding = 16)
    {
        var box = new MacSurface(NSColor.ControlBackground, NSColor.Separator, 10);
        box.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor, padding),
            content.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor, -padding),
            content.TopAnchor.ConstraintEqualTo(box.TopAnchor, padding),
            content.BottomAnchor.ConstraintEqualTo(box.BottomAnchor, -padding),
        ]);
        return box;
    }

    public static NSView Divider()
    {
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        line.HeightAnchor.ConstraintEqualTo(1).Active = true;
        return line;
    }

    public static NSProgressIndicator Progress()
    {
        var spinner = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            Indeterminate = true,
            ControlSize = NSControlSize.Small,
            IsDisplayedWhenStopped = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        spinner.WidthAnchor.ConstraintEqualTo(16).Active = true;
        spinner.HeightAnchor.ConstraintEqualTo(16).Active = true;
        return spinner;
    }
}

// Draw dynamic NSColors in the current appearance instead of freezing CGColors
// in layers. AppKit invalidates drawing on a light/dark appearance change.
internal sealed class MacSurface : NSView
{
    private readonly nfloat _radius;
    public NSColor Fill { get; set; }
    public NSColor? Border { get; set; }

    public MacSurface(NSColor fill, NSColor? border = null, float radius = 0)
    {
        Fill = fill;
        Border = border;
        _radius = radius;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }
    public override void DrawRect(CGRect dirtyRect)
    {
        base.DrawRect(dirtyRect);
        var rect = Bounds;
        rect.Inflate(-0.5, -0.5);
        using var path = NSBezierPath.FromRoundedRect(rect, _radius, _radius);
        Fill.SetFill();
        path.Fill();
        if (Border is null) return;
        Border.SetStroke();
        path.LineWidth = 0.5f;
        path.Stroke();
    }
}

internal sealed class StatusPill : NSView
{
    private readonly NSImageView _icon;
    private readonly NSTextField _label;
    private (string Text, string Symbol) _current;

    public StatusPill()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _icon = MacControls.Symbol("circle", "状态", 12);
        _label = MacControls.Label("", MacControls.FootnoteFont);
        _label.UsesSingleLineMode = true;
        AddSubview(_icon);
        AddSubview(_label);
        NSLayoutConstraint.ActivateConstraints([
            _icon.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _icon.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _label.LeadingAnchor.ConstraintEqualTo(_icon.TrailingAnchor, 5),
            _label.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _label.TopAnchor.ConstraintEqualTo(TopAnchor, 3),
            _label.BottomAnchor.ConstraintEqualTo(BottomAnchor, -3),
        ]);
    }

    public void Update(string text, NSColor tint, string symbol)
    {
        if (_current == (text, symbol)) return;
        _current = (text, symbol);
        MacAnimation.FadeContentChange(this, () =>
        {
            _label.StringValue = text;
            _label.TextColor = tint;
            _icon.Image = NSImage.GetSystemSymbol(symbol, text);
            _icon.ContentTintColor = tint;
        });
    }
}
