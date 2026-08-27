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

    private float[] _bands = Array.Empty<float>();
    private IBrush? _wavePrimaryBrush;
    private IBrush? _waveSecondaryBrush;
    private Pen? _wavePrimaryPen;
    private Pen? _waveGlowPen;

    static SpectrumRenderer()
    {
        AffectsRender<SpectrumRenderer>(StyleNameProperty, PrimaryBrushProperty, SecondaryBrushProperty);
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

    public void SetBands(float[] data)
    {
        if (_bands.Length != data.Length)
            _bands = new float[data.Length];
        Array.Copy(data, _bands, data.Length);
        InvalidateVisual();
    }

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

    private Point WavePoint(int index)
    {
        var x = _bands.Length == 1 ? 0 : index * Bounds.Width / (_bands.Length - 1);
        var y = Bounds.Height * 0.82 - Level(index) * Bounds.Height * 0.68;
        return new Point(x, y);
    }

    private double Level(int index) => Math.Clamp(_bands[index], 0f, 1f);
}
