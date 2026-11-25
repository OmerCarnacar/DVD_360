using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dvd360.Core;

namespace Dvd360.App;

public enum DiscViewMode { Empty, Detecting, Idle, Reading, Done }

/// <summary>
/// An animated disc whose surface fills in as sectors are read. The data spiral of a real CD/DVD runs from the
/// centre outwards at constant linear velocity, so every sector is drawn where it physically is. Around it, a
/// glowing ring shows progress; a light "laser" rides on the current position; freshly read sectors flash.
/// </summary>
public sealed class DiscView : Grid
{
    private const int Px = 440;                              // surface bitmap size
    private const int Rings = 60, Segments = 128, Cells = Rings * Segments;
    private const double InnerR = 0.30, OuterR = 0.985;      // data area, as a fraction of the disc radius
    private const double Inset = 18;                         // room for the progress ring

    // surface
    private readonly WriteableBitmap _bitmap = new(Px, Px, 96, 96, PixelFormats.Pbgra32, null);
    private readonly int[] _pixelCell = new int[Px * Px];    // >=0 data cell, -1 outside, -2 hub, -3 hole, -4 clear ring, -5 hub edge
    private readonly byte[] _pixelShade = new byte[Px * Px];
    private readonly uint[] _pixels = new uint[Px * Px];
    private readonly int[] _cellColor = new int[Cells], _prevColor = new int[Cells];
    private readonly float[] _flash = new float[Cells];
    private readonly bool[] _highlight = new bool[Cells];
    private bool _hasHighlight;

    // visuals
    private readonly Image _surface = new() { Stretch = Stretch.Fill };
    private readonly Image _rainbow = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Grid _disc = new();
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly Ellipse _emptySlot, _track;
    private readonly Path _ring = new() { StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly DropShadowEffect _ringGlow = new() { Color = Color.FromRgb(0x2E, 0xD5, 0x73), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Line _beam = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Ellipse _head, _ripple;
    private readonly TextBlock _center = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock _caption = new() { FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB3, 0xC7)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -26), IsHitTestVisible = false };
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private DiscViewMode _mode = (DiscViewMode)(-1);
    private double _headFraction = -1;
    private SectorMap? _lastMap;
    private long _lastLba, _total = 1;
    private bool _hasPrev;

    private static readonly uint Silver = Rgb(0xCB, 0xD3, 0xDE), Ok = Rgb(0x2E, 0xD5, 0x73), OkLight = Rgb(0x8E, 0xE6, 0xB0),
        Bad = Rgb(0xFF, 0x4D, 0x5E), Retry = Rgb(0xFF, 0xB0, 0x20), Skipped = Rgb(0x3A, 0x42, 0x52),
        Hub = Rgb(0x1C, 0x24, 0x34), HubEdge = Rgb(0x3A, 0x47, 0x60), Flash = Rgb(0xE6, 0xFF, 0xF4);

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DiscView), new PropertyMetadata(0.0, (d, _) => ((DiscView)d).UpdateRing()));

    public static readonly DependencyProperty ShownPercentProperty = DependencyProperty.Register(
        nameof(ShownPercent), typeof(double), typeof(DiscView), new PropertyMetadata(0.0, (d, e) =>
        {
            var v = (DiscView)d;
            if (v._mode is not DiscViewMode.Empty && v._lastMap != null) v._center.Text = $"{Math.Floor((double)e.NewValue * 10) / 10:0.#}%";
        }));

    /// <summary>0..1, drawn by the glowing ring (animated).</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    /// <summary>Number in the middle (animated count-up).</summary>
    public double ShownPercent { get => (double)GetValue(ShownPercentProperty); set => SetValue(ShownPercentProperty, value); }

    /// <summary>Raised while the mouse moves over the data area: the first sector under the pointer (or null).</summary>
    public event Action<long?>? SectorHovered;

    /// <summary>Total sectors on the disc, so positions can be mapped without a sector map.</summary>
    public long TotalSectors { get => _total; set => _total = Math.Max(1, value); }

    /// <summary>Text shown in the hover tooltip; set it from <see cref="SectorHovered"/>.</summary>
    public object? HoverContent
    {
        get => _tip.Content;
        set { _tip.Content = value; _tip.IsOpen = value != null; }
    }

    public DiscView()
    {
        Width = Height = 300;
        Background = Brushes.Transparent; // receive mouse events everywhere
        PrecomputeGeometry();

        // --- the disc (rotates) + light reflection (does not rotate, like real light)
        _surface.Source = _bitmap;
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        _surface.RenderTransformOrigin = new Point(0.5, 0.5);
        _surface.RenderTransform = _spin;
        _rainbow.Source = BuildRainbow();
        RenderOptions.SetBitmapScalingMode(_rainbow, BitmapScalingMode.HighQuality);
        _disc.Margin = new Thickness(Inset);
        _disc.Children.Add(_surface);
        _disc.Children.Add(_rainbow);
        _disc.RenderTransformOrigin = new Point(0.5, 0.5);
        _disc.RenderTransform = _pop;
        _disc.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.55 };

        // --- progress ring
        _track = new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x1C, 0x26, 0x38)), StrokeThickness = 6, Margin = new Thickness(3), IsHitTestVisible = false };
        _ring.Stroke = new LinearGradientBrush(Color.FromRgb(0x5E, 0xC8, 0xFF), Color.FromRgb(0x2E, 0xD5, 0x73), new Point(0, 0), new Point(1, 1));
        _ring.Effect = _ringGlow;

        // --- laser head: beam, pulsing dot, ripple
        _beam.Stroke = new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0x00, 0x5E, 0xC8, 0xFF), 0), new GradientStop(Color.FromArgb(0xE0, 0x5E, 0xC8, 0xFF), 1)],
            new Point(1, 1), new Point(0, 0));
        _head = new Ellipse
        {
            Width = 12, Height = 12, Fill = new RadialGradientBrush(Colors.White, Color.FromRgb(0x5E, 0xC8, 0xFF)),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x5E, 0xC8, 0xFF), BlurRadius = 20, ShadowDepth = 0, Opacity = 1 },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        _ripple = new Ellipse { Width = 12, Height = 12, Stroke = new SolidColorBrush(Color.FromRgb(0x5E, 0xC8, 0xFF)), StrokeThickness = 1.5, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        foreach (var el in new UIElement[] { _head, _ripple }) { Canvas.SetLeft(el, 0); Canvas.SetTop(el, 0); }
        _beam.X1 = _beam.Y1 = _beam.X2 = _beam.Y2 = 0;
        _overlay.Children.Add(_beam);
        _overlay.Children.Add(_ripple);
        _overlay.Children.Add(_head);
        StartHeadAnimations();

        // --- empty slot
        _emptySlot = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x6A)), StrokeThickness = 2.5, StrokeDashArray = [3, 3],
            Margin = new Thickness(Inset), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
        };
        _emptySlot.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(30)) { RepeatBehavior = RepeatBehavior.Forever });

        Children.Add(_emptySlot);
        Children.Add(_track);
        Children.Add(_ring);
        Children.Add(_disc);
        Children.Add(_overlay);
        Children.Add(_center);
        Children.Add(_caption);

        SizeChanged += (_, _) => { UpdateRing(); PlaceHead(_headFraction < 0 ? 0 : _headFraction, animate: false); };
        _tip.PlacementTarget = this;
        MouseMove += (_, e) => SectorHovered?.Invoke(SectorAt(e.GetPosition(this)));
        MouseLeave += (_, _) => { SectorHovered?.Invoke(null); HoverContent = null; };
        _flashTimer.Tick += (_, _) => FlashTick();
        SetMode(DiscViewMode.Empty, Loc.T("InsertDisc"));
    }

    private static uint Rgb(byte r, byte g, byte b) => 0xFF000000u | (uint)(r << 16 | g << 8 | b);

    // ------------------------------------------------------------------ geometry

    private void PrecomputeGeometry()
    {
        double c = (Px - 1) / 2.0, R = Px / 2.0, r0 = InnerR * R, r1 = OuterR * R;
        for (int y = 0; y < Px; y++)
        for (int x = 0; x < Px; x++)
        {
            int i = y * Px + x;
            double dx = x - c, dy = y - c, r = Math.Sqrt(dx * dx + dy * dy);
            if (r > R) { _pixelCell[i] = -1; continue; }
            if (r < r0)
            {
                _pixelCell[i] = r < 0.105 * R ? -3 : r < 0.125 * R ? -5 : r < 0.235 * R ? -2 : r < 0.25 * R ? -5 : -4;
                _pixelShade[i] = (byte)(r < 0.235 * R ? 100 - 25 * (r / (0.235 * R)) : 100);
                continue;
            }
            if (r > r1) { _pixelCell[i] = -4; _pixelShade[i] = 100; continue; }
            double f = (r * r - r0 * r0) / (r1 * r1 - r0 * r0);  // equal area per sector (constant linear velocity)
            double ringPos = f * Rings;
            int ring = Math.Min(Rings - 1, (int)ringPos);
            double ang = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI);
            int seg = Math.Min(Segments - 1, (int)(ang * Segments));
            _pixelCell[i] = ring * Segments + seg;
            // soft radial brightening towards the rim + a faint ring relief
            _pixelShade[i] = (byte)(86 + 12 * (r / R) + 2 * Math.Sin((ringPos - ring) * Math.PI));
        }
    }

    /// <summary>A non-rotating rainbow sheen, like light diffracting on the data tracks of a real disc.</summary>
    private static WriteableBitmap BuildRainbow()
    {
        var bmp = new WriteableBitmap(Px, Px, 96, 96, PixelFormats.Pbgra32, null);
        var px = new uint[Px * Px];
        double c = (Px - 1) / 2.0, R = Px / 2.0;
        for (int y = 0; y < Px; y++)
        for (int x = 0; x < Px; x++)
        {
            double dx = x - c, dy = y - c, r = Math.Sqrt(dx * dx + dy * dy) / R;
            if (r > 0.995 || r < 0.27) continue;
            double a = Math.Atan2(dy, dx);
            // two bright bands opposite each other, colours shifting along the radius
            double band = Math.Pow(Math.Abs(Math.Cos(a - 0.9)), 6);
            double hue = (a / (2 * Math.PI) * 2 + r * 0.9 + 10) % 1.0;
            var (rr, gg, bb) = Hsv(hue, 0.55, 1.0);
            double alpha = (0.05 + 0.30 * band) * Math.Min(1, (r - 0.27) * 8) * Math.Min(1, (0.995 - r) * 40);
            byte A = (byte)(alpha * 255);
            px[y * Px + x] = (uint)(A << 24 | (byte)(rr * A) << 16 | (byte)(gg * A) << 8 | (byte)(bb * A));
        }
        bmp.WritePixels(new Int32Rect(0, 0, Px, Px), px, Px * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private static (double R, double G, double B) Hsv(double h, double s, double v)
    {
        double i = Math.Floor(h * 6), f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return ((int)i % 6) switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    // ------------------------------------------------------------------ modes

    public void SetMode(DiscViewMode mode, string caption)
    {
        _caption.Text = caption;
        if (mode == _mode) return;
        var previous = _mode;
        _mode = mode;
        bool hasDisc = mode is not DiscViewMode.Empty;
        _emptySlot.Visibility = hasDisc ? Visibility.Collapsed : Visibility.Visible;
        _disc.Visibility = _track.Visibility = _ring.Visibility = hasDisc ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = mode is DiscViewMode.Reading ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDisc) { _center.Text = ""; _hasPrev = false; }

        // spin: fast while reading, gentle when idle, coasting to a stop otherwise
        double seconds = mode switch { DiscViewMode.Reading => 0.8, DiscViewMode.Detecting => 1.4, DiscViewMode.Idle => 16, _ => 0 };
        double a = _spin.Angle;
        if (seconds > 0)
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(a, a + 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever, IsCumulative = true });
        else
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(a, a + 120, TimeSpan.FromSeconds(1.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // disc slides in when it appears
        if (hasDisc && previous is DiscViewMode.Empty or (DiscViewMode)(-1))
        {
            var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
            _pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            _disc.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)));
        }

        // ring glow: breathe while reading, celebrate when done
        _ringGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, mode switch
        {
            DiscViewMode.Reading => new DoubleAnimation(10, 22, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() },
            DiscViewMode.Done => new DoubleAnimation(16, 40, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) },
            _ => null,
        });
        if (mode == DiscViewMode.Empty) { Progress = 0; Render(null, 0); }
    }

    private void StartHeadAnimations()
    {
        var pulse = new DoubleAnimation(0.8, 1.25, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        var hs = (ScaleTransform)_head.RenderTransform;
        hs.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        hs.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        var grow = new DoubleAnimation(1, 3.4, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var rs = (ScaleTransform)_ripple.RenderTransform;
        rs.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        rs.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Redraws the surface from the sector map (null = fresh silver disc).</summary>
    public void Render(SectorMap? map, long currentLba)
    {
        _lastMap = map;
        _lastLba = currentLba;
        if (map != null) _total = map.Count;
        Array.Fill(_cellColor, 0);
        if (map != null)
        {
            var raw = map.Raw;
            long n = raw.Length;
            var flags = new byte[Cells];
            for (long s = 0; s < n; s++) flags[(int)(s * Cells / n)] |= (byte)(1 << raw[(int)s]);
            for (int c = 0; c < Cells; c++)
            {
                byte f = flags[c];
                _cellColor[c] =
                    (f & (1 << (int)SectorState.Bad)) != 0 ? 3 :
                    (f & (1 << (int)SectorState.NeedsRetry)) != 0 ? 2 :
                    (f & ~(1 << (int)SectorState.Ok | 1 << (int)SectorState.Skipped)) == 0 && (f & (1 << (int)SectorState.Ok)) != 0 ? 1 :
                    f == 1 << (int)SectorState.Skipped ? 4 :
                    (f & (1 << (int)SectorState.Ok)) != 0 ? 5 : 0;
                // freshly read: flash
                if (_hasPrev && _cellColor[c] is 1 or 5 && _prevColor[c] is 0 or 2) _flash[c] = 1f;
            }
            Array.Copy(_cellColor, _prevColor, Cells);
            _hasPrev = true;

            long useful = Math.Max(1, n - map.CountOf(SectorState.Skipped));
            double pct = 100.0 * map.CountOf(SectorState.Ok) / useful;
            double done = (double)(n - map.CountOf(SectorState.Untried) - map.CountOf(SectorState.NeedsRetry)) / Math.Max(1, n);
            BeginAnimation(ShownPercentProperty, new DoubleAnimation(pct, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
            BeginAnimation(ProgressProperty, new DoubleAnimation(done, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
            PlaceHead((double)currentLba / Math.Max(1, n), animate: true);
        }
        else
        {
            _hasPrev = false;
            Array.Clear(_flash);
            BeginAnimation(ProgressProperty, null);
            Progress = 0;
            _center.Text = "";
        }
        Compose();
        if (_flash.Any(f => f > 0) && !_flashTimer.IsEnabled) _flashTimer.Start();
    }

    private void FlashTick()
    {
        bool any = false;
        for (int c = 0; c < Cells; c++)
        {
            if (_flash[c] <= 0) continue;
            _flash[c] = Math.Max(0, _flash[c] - 0.09f);
            any |= _flash[c] > 0;
        }
        Compose();
        if (!any) _flashTimer.Stop();
    }

    private void Compose()
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            int cell = _pixelCell[i];
            uint color;
            switch (cell)
            {
                case -1: case -3: color = 0; break;
                case -2: color = Shade(Hub, _pixelShade[i]); break;
                case -5: color = HubEdge; break;
                case -4: color = 0x60B4BCC8; break; // clear plastic ring (premultiplied below)
                default:
                    color = _cellColor[cell] switch { 1 => Ok, 2 => Retry, 3 => Bad, 4 => Skipped, 5 => OkLight, _ => Silver };
                    float fl = _flash[cell];
                    if (fl > 0) color = Lerp(color, Flash, fl * 0.85f);
                    byte shade = _pixelShade[i];
                    if (_hasHighlight && !_highlight[cell]) shade = (byte)(shade * 30 / 100);
                    color = Shade(color, shade);
                    break;
            }
            _pixels[i] = Premultiply(color);
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, Px, Px), _pixels, Px * 4, 0);
    }

    private static uint Premultiply(uint c)
    {
        uint a = c >> 24;
        if (a == 255) return c;
        return a << 24 | ((c >> 16 & 0xFF) * a / 255) << 16 | ((c >> 8 & 0xFF) * a / 255) << 8 | (c & 0xFF) * a / 255;
    }

    private static uint Lerp(uint a, uint b, float t)
    {
        uint Ch(int s) => (uint)((a >> s & 0xFF) + ((int)(b >> s & 0xFF) - (int)(a >> s & 0xFF)) * t) & 0xFF;
        return 0xFF000000u | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    private static uint Shade(uint c, byte pct)
    {
        if (pct >= 100) return c;
        uint r = (c >> 16 & 0xFF) * pct / 100, g = (c >> 8 & 0xFF) * pct / 100, b = (c & 0xFF) * pct / 100;
        return (c & 0xFF000000) | r << 16 | g << 8 | b;
    }

    private void UpdateRing()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double rr = Math.Min(w, h) / 2 - 6, cx = w / 2, cy = h / 2;
        double p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.0005) { _ring.Data = null; return; }
        if (p >= 0.9995) { _ring.Data = new EllipseGeometry(new Point(cx, cy), rr, rr); return; }
        double a = p * 2 * Math.PI;
        var start = new Point(cx, cy - rr);
        var end = new Point(cx + rr * Math.Sin(a), cy - rr * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(rr, rr), 0, p > 0.5, SweepDirection.Clockwise, true));
        _ring.Data = new PathGeometry([fig]);
    }

    // ------------------------------------------------------------------ highlight & hit testing

    /// <summary>Lights up the given sector ranges and dims the rest of the disc (empty = no highlight).</summary>
    public void SetHighlight(IEnumerable<(long Start, long Count)> ranges)
    {
        Array.Clear(_highlight);
        _hasHighlight = false;
        foreach (var (start, count) in ranges)
        {
            if (count <= 0) continue;
            int first = (int)Math.Clamp(start * Cells / _total, 0, Cells - 1);
            int last = (int)Math.Clamp((start + count - 1) * Cells / _total, 0, Cells - 1);
            for (int c = first; c <= last; c++) _highlight[c] = true;
            _hasHighlight = true;
        }
        if (_mode != DiscViewMode.Empty) Compose();
    }

    private double DiscRadius => Math.Min(ActualWidth, ActualHeight) / 2 - Inset;

    /// <summary>Which sector is under a point, taking the current spin into account.</summary>
    private long? SectorAt(Point p)
    {
        if (_mode is DiscViewMode.Empty) return null;
        double R = DiscRadius * _pop.ScaleX;
        if (R <= 0) return null;
        double r0 = InnerR * R, r1 = OuterR * R;
        double dx = p.X - ActualWidth / 2, dy = p.Y - ActualHeight / 2, r = Math.Sqrt(dx * dx + dy * dy);
        if (r < r0 || r > r1) return null;
        double f = (r * r - r0 * r0) / (r1 * r1 - r0 * r0);
        int ring = Math.Min(Rings - 1, (int)(f * Rings));
        double ang = (Math.Atan2(dy, dx) - _spin.Angle * Math.PI / 180 + Math.PI) / (2 * Math.PI);
        ang -= Math.Floor(ang);
        int seg = Math.Min(Segments - 1, (int)(ang * Segments));
        long cell = ring * Segments + seg;
        return cell * _total / Cells;
    }

    private void PlaceHead(double fraction, bool animate)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double R = DiscRadius;
        if (R <= 0) return;
        double r0 = InnerR * R, r1 = OuterR * R;
        double r = Math.Sqrt(r0 * r0 + fraction * (r1 * r1 - r0 * r0));
        double cx = ActualWidth / 2, cy = ActualHeight / 2, angle = Math.PI / 4; // sled comes in from the lower right
        double hx = cx + r * Math.Cos(angle), hy = cy + r * Math.Sin(angle);
        double ex = cx + (R + Inset + 10) * Math.Cos(angle), ey = cy + (R + Inset + 10) * Math.Sin(angle);
        _beam.X2 = ex; _beam.Y2 = ey;
        var dur = TimeSpan.FromMilliseconds(animate ? 450 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _beam.BeginAnimation(Line.X1Property, new DoubleAnimation(hx, dur) { EasingFunction = ease });
        _beam.BeginAnimation(Line.Y1Property, new DoubleAnimation(hy, dur) { EasingFunction = ease });
        foreach (var el in new FrameworkElement[] { _head, _ripple })
        {
            el.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(hx - el.Width / 2, dur) { EasingFunction = ease });
            el.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(hy - el.Height / 2, dur) { EasingFunction = ease });
        }
        _headFraction = fraction;
    }
}
