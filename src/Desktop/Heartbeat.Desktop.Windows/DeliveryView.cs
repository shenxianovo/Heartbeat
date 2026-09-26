using System.Diagnostics;
using Heartbeat.Management;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Heartbeat.Desktop.Windows;

internal sealed class DeliveryView : StackPanel
{
    private static readonly global::Windows.UI.Color[] Colors = [ColorHelper.FromArgb(255, 56, 169, 232), ColorHelper.FromArgb(255, 237, 154, 56), ColorHelper.FromArgb(255, 47, 188, 149)];
    private readonly Canvas _canvas = new() { Height = 124 };
    private readonly TextBlock _caption = new() { FontSize = 12 };
    private readonly UISettings _settings = new();
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private DeliveryActivitySnapshot? _activity;
    private long _observedAt;
    private long _drawnAt;
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> _lines = [];

    public DeliveryView()
    {
        Spacing = 6;
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        var titles = new[] { "接收", "发送", "确认" };
        for (var i = 0; i < 3; i++) legend.Children.Add(new TextBlock { Text = titles[i], Foreground = new SolidColorBrush(Colors[i]) });
        Children.Add(_caption); Children.Add(legend); Children.Add(_canvas);
        _canvas.SizeChanged += (_, _) => Draw();
        _animation.Tick += (_, _) => Scroll();
        Unloaded += (_, _) => _animation.Stop();
    }

    public void Refresh(DeliveryActivitySnapshot? activity, bool visible)
    {
        _activity = activity;
        _observedAt = Stopwatch.GetTimestamp();
        _caption.Text = activity is null ? "活动状态暂不可用" : "最近 60 秒 · Records / 秒";
        if (visible && _settings.AnimationsEnabled) _animation.Start(); else _animation.Stop();
        if (visible) Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        _lines.Clear();
        _drawnAt = Stopwatch.GetTimestamp();
        var width = Math.Max(1, _canvas.ActualWidth - 40);
        const double height = 94;
        var buckets = _activity?.Buckets ?? [];
        var maximum = Math.Max(4, Math.Ceiling(buckets.SelectMany(bucket => new[] { bucket.Received, bucket.Sent, bucket.Confirmed }).DefaultIfEmpty().Max() * 1.15 / 2) * 2);
        Label("−60 秒", 32, 104); Label("−30 秒", 32 + width / 2 - 18, 104); Label("现在", 32 + width - 24, 104);
        for (var step = 0; step <= 2; step++)
        {
            var y = height - height * step / 2;
            Label(Math.Round(maximum * step / 2).ToString("0", System.Globalization.CultureInfo.CurrentCulture), 0, y - 6);
            _canvas.Children.Add(new Line { X1 = 32, X2 = width + 32, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), Opacity = 0.2 });
        }
        if (_activity is null) return;
        var end = _activity.CapturedAt / 1000d + Stopwatch.GetElapsedTime(_observedAt).TotalSeconds;
        for (var stage = 0; stage < 3; stage++)
        {
            var figure = new PathFigure { IsClosed = false, IsFilled = false };
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var line = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, RenderTransform = new TranslateTransform(), Stroke = new SolidColorBrush(Colors[stage]), StrokeThickness = 1.8,
                Clip = new RectangleGeometry { Rect = new Rect(32, -1, width, height + 2) } };
            Point? previous = null;
            foreach (var bucket in buckets)
            {
                var value = stage == 0 ? bucket.Received : stage == 1 ? bucket.Sent : bucket.Confirmed;
                var point = new Point(32 + (bucket.Second - end + 60) / 60 * width, height - value / maximum * height);
                if (previous is { } before)
                {
                    var middle = (before.X + point.X) / 2;
                    figure.Segments.Add(new BezierSegment { Point1 = new Point(middle, before.Y), Point2 = new Point(middle, point.Y), Point3 = point });
                }
                else figure.StartPoint = point;
                previous = point;
            }
            _canvas.Children.Add(line);
            _lines.Add(line);
        }
    }

    private void Scroll()
    {
        var offset = -Stopwatch.GetElapsedTime(_drawnAt).TotalSeconds / 60 * Math.Max(1, _canvas.ActualWidth - 40);
        foreach (var line in _lines) ((TranslateTransform)line.RenderTransform).X = offset;
    }

    private void Label(string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Opacity = 0.7 };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); _canvas.Children.Add(label);
    }
}
