using AppKit;
using CoreAnimation;
using Foundation;
using System.Runtime.CompilerServices;

namespace Heartbeat.Desktop.Mac;

// Commit state immediately; stale completion callbacks must not hide newer content.
internal static class MacAnimation
{
    public static readonly TimeSpan Standard = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan Sidebar = TimeSpan.FromMilliseconds(200);
    private static readonly ConditionalWeakTable<NSView, Transition> Transitions = new();
    public static bool ReduceMotion => NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;

    public static void Run(Action<NSAnimationContext> body, TimeSpan? duration = null, Action? completion = null)
    {
        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = ReduceMotion ? 0 : (duration ?? Standard).TotalSeconds;
            context.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseInEaseOut);
            context.AllowsImplicitAnimation = true;
            body(context);
        }, completion ?? (() => { }));
    }

    public static void SetVisible(NSView view, bool visible, bool animated = true, Action? completion = null)
    {
        var transition = Transitions.GetOrCreateValue(view);
        var generation = ++transition.Generation;
        if (!animated || ReduceMotion)
        {
            view.Hidden = !visible;
            view.AlphaValue = visible ? 1 : 0;
            completion?.Invoke();
            return;
        }
        view.WantsLayer = true;
        if (view.Hidden) view.AlphaValue = 0;
        view.Hidden = false;
        // Animator is an Objective-C forwarding proxy. Wrapping it as a custom
        // managed NSView can fail native identity lookup in the .NET binding.
        // Layer-backed views participate in this implicit animation context.
        Run(_ => view.AlphaValue = visible ? 1 : 0, Short, () =>
            {
                if (generation != transition.Generation) return;
                view.Hidden = !visible;
                completion?.Invoke();
            });
    }

    public static void FadeContentChange(NSView view, Action apply, TimeSpan? duration = null)
    {
        apply();
        if (ReduceMotion || view.Window?.IsVisible != true || view.Hidden) return;
        view.WantsLayer = true;
        using var fade = new CABasicAnimation
        {
            KeyPath = "opacity",
            From = NSNumber.FromFloat(0.65f),
            To = NSNumber.FromFloat(1),
            Duration = (duration ?? Short).TotalSeconds,
        };
        view.Layer!.AddAnimation(fade, "heartbeat.content");
    }

    public static void SetBreathing(CALayer layer, bool enabled)
    {
        const string key = "heartbeat.breathing";
        layer.RemoveAnimation(key);
        if (!enabled || ReduceMotion) return;
        using var pulse = new CABasicAnimation
        {
            KeyPath = "opacity",
            From = NSNumber.FromFloat(1),
            To = NSNumber.FromFloat(0.8f),
            Duration = 1.1,
            AutoReverses = true,
            RepeatCount = float.PositiveInfinity,
            TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseInEaseOut),
        };
        layer.AddAnimation(pulse, key);
    }

    private sealed class Transition { public int Generation; }
}
