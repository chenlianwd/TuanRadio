using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AIRadio.Desktop.Views;

public partial class StarfieldView : UserControl
{
    private readonly List<Star> _stars = new();
    private readonly Random _rng = new();
    private DispatcherTimer? _timer;
    private float[] _spectrum = new float[32];
    private double _canvasW, _canvasH;
    private const int StarCount = 55;
    private bool _initialized;

    public StarfieldView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            _initialized = false;
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _canvasW = Bounds.Width > 10 ? Bounds.Width : 500;
        _canvasH = Bounds.Height > 10 ? Bounds.Height : 168;
        CreateStars();
        StartAnimation();
        _initialized = true;
    }

    public void PushSpectrum(float[] data)
    {
        _spectrum = data;
    }

    private void CreateStars()
    {
        StarCanvas.Children.Clear();
        _stars.Clear();

        for (int i = 0; i < StarCount; i++)
        {
            var ellipse = new Ellipse
            {
                Width = 1.5,
                Height = 1.5,
                Fill = Brushes.White,
                Opacity = 0.15
            };

            var x = _rng.NextDouble() * _canvasW;
            var y = _rng.NextDouble() * _canvasH;
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            StarCanvas.Children.Add(ellipse);

            _stars.Add(new Star
            {
                Ellipse = ellipse,
                X = x,
                Y = y,
                SpeedX = (_rng.NextDouble() - 0.5) * 0.15,
                SpeedY = -0.08 - _rng.NextDouble() * 0.25,
                BaseSize = 1.0 + _rng.NextDouble() * 2.0,
                BaseOpacity = 0.08 + _rng.NextDouble() * 0.2,
                BandIndex = _rng.Next(32),
                Phase = _rng.NextDouble() * Math.PI * 2
            });
        }
    }

    private void StartAnimation()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w > 10) _canvasW = w;
        if (h > 10) _canvasH = h;

        var time = Environment.TickCount64 / 1000.0;

        for (int i = 0; i < _stars.Count; i++)
        {
            var star = _stars[i];

            var bandVal = star.BandIndex < _spectrum.Length ? _spectrum[star.BandIndex] : 0f;
            var pulse = 0.5 + 0.5 * Math.Sin(time * 1.5 + star.Phase);
            var targetOpacity = star.BaseOpacity + bandVal * 0.6 + pulse * 0.05;
            var targetSize = star.BaseSize + bandVal * 3.0;

            star.CurrentOpacity += (targetOpacity - star.CurrentOpacity) * 0.12;
            star.CurrentSize += (targetSize - star.CurrentSize) * 0.1;

            star.X += star.SpeedX + bandVal * 0.3 * Math.Sin(time + star.Phase);
            star.Y += star.SpeedY;

            if (star.Y < -5) { star.Y = _canvasH + 5; star.X = _rng.NextDouble() * _canvasW; }
            if (star.X < -5) star.X = _canvasW + 5;
            if (star.X > _canvasW + 5) star.X = -5;

            var size = Math.Max(0.8, star.CurrentSize);
            star.Ellipse.Width = size;
            star.Ellipse.Height = size;
            star.Ellipse.Opacity = Math.Clamp(star.CurrentOpacity, 0, 0.95);
            Canvas.SetLeft(star.Ellipse, star.X);
            Canvas.SetTop(star.Ellipse, star.Y);
        }
    }

    private class Star
    {
        public Ellipse Ellipse = null!;
        public double X, Y;
        public double SpeedX, SpeedY;
        public double BaseSize, BaseOpacity;
        public int BandIndex;
        public double Phase;
        public double CurrentOpacity = 0.15;
        public double CurrentSize = 1.5;
    }
}
