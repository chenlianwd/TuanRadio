using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Views;

public static class Animations
{
    public static Task FadeOutAsync(Control control, TimeSpan duration)
    {
        var animation = new OpacityFadeAnimation(duration, 1.0, 0.0);
        return animation.RunAsync(control);
    }

    public static async Task FadeInAsync(Control control, TimeSpan duration)
    {
        var animation = new OpacityFadeAnimation(duration, control.Opacity, 1.0);
        await animation.RunAsync(control);
    }

    public static async Task PlayBounceAsync(Border border)
    {
        await FadeOutAsync(border, TimeSpan.FromMilliseconds(80));
        await FadeInAsync(border, TimeSpan.FromMilliseconds(120));
        await Task.Delay(30);
        await FadeOutAsync(border, TimeSpan.FromMilliseconds(80));
        await FadeInAsync(border, TimeSpan.FromMilliseconds(100));
    }
}

public class OpacityFadeAnimation
{
    private readonly TimeSpan _duration;
    private readonly double _from;
    private readonly double _to;

    public OpacityFadeAnimation(TimeSpan duration, double from, double to)
    {
        _duration = duration;
        _from = from;
        _to = to;
    }

    public async Task RunAsync(Control control)
    {
        var start = DateTimeOffset.Now;
        var original = control.Opacity;
        control.Opacity = _from;

        while (true)
        {
            var elapsed = DateTimeOffset.Now - start;
            var progress = Math.Min(1.0, elapsed.TotalMilliseconds / _duration.TotalMilliseconds);
            control.Opacity = _from + (_to - _from) * progress;

            if (progress >= 1.0) break;
            await Task.Delay(16);
        }
        control.Opacity = _to;
    }
}