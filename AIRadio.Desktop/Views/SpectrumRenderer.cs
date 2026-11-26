using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIRadio.Desktop.Views;

/// <summary>低分配频谱绘制器：同一份 FFT 数据可切换柱状、镜像、波形和粒子视图。</summary>
public sealed class SpectrumRenderer : Control
{
    public static readonly StyledProperty<string> StyleNameProperty =
        AvaloniaProperty.Register<SpectrumRenderer, string>(nameof(StyleName), "bars");

    public static readonly StyledProperty<IBrush?> PrimaryBrushProperty =
        AvaloniaProperty.Register<SpectrumRenderer, IBrush?>(nameof(PrimaryBrush));

    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty =
        AvaloniaProperty.Register<SpectrumRenderer, IBrush?>(nameof(SecondaryBrush));

    public static readonly StyledProperty<IBrush?> PeakBrushProperty =
        AvaloniaProperty.Register<SpectrumRenderer, IBrush?>(nameof(PeakBrush));

    private const int VfdPeakHoldFrameCount = 8;
    private const float VfdPeakFallStep = 0.035f;

    private float[] _bands = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();
    private int[] _peakHoldFrames = Array.Empty<int>();
    private IBrush? _wavePrimaryBrush;
    private IBrush? _waveSecondaryBrush;
    private Pen? _wavePrimaryPen;
    private Pen? _waveGlowPen;

    static SpectrumRenderer()
    {
        AffectsRender<SpectrumRenderer>(StyleNameProperty, PrimaryBrushProperty, SecondaryBrushProperty, PeakBrushProperty);
    }

    public string StyleName
    {
        get => GetValue(StyleNameProperty);
        set => SetValue(StyleNameProperty, value);
    }

    public IBrush? PrimaryBrush
    {
        get => GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public IBrush? SecondaryBrush
    {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public IBrush? PeakBrush
    {
        get => GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public void SetBands(float[] data)
    {
        if (_bands.Length != data.Length)
        {
            _bands = new float[data.Length];
            _peaks = new float[data.Length];
            _peakHoldFrames = new int[data.Length];
        }
        Array.Copy(data, _bands, data.Length);

        // Peak Hold 物理状态仅在数据推入时推进，避免 Render() 额外重绘加速峰值下落
        for (var i = 0; i < _bands.Length; i++)
        {
            var level = Math.Clamp(_bands[i], 0f, 1f);
            if (level >= _peaks[i])
            {
                _peaks[i] = level;
                _peakHoldFrames[i] = VfdPeakHoldFrameCount;
            }
            else if (_peakHoldFrames[i] > 0)
            {
                _peakHoldFrames[i]--;
            }
            else
            {
                _peaks[i] = Math.Max(level, _peaks[i] - VfdPeakFallStep);
            }
        }

        InvalidateVisual();
    }

    /// <summary>测试钩子：headless 无法断言绘制输出，事件→SetBands 接线以此锚定。</summary>
    internal float[] Bands => _bands;

    /// <summary>测试钩子：观察 VFD Peak Hold 峰值状态。</summary>
    internal float[] Peaks => _peaks;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bands.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0 || PrimaryBrush == null)
            return;

        switch (StyleName)
        {
            case "mirror":
                DrawMirror(context);
                break;
            case "wave":
                DrawWave(context);
                break;
            case "particles":
                DrawParticles(context);
                break;
            case "vfd":
                DrawVfd(context);
                break;
            default:
                DrawBars(context);
                break;
        }
    }

    private void DrawBars(DrawingContext context)
    {
        var slot = Bounds.Width / _bands.Length;
        var width = Math.Max(2, slot * 0.68);
        for (var i = 0; i < _bands.Length; i++)
        {
            var height = 4 + Level(i) * Math.Max(0, Bounds.Height - 4);
            var x = i * slot + (slot - width) / 2;
            context.DrawRectangle(PrimaryBrush, null, new Rect(x, Bounds.Height - height, width, height), width / 2, width / 2);
        }
    }

    private void DrawMirror(DrawingContext context)
    {
        var slot = Bounds.Width / _bands.Length;
        var width = Math.Max(2, slot * 0.58);
        var center = Bounds.Height / 2;
        for (var i = 0; i < _bands.Length; i++)
        {
            var halfHeight = 2 + Level(i) * Math.Max(0, center - 3);
            var x = i * slot + (slot - width) / 2;
            context.DrawRectangle(PrimaryBrush, null, new Rect(x, center - halfHeight, width, halfHeight * 2), width / 2, width / 2);
        }
    }

    private void DrawWave(DrawingContext context)
    {
        if (_bands.Length < 2)
            return;
        // Pen 缓存：渲染热路径不分配，画刷引用变化时才重建
        if (_wavePrimaryPen == null ||
            !ReferenceEquals(_wavePrimaryBrush, PrimaryBrush) ||
            !ReferenceEquals(_waveSecondaryBrush, SecondaryBrush))
        {
            _wavePrimaryBrush = PrimaryBrush;
            _waveSecondaryBrush = SecondaryBrush;
            _wavePrimaryPen = new Pen(PrimaryBrush, 2);
            _waveGlowPen = new Pen(SecondaryBrush ?? PrimaryBrush, 5);
        }
        var primaryPen = _wavePrimaryPen;
        var glowPen = _waveGlowPen!;
        var previous = WavePoint(0);
        for (var i = 1; i < _bands.Length; i++)
        {
            var next = WavePoint(i);
            context.DrawLine(glowPen, previous, next);
            context.DrawLine(primaryPen, previous, next);
            previous = next;
        }
    }

    private void DrawParticles(DrawingContext context)
    {
        var slot = Bounds.Width / _bands.Length;
        for (var i = 0; i < _bands.Length; i++)
        {
            var level = Level(i);
            var radius = 1.5 + level * 3;
            var point = new Point(i * slot + slot / 2, Bounds.Height - 4 - level * Math.Max(0, Bounds.Height - 10));
            context.DrawEllipse(i % 3 == 0 ? SecondaryBrush ?? PrimaryBrush : PrimaryBrush, null, point, radius, radius);
        }
    }

    private void DrawVfd(DrawingContext context)
    {
        var slot = Bounds.Width / _bands.Length;
        var width = Math.Max(2, slot * 0.72);
        var compact = Bounds.Height < 32;
        var segHeight = compact ? 2.0 : 3.0;
        var segGap = compact ? 1.0 : 2.0;
        var step = segHeight + segGap;
        var totalSegments = Math.Max(1, (int)Math.Floor((Bounds.Height + segGap) / step));
        var midBrush = SecondaryBrush ?? PrimaryBrush!;
        var peakBrush = PeakBrush ?? midBrush;

        for (var i = 0; i < _bands.Length; i++)
        {
            var x = i * slot + (slot - width) / 2;
            var level = Level(i);
            var litCount = (int)Math.Round(level * totalSegments);
            if (level > 0.02 && litCount == 0)
                litCount = 1;

            for (var s = 0; s < litCount; s++)
            {
                var ratio = (s + 1) / (double)totalSegments;
                var brush = ratio > 0.85
                    ? peakBrush
                    : ratio > 0.65
                        ? midBrush
                        : PrimaryBrush!;
                var y = Bounds.Height - (s + 1) * step + segGap;
                if (y < 0)
                    break;
                context.DrawRectangle(brush, null, new Rect(x, y, width, segHeight), 1, 1);
            }

            if (i < _peaks.Length && _peaks[i] > 0.04f)
            {
                var peakIdx = Math.Clamp((int)Math.Round(_peaks[i] * totalSegments) - 1, 0, totalSegments - 1);
                if (peakIdx >= litCount)
                {
                    var py = Bounds.Height - (peakIdx + 1) * step + segGap;
                    if (py >= 0)
                        context.DrawRectangle(peakBrush, null, new Rect(x, py, width, segHeight), 1, 1);
                }
            }
        }
    }

    private Point WavePoint(int index)
    {
        var x = _bands.Length == 1 ? 0 : index * Bounds.Width / (_bands.Length - 1);
        var y = Bounds.Height * 0.82 - Level(index) * Bounds.Height * 0.68;
        return new Point(x, y);
    }

    private double Level(int index) => Math.Clamp(_bands[index], 0f, 1f);
}
