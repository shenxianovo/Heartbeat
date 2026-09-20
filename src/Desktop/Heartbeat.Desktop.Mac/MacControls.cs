using AppKit;
using CoreGraphics;

namespace Heartbeat.Desktop.Mac;

internal static class MacControls
{
    public static NSTextField Label(NSView parent, string text, double x, double y, double width, double height = 24)
    {
        var label = NSTextField.CreateLabel(text);
        label.Frame = new CGRect(x, y, width, height);
        label.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        parent.AddSubview(label);
        return label;
    }

    public static NSButton Button(NSView parent, string title, double x, double y, double width, Action action)
    {
        var button = NSButton.CreateButton(title, action);
        button.Frame = new CGRect(x, y, width, 32);
        button.BezelStyle = NSBezelStyle.Rounded;
        parent.AddSubview(button);
        return button;
    }

    public static NSTextField Field(NSView parent, string title, string value, double y, bool secret = false)
    {
        Label(parent, title, 24, y + 30, 620);
        NSTextField field = secret ? new NSSecureTextField() : new NSTextField();
        field.Frame = new CGRect(24, y, 620, 26);
        field.StringValue = value;
        parent.AddSubview(field);
        return field;
    }
}
