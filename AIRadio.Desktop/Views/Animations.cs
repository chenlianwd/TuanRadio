using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Views;

public static class Animations
{
    // 徽章"说话"弹跳：两连跳的真实位移动画（首跳大、二跳小，抛物线轨迹）。
    // 旧实现是透明度闪烁——深色徽章在深色头栏上肉眼不可见，用户从未感知到，已废弃。
    public static async Task PlayBounceAsync(Border border)
    {
        if (border.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            border.RenderTransform = translate;
        }

        await HopAsync(translate, amplitude: 7, durationMs: 280);
        await Task.Delay(60);
        await HopAsync(translate, amplitude: 4, durationMs: 240);
        translate.Y = 0;
    }

    private static async Task HopAsync(TranslateTransform translate, double amplitude, double durationMs)
    {
        var start = DateTimeOffset.Now;
        while (true)
        {
            var progress = Math.Min(1.0, (DateTimeOffset.Now - start).TotalMilliseconds / durationMs);
            // 抛物线 4t(1-t)：起跳/落地速度最大、顶点速度为零，符合物理直觉的"跳"
            translate.Y = -amplitude * 4 * progress * (1 - progress);
            if (progress >= 1.0) break;
            await Task.Delay(16);
        }
        translate.Y = 0;
    }
}
