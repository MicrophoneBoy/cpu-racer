using System.Diagnostics;
using System.Management;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CpuRacer.App.Game;
using Microsoft.Win32;

namespace CpuRacer.App;

public partial class MainWindow : Window
{
    private const int SidebarHistoryLength = 90;
    private const double CpuSampleIntervalSeconds = 0.1;

    private readonly Queue<double> _sidebarCpuHistory = new();
    private readonly DateTime _startTime = DateTime.Now;
    private readonly GameEngine _engine = new();
    private readonly HashSet<Key> _keysDown = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private PerformanceCounter? _cpuCounter;
    private DispatcherTimer? _cpuSampleTimer;
    private double _lastFrameTime;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeStaticInfo();

        DrawGridLines();
        GraphGrid.SizeChanged += (_, _) => DrawGridLines();

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // first call always returns 0, prime it
        }
        catch
        {
            _cpuCounter = null;
        }

        _cpuSampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CpuSampleIntervalSeconds) };
        _cpuSampleTimer.Tick += CpuSampleTimer_Tick;
        _cpuSampleTimer.Start();

        KeyDown += Window_KeyDown;
        KeyUp += Window_KeyUp;
        Focus();

        CompositionTarget.Rendering += OnRendering;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _cpuSampleTimer?.Stop();
        _cpuCounter?.Dispose();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        _keysDown.Add(e.Key);
        if (e.Key == Key.Space)
        {
            if (_engine.State == RunState.GameOver) _engine.Restart();
            else _engine.TryJump();
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e) => _keysDown.Remove(e.Key);

    private void CpuSampleTimer_Tick(object? sender, EventArgs e)
    {
        double cpu = Math.Clamp(_cpuCounter?.NextValue() ?? 0, 0, 100);

        _sidebarCpuHistory.Enqueue(cpu);
        while (_sidebarCpuHistory.Count > SidebarHistoryLength) _sidebarCpuHistory.Dequeue();

        CpuTileValueText.Text = $"{cpu:0}%";
        UtilizationText.Text = $"{cpu:0}%";
        UpTimeText.Text = (DateTime.Now - _startTime).ToString(@"hh\:mm\:ss");

        UpdateSidebarSparkline();
        _engine.SampleCpu(cpu);
    }

    private void UpdateSidebarSparkline()
    {
        if (_sidebarCpuHistory.Count == 0) return;
        var points = new PointCollection();
        int i = 0;
        foreach (double value in _sidebarCpuHistory)
        {
            points.Add(new Point(i, 100 - value));
            i++;
        }
        CpuTileSparkline.Points = points;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastFrameTime, 0.1);
        _lastFrameTime = now;

        bool throttleUp = _keysDown.Contains(Key.Up) || _keysDown.Contains(Key.W);
        bool throttleDown = _keysDown.Contains(Key.Down) || _keysDown.Contains(Key.S);
        int rotateInput = 0;
        if (_keysDown.Contains(Key.Left) || _keysDown.Contains(Key.A)) rotateInput -= 1;
        if (_keysDown.Contains(Key.Right) || _keysDown.Contains(Key.D)) rotateInput += 1;
        bool recover = _keysDown.Contains(Key.R);
        _engine.Update(dt, throttleUp, throttleDown, rotateInput, recover);

        RenderGame();
    }

    private void RenderGame()
    {
        double panelW = GraphGrid.ActualWidth;
        double panelH = GraphGrid.ActualHeight;
        if (panelW <= 0 || panelH <= 0) return;

        const double margin = 20;
        double scale = (panelH - margin * 2) / Track.MaxHeight; // pixels per meter, same for X and Y
        double cameraWorldX = _engine.CameraWorldX - panelW * 0.3 / scale;

        Point ScreenPoint(double worldX, double worldY) =>
            new((worldX - cameraWorldX) * scale, panelH - margin - worldY * scale);

        // Track curve, resampled at a fixed screen-pixel step so it stays smooth regardless
        // of the underlying CPU sample spacing.
        var trackPoints = new PointCollection();
        for (double sx = 0; sx <= panelW; sx += 4)
        {
            double worldX = cameraWorldX + sx / scale;
            double worldY = _engine.Track.HeightAt(worldX);
            trackPoints.Add(new Point(sx, panelH - margin - worldY * scale));
        }
        CpuGraphLine.Points = trackPoints;

        RenderCoins(cameraWorldX, panelW / scale, ScreenPoint);
        RenderCar(ScreenPoint, scale);
        RenderThrottleGauge(panelH);
        UpdateHud();
    }

    private void RenderCoins(double cameraWorldX, double visibleWorldWidth, Func<double, double, Point> screenPoint)
    {
        CoinLayer.Children.Clear();
        foreach (var coin in _engine.CoinMarkers)
        {
            if (coin.Collected) continue;
            if (coin.X < cameraWorldX - 2 || coin.X > cameraWorldX + visibleWorldWidth + 2) continue;

            var p = screenPoint(coin.X, coin.Y);
            var ellipse = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.Gold,
                Stroke = Brushes.DarkGoldenrod,
                StrokeThickness = 1
            };
            Canvas.SetLeft(ellipse, p.X - 7);
            Canvas.SetTop(ellipse, p.Y - 7);
            CoinLayer.Children.Add(ellipse);
        }
    }

    // Drawn straight from the Box2D chassis/wheel bodies (fixed-size box + circles, rotated by the
    // real simulated angle) rather than sampled off the terrain, so the car keeps a constant length
    // and can separate from the ground (jumps, launches off crests) instead of hugging the contour.
    private void RenderCar(Func<double, double, Point> screenPoint, double scale)
    {
        CarLayer.Children.Clear();

        var physics = _engine.Physics;
        Vector2 pos = physics.ChassisPosition;
        float angle = physics.ChassisAngle;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);

        Point LocalToScreen(float lx, float ly) => screenPoint(
            pos.X + lx * cos - ly * sin,
            pos.Y + lx * sin + ly * cos);

        var bodyColor = _engine.State == RunState.Flipped
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));

        var body = new Polygon
        {
            Points = new PointCollection
            {
                LocalToScreen(-CarPhysics.ChassisHalfWidth, -CarPhysics.ChassisHalfHeight),
                LocalToScreen(CarPhysics.ChassisHalfWidth, -CarPhysics.ChassisHalfHeight),
                LocalToScreen(CarPhysics.ChassisHalfWidth, CarPhysics.ChassisHalfHeight),
                LocalToScreen(-CarPhysics.ChassisHalfWidth, CarPhysics.ChassisHalfHeight),
            },
            Fill = bodyColor,
            Stretch = Stretch.None
        };
        CarLayer.Children.Add(body);

        double wheelDiameterPx = CarPhysics.WheelRadius * 2 * scale;
        foreach (var wheelPos in new[] { physics.RearWheelPosition, physics.FrontWheelPosition })
        {
            var p = screenPoint(wheelPos.X, wheelPos.Y);
            var wheel = new Ellipse { Width = wheelDiameterPx, Height = wheelDiameterPx, Fill = Brushes.Black };
            Canvas.SetLeft(wheel, p.X - wheelDiameterPx / 2);
            Canvas.SetTop(wheel, p.Y - wheelDiameterPx / 2);
            CarLayer.Children.Add(wheel);
        }
    }

    // Vertical fader showing the throttle lever position, styled after the original CPURacer's gauge.
    private void RenderThrottleGauge(double panelH)
    {
        ThrottleGauge.Children.Clear();

        const double barX = 36, barWidth = 6, verticalInset = 70;
        double barTop = verticalInset;
        double barBottom = panelH - verticalInset;
        double barHeight = barBottom - barTop;
        if (barHeight <= 0) return;

        var track = new Rectangle
        {
            Width = barWidth,
            Height = barHeight,
            Stroke = (Brush)FindResource("GridLineBrush"),
            StrokeThickness = 1
        };
        Canvas.SetLeft(track, barX);
        Canvas.SetTop(track, barTop);
        ThrottleGauge.Children.Add(track);

        double fraction = Math.Clamp(_engine.ThrottleFraction, 0, 1);
        double fillHeight = fraction * barHeight;
        double fillTop = barBottom - fillHeight;

        var fill = new Rectangle
        {
            Width = barWidth,
            Height = fillHeight,
            Fill = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))
        };
        Canvas.SetLeft(fill, barX);
        Canvas.SetTop(fill, fillTop);
        ThrottleGauge.Children.Add(fill);

        var marker = new Rectangle
        {
            Width = barWidth + 4,
            Height = 5,
            Fill = (Brush)FindResource("AccentBrush")
        };
        Canvas.SetLeft(marker, barX - 2);
        Canvas.SetTop(marker, fillTop - 2.5);
        ThrottleGauge.Children.Add(marker);

        var label = new TextBlock
        {
            Text = $"{_engine.ThrottleLevel * 100:0}%",
            FontSize = 10,
            Foreground = (Brush)FindResource("AccentBrush")
        };
        Canvas.SetLeft(label, barX + barWidth + 5);
        Canvas.SetTop(label, fillTop - 7);
        ThrottleGauge.Children.Add(label);
    }

    private void UpdateHud()
    {
        DistanceHudText.Text = $"{_engine.DistanceMeters:0.0}m  best {_engine.BestDistanceMeters:0.0}m";
        ScoreHudText.Text = $"Score {_engine.Score}  best {_engine.BestScore}";
        StatusHudText.Text = _engine.StatusMessage;
        ScoreText.Text = _engine.Score.ToString();
        CoinsText.Text = _engine.CoinCount.ToString();
        BestScoreText.Text = _engine.BestScore.ToString();
    }

    private void DrawGridLines()
    {
        GridLinesCanvas.Children.Clear();
        double width = GraphGrid.ActualWidth;
        double height = GraphGrid.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var brush = (Brush)FindResource("GridLineBrush");
        const int rows = 4;
        for (int i = 1; i < rows; i++)
        {
            double y = height / rows * i;
            GridLinesCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = 1
            });
        }
    }

    private void InitializeStaticInfo()
    {
        string processorName = "Unknown processor";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            processorName = key?.GetValue("ProcessorNameString") as string ?? processorName;
            if (key?.GetValue("~MHz") is int mhz)
            {
                SpeedText.Text = $"{mhz / 1000.0:0.00} GHz";
            }
        }
        catch
        {
            // Registry access can fail under odd security policies; static info is cosmetic only.
        }

        ProcessorNameText.Text = processorName.Trim();
        LogicalProcessorsText.Text = Environment.ProcessorCount.ToString();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            int cores = 0;
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                cores += Convert.ToInt32(obj["NumberOfCores"]);
            }
            CoresText.Text = cores > 0 ? cores.ToString() : "—";
        }
        catch
        {
            CoresText.Text = "—";
        }
    }
}
