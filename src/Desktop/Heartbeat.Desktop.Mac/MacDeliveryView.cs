using System.Diagnostics;
using AppKit;
using CoreGraphics;
using Foundation;
using Heartbeat.Management;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacDeliveryView : NSView
{
    private static readonly NSColor[] Colors = [NSColor.SystemBlue, NSColor.SystemOrange, NSColor.SystemGreen];
    private DeliveryActivitySnapshot? _activity;
    private long _observedAt;
    private NSTimer? _animation;

    public MacDeliveryView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        HeightAnchor.ConstraintEqualTo(155).Active = true;
        AccessibilityLabel = "最近 60 秒收发曲线，纵轴每秒 Record 快照数，含重试和续期";
    }

    public void Refresh(DeliveryActivitySnapshot? activity, bool visible)
    {
        _activity = activity;
        _observedAt = Stopwatch.GetTimestamp();
        if (!visible || MacAnimation.ReduceMotion)
        {
            _animation?.Invalidate();
            _animation?.Dispose();
            _animation = null;
        }
        else _animation ??= NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(50), _ => NeedsDisplay = true);
        NeedsDisplay = visible;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        base.DrawRect(dirtyRect);
        var width = Math.Max(1, (double)Bounds.Width - 40);
        const double bottom = 24, height = 94;
        Text(_activity is null ? "活动状态暂不可用" : "Records / 秒", 0, 132, NSColor.SecondaryLabel);
        var labels = new[] { "接收", "发送", "确认" };
        for (var i = 0; i < labels.Length; i++) Text(labels[i], 128 + i * 60, 132, Colors[i]);
        Text("−60 秒", 32, 4, NSColor.SecondaryLabel);
        Text("−30 秒", 32 + width / 2 - 18, 4, NSColor.SecondaryLabel);
        Text("现在", 32 + width - 24, 4, NSColor.SecondaryLabel);
        var buckets = _activity?.Buckets ?? [];
        var maximum = Math.Max(4, Math.Ceiling(buckets.SelectMany(bucket => new[] { bucket.Received, bucket.Sent, bucket.Confirmed }).DefaultIfEmpty().Max() * 1.15 / 2) * 2);
        for (var step = 0; step <= 2; step++)
        {
            var y = bottom + height * step / 2;
            Text(Math.Round(maximum * step / 2).ToString("0", System.Globalization.CultureInfo.CurrentCulture), 0, y - 5, NSColor.SecondaryLabel);
            using var grid = new NSBezierPath();
            NSColor.Separator.SetStroke();
            grid.MoveTo(new CGPoint(32, y)); grid.LineTo(new CGPoint(32 + width, y)); grid.Stroke();
        }
        if (_activity is { } activity) DrawSeries(activity, width, maximum);
    }

    private void DrawSeries(DeliveryActivitySnapshot activity, double width, double maximum)
    {
        const double bottom = 24, height = 94;
        var end = activity.CapturedAt / 1000d + Stopwatch.GetElapsedTime(_observedAt).TotalSeconds;
        NSGraphicsContext.CurrentContext!.SaveGraphicsState();
        using var clip = NSBezierPath.FromRect(new CGRect(32, bottom - 1, width, height + 2));
        clip.AddClip();
        for (var stage = 0; stage < 3; stage++)
        {
            using var line = new NSBezierPath { LineWidth = 1.8f };
            var first = true;
            foreach (var bucket in activity.Buckets)
            {
                var value = stage == 0 ? bucket.Received : stage == 1 ? bucket.Sent : bucket.Confirmed;
                var point = new CGPoint(32 + (bucket.Second - end + 60) / 60 * width, bottom + value / maximum * height);
                if (first) line.MoveTo(point); else line.LineTo(point);
                first = false;
            }
            Colors[stage].SetStroke(); line.Stroke();
        }
        NSGraphicsContext.CurrentContext!.RestoreGraphicsState();
    }

    private static void Text(string text, double x, double y, NSColor color)
    {
        using var value = new NSAttributedString(text, new NSStringAttributes { Font = NSFont.SystemFontOfSize(10), ForegroundColor = color });
        value.DrawString(new CGPoint(x, y));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animation?.Invalidate(); _animation?.Dispose(); }
        base.Dispose(disposing);
    }
}
