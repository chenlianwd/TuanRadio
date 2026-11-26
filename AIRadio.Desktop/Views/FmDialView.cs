using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AIRadio.Desktop.Views;

/// <summary>
/// 模拟 FM 频率刻度尺（88–108 MHz）：纯自绘、不可命中、按需 60fps 指数阻尼插值，
/// 指针收敛或控件隐藏/卸载后自动停帧（静止态 0 CPU 占用）。
/// </summary>
public sealed class FmDialView : Control
{
    public const double MinFrequency = 88.0;
    public const double MaxFrequency = 108.0;
    private const double ConvergenceEpsilon = 0.015;
    private const double DampingFactor = 0.22; // 60fps 下约 450ms 收敛

    public static readonly StyledProperty<double> FrequencyProperty =
        AvaloniaProperty.Register<FmDialView, double>(nameof(Frequency), 98.0);

    public static readonly StyledProperty<string> BandLabelProperty =
        AvaloniaProperty.Register<FmDialView, string>(nameof(BandLabel), string.Empty);

    public static readonly StyledProperty<IBrush?> NeedleBrushProperty =
        AvaloniaProperty.Register<FmDialView, IBrush?>(nameof(NeedleBrush));

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<FmDialView, IBrush?>(nameof(TickBrush));

    private static readonly Typeface ScaleTypeface = new("Consolas");
    private static readonly int[] MajorFrequencies = [88, 92, 96, 100, 104, 108];

    private double _currentFrequency = 98.0;
    private DispatcherTimer? _animTimer;
    private IBrush? _cachedTickBrush;
    private IBrush? _cachedNeedleBrush;
    private Pen? _majorTickPen;
    private Pen? _minorTickPen;
    private Pen? _baselinePen;
    private Pen? _needlePen;
    private Pen? _needleGlowPen;
    private FormattedText[]? _cachedMajorLabels;
    private string? _cachedReadoutString;
    private FormattedText? _cachedReadoutText;

    static FmDialView()
    {
        AffectsRender<FmDialView>(BandLabelProperty, NeedleBrushProperty, TickBrushProperty);
        FrequencyProperty.Changed.AddClassHandler<FmDialView>((view, _) => view.OnFrequencyChanged());
        IsVisibleProperty.Changed.AddClassHandler<FmDialView>((view, _) => view.OnVisibilityChanged());
    }

    public FmDialView()
    {
        IsHitTestVisible = false;
        DetachedFromVisualTree += (_, _) => StopAnimation(snapToTarget: true);
        AttachedToVisualTree += (_, _) =>
        {
            if (Math.Abs(ClampFrequency(Frequency) - _currentFrequency) > ConvergenceEpsilon && IsVisible)
                StartAnimation();
        };
    }

    public double Frequency
    {
        get => GetValue(FrequencyProperty);
        set => SetValue(FrequencyProperty, value);
    }

    public string BandLabel
    {
        get => GetValue(BandLabelProperty);
        set => SetValue(BandLabelProperty, value);
    }

    public IBrush? NeedleBrush
    {
        get => GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /// <summary>测试钩子：观察当前插值频点。</summary>
    internal double CurrentFrequency => _currentFrequency;

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 380 : Math.Min(availableSize.Width, 420);
        var h = double.IsInfinity(availableSize.Height) ? 20 : Math.Min(availableSize.Height, 24);
        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    private static double ClampFrequency(double value)
        => double.IsFinite(value) ? Math.Clamp(value, MinFrequency, MaxFrequency) : 98.0;

    private void OnFrequencyChanged()
    {
        var target = ClampFrequency(Frequency);
        if (!IsVisible || VisualRoot is null || Math.Abs(target - _currentFrequency) <= ConvergenceEpsilon)
        {
            _currentFrequency = target;
            StopAnimation(snapToTarget: false);
            InvalidateVisual();
            return;
        }

        StartAnimation();
    }

    private void OnVisibilityChanged()
    {
        if (!IsVisible)
        {
            StopAnimation(snapToTarget: true);
        }
        else if (Math.Abs(ClampFrequency(Frequency) - _currentFrequency) > ConvergenceEpsilon)
        {
            StartAnimation();
        }
    }

    private void StartAnimation()
    {
        if (_animTimer is null)
        {
            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _animTimer.Tick += OnAnimationTick;
        }
        if (!_animTimer.IsEnabled)
            _animTimer.Start();
    }

    private void StopAnimation(bool snapToTarget)
    {
        if (_animTimer is { IsEnabled: true })
            _animTimer.Stop();
        if (snapToTarget)
            _currentFrequency = ClampFrequency(Frequency);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var target = ClampFrequency(Frequency);
        var diff = target - _currentFrequency;
        if (Math.Abs(diff) <= ConvergenceEpsilon)
        {
            _currentFrequency = target;
            StopAnimation(snapToTarget: false);
        }
        else
        {
            _currentFrequency += diff * DampingFactor;
        }
        InvalidateVisual();
    }

    private void EnsurePens()
    {
        if (!ReferenceEquals(_cachedTickBrush, TickBrush))
        {
            _cachedTickBrush = TickBrush;
            _cachedReadoutString = null;
            _cachedReadoutText = null;
            if (TickBrush is not null)
            {
                _majorTickPen = new Pen(TickBrush, 1.0);
                _minorTickPen = new Pen(TickBrush, 0.75);
                _baselinePen = new Pen(TickBrush, 0.75);
                _cachedMajorLabels = new FormattedText[MajorFrequencies.Length];
                for (var i = 0; i < MajorFrequencies.Length; i++)
                {
                    _cachedMajorLabels[i] = new FormattedText(
                        MajorFrequencies[i].ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        ScaleTypeface,
                        8.5,
                        TickBrush);
                }
            }
            else
            {
                _majorTickPen = null;
                _minorTickPen = null;
                _baselinePen = null;
                _cachedMajorLabels = null;
            }
        }

        if (!ReferenceEquals(_cachedNeedleBrush, NeedleBrush))
        {
            _cachedNeedleBrush = NeedleBrush;
            if (NeedleBrush is not null)
            {
                _needlePen = new Pen(NeedleBrush, 1.5);
                var glowBrush = NeedleBrush is ISolidColorBrush solid
                    ? new SolidColorBrush(Color.FromArgb(72, solid.Color.R, solid.Color.G, solid.Color.B))
                    : NeedleBrush;
                _needleGlowPen = new Pen(glowBrush, 5.0);
            }
            else
            {
                _needlePen = null;
                _needleGlowPen = null;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 100 || height < 12)
            return;

        EnsurePens();
        if (TickBrush is null || NeedleBrush is null)
            return;

        const double padLeft = 6;
        const double padRight = 6;
        // 左侧留出紧凑频段读数区（如 FM 98.7 · NIGHT），使用 InvariantCulture 与状态栏台标一致并缓存复用
        var freqText = _currentFrequency.ToString("0.0", CultureInfo.InvariantCulture);
        var readout = string.IsNullOrWhiteSpace(BandLabel)
            ? $"FM {freqText}"
            : $"FM {freqText} · {BandLabel}";
        if (!string.Equals(_cachedReadoutString, readout, StringComparison.Ordinal) || _cachedReadoutText is null)
        {
            _cachedReadoutString = readout;
            _cachedReadoutText = new FormattedText(
                readout,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                ScaleTypeface,
                9.5,
                TickBrush);
        }
        var readoutText = _cachedReadoutText;

        var scaleLeft = padLeft + Math.Min(118, readoutText.Width + 12);
        var scaleRight = width - padRight;
        if (scaleRight - scaleLeft < 80)
        {
            // 极窄宽度下仅绘制居中标尺
            scaleLeft = padLeft;
        }
        else
        {
            context.DrawText(readoutText, new Point(padLeft, Math.Max(0, (height - readoutText.Height) / 2)));
        }

        var scaleWidth = scaleRight - scaleLeft;
        var baselineY = height - 4;

        // 水平基准微线
        if (_baselinePen is not null)
        {
            using (context.PushOpacity(0.32))
                context.DrawLine(_baselinePen, new Point(scaleLeft, baselineY), new Point(scaleRight, baselineY));
        }

        // 每 1 MHz 一根刻度（88..108 共 21 根），每 4 MHz 为主刻度并标数字
        for (var freq = 88; freq <= 108; freq++)
        {
            var ratio = (freq - MinFrequency) / (MaxFrequency - MinFrequency);
            var x = scaleLeft + ratio * scaleWidth;
            var isMajor = (freq - 88) % 4 == 0;
            var tickTop = isMajor ? baselineY - 6 : baselineY - 3;
            var pen = isMajor ? _majorTickPen : _minorTickPen;
            if (pen is not null)
            {
                using (context.PushOpacity(isMajor ? 0.78 : 0.42))
                    context.DrawLine(pen, new Point(x, tickTop), new Point(x, baselineY));
            }
        }

        // 主刻度数字（88 / 92 / 96 / 100 / 104 / 108，缓存复用零分配）
        if (_cachedMajorLabels is not null)
        {
            for (var i = 0; i < MajorFrequencies.Length; i++)
            {
                var major = MajorFrequencies[i];
                var ratio = (major - MinFrequency) / (MaxFrequency - MinFrequency);
                var x = scaleLeft + ratio * scaleWidth;
                var label = _cachedMajorLabels[i];
                var textX = Math.Clamp(x - label.Width / 2, scaleLeft, scaleRight - label.Width);
                using (context.PushOpacity(0.75))
                    context.DrawText(label, new Point(textX, Math.Max(0, baselineY - 6 - label.Height)));
            }
        }

        // 指针 + 柔和辉光
        var needleRatio = (_currentFrequency - MinFrequency) / (MaxFrequency - MinFrequency);
        var needleX = scaleLeft + Math.Clamp(needleRatio, 0, 1) * scaleWidth;
        if (_needleGlowPen is not null)
            context.DrawLine(_needleGlowPen, new Point(needleX, 1), new Point(needleX, height - 1));
        if (_needlePen is not null)
            context.DrawLine(_needlePen, new Point(needleX, 1), new Point(needleX, height - 1));
    }
}
