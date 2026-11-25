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
    private const int Px = 768;                              // only for the soft rainbow sheen
    private const int Rings = 72, Segments = 160, Cells = Rings * Segments;
    private const double InnerR = 0.30, OuterR = 0.985;      // data area, as a fraction of the disc radius
    private const double Inset = 18;                         // room for the progress ring

    // surface (drawn as vector geometry, sharp at any size and DPI)
    private readonly int[] _cellColor = new int[Cells], _prevColor = new int[Cells];
    private readonly float[] _flash = new float[Cells];
    private readonly bool[] _highlight = new bool[Cells];
    private bool _hasHighlight;

    // visuals
    private readonly VectorSurface _surface;
    private readonly Image _rainbow = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Grid _disc = new();
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly Ellipse _emptySlot, _track, _errTrack;
    private readonly Path _ring = new() { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly Path _errRing = new() { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly TextBlock _centerErr = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x7A)), HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly DropShadowEffect _ringGlow = new() { Color = Color.FromRgb(0x2E, 0xD5, 0x73), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Line _beam = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Ellipse _head, _ripple;
    private readonly TextBlock _center = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock _caption = new() { FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB3, 0xC7)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -26), IsHitTestVisible = false };
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private DiscViewMode _mode = (DiscViewMode)(-1);
    private double _headFraction = -1;
    private SectorMap? _lastMap;
    private long _lastLba, _total = 1;
    private bool _hasPrev;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DiscView), new PropertyMetadata(0.0, (d, _) => ((DiscView)d).UpdateRing()));

    public static readonly DependencyProperty ShownPercentProperty = DependencyProperty.Register(
        nameof(ShownPercent), typeof(double), typeof(DiscView), new PropertyMetadata(0.0, (d, e) =>
        {
            var v = (DiscView)d;
            if (v._mode is not DiscViewMode.Empty && v._lastMap != null) v._center.Text = Loc.T("PctShort", Math.Floor((double)e.NewValue * 10) / 10);
        }));

    public static readonly DependencyProperty ErrorProgressProperty = DependencyProperty.Register(
        nameof(ErrorProgress), typeof(double), typeof(DiscView), new PropertyMetadata(0.0, (d, _) => ((DiscView)d).UpdateRing()));

    /// <summary>0..1 share of the disc that failed to read, drawn by the inner red ring.</summary>
    public double ErrorProgress { get => (double)GetValue(ErrorProgressProperty); set => SetValue(ErrorProgressProperty, value); }

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
        // --- the disc (rotates) + light reflection (does not rotate, like real light)
        _surface = new VectorSurface(this);
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
        _track = new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x1C, 0x26, 0x38)), StrokeThickness = 5, Margin = new Thickness(3.5), IsHitTestVisible = false };
        _errTrack = new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x1A, 0x22)), StrokeThickness = 5, Margin = new Thickness(10.5), IsHitTestVisible = false };
        _errRing.Stroke = new LinearGradientBrush(Color.FromRgb(0xFF, 0x4D, 0x5E), Color.FromRgb(0xFF, 0x8A, 0x6B), new Point(0, 0), new Point(1, 1));
        _errRing.Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x4D, 0x5E), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.85 };
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
        Children.Add(_errTrack);
        Children.Add(_errRing);
        Children.Add(_disc);
        Children.Add(_overlay);
        Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Children = { _center, _centerErr } });
        Children.Add(_caption);

        SizeChanged += (_, _) => { UpdateRing(); PlaceHead(_headFraction < 0 ? 0 : _headFraction, animate: false); };
        _tip.PlacementTarget = this;
        MouseMove += (_, e) => SectorHovered?.Invoke(SectorAt(e.GetPosition(this)));
        MouseLeave += (_, _) => { SectorHovered?.Invoke(null); HoverContent = null; };
        _flashTimer.Tick += (_, _) => FlashTick();
        SetMode(DiscViewMode.Empty, Loc.T("InsertDisc"));
    }

    // ------------------------------------------------------------------ geometry

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
        _disc.Visibility = _track.Visibility = _ring.Visibility = _errTrack.Visibility = _errRing.Visibility = hasDisc ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = mode is DiscViewMode.Reading ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDisc) { _center.Text = _centerErr.Text = ""; _hasPrev = false; }

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
        if (mode == DiscViewMode.Empty) { Progress = 0; ErrorProgress = 0; Render(null, 0); }
        Compose();
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
            double errShare = (double)(map.CountOf(SectorState.Bad) + map.CountOf(SectorState.NeedsRetry)) / useful;
            BeginAnimation(ShownPercentProperty, new DoubleAnimation(pct, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
            BeginAnimation(ProgressProperty, new DoubleAnimation(pct / 100, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
            BeginAnimation(ErrorProgressProperty, new DoubleAnimation(errShare, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
            _centerErr.Text = errShare > 0 ? Loc.T("ErrShort", errShare * 100) : "";
            PlaceHead((double)currentLba / Math.Max(1, n), animate: true);
        }
        else
        {
            _hasPrev = false;
            Array.Clear(_flash);
            BeginAnimation(ProgressProperty, null);
            BeginAnimation(ErrorProgressProperty, null);
            Progress = 0;
            ErrorProgress = 0;
            _center.Text = _centerErr.Text = "";
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

    private void Compose() => _surface.InvalidateVisual();

    private static readonly Color CSilver = Color.FromRgb(0xCB, 0xD3, 0xDE), COk = Color.FromRgb(0x2E, 0xD5, 0x73), COkLight = Color.FromRgb(0x8E, 0xE6, 0xB0),
        CBad = Color.FromRgb(0xB3, 0x12, 0x2E), CRetry = Color.FromRgb(0xFF, 0x4D, 0x5E), CSkipped = Color.FromRgb(0x3A, 0x42, 0x52),
        CFlash = Color.FromRgb(0xE6, 0xFF, 0xF4);

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Dim(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    /// <summary>
    /// Draws the disc as vector shapes: every run of equal cells along a ring becomes one annular sector, and all
    /// sectors of one colour share a single geometry, so a full redraw is a handful of DrawGeometry calls.
    /// </summary>
    private void DrawSurface(DrawingContext dc, Size size)
    {
        double R = Math.Min(size.Width, size.Height) / 2;
        if (R <= 0 || _mode == DiscViewMode.Empty) return;
        var c = new Point(size.Width / 2, size.Height / 2);
        double r0 = InnerR * R, r1 = OuterR * R;

        // clear polycarbonate rim and inner clear ring
        var clear = new SolidColorBrush(Color.FromArgb(0x55, 0xB4, 0xBC, 0xC8));
        dc.DrawGeometry(clear, null, Annulus(c, r1, R));
        dc.DrawGeometry(clear, null, Annulus(c, 0.25 * R, r0));

        // data area: group runs by colour
        var groups = new Dictionary<Color, StreamGeometry>();
        var contexts = new Dictionary<Color, StreamGeometryContext>();
        StreamGeometryContext Ctx(Color col)
        {
            if (!contexts.TryGetValue(col, out var g))
            {
                var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
                groups[col] = geo;
                g = contexts[col] = geo.Open();
            }
            return g;
        }
        double area = r1 * r1 - r0 * r0;
        for (int ring = 0; ring < Rings; ring++)
        {
            double ri = Math.Sqrt(r0 * r0 + area * ring / Rings);
            double ro = Math.Sqrt(r0 * r0 + area * (ring + 1) / Rings);
            int seg = 0;
            while (seg < Segments)
            {
                int cell = ring * Segments + seg;
                var col = CellColor(cell);
                int end = seg + 1;
                while (end < Segments && CellColor(ring * Segments + end) == col) end++;
                AddSector(Ctx(col), c, ri, ro, seg, end);
                seg = end;
            }
        }
        foreach (var (col, ctx) in contexts) ctx.Close();
        foreach (var (col, geo) in groups)
        {
            geo.Freeze();
            var b = new SolidColorBrush(col);
            b.Freeze();
            dc.DrawGeometry(b, null, geo);
        }

        // depth: darker towards the centre, plus faint track grooves
        var depth = new RadialGradientBrush(
            [new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 0.30), new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0)]) { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5) };
        dc.DrawGeometry(depth, null, Annulus(c, r0, r1));
        var groove = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0, 0, 0)), Math.Max(0.4, R / 600));
        for (int ring = 6; ring < Rings; ring += 6)
            dc.DrawEllipse(null, groove, c, Math.Sqrt(r0 * r0 + area * ring / Rings), Math.Sqrt(r0 * r0 + area * ring / Rings));

        // hub: dark glass with a bright edge, and the hole in the middle
        var hub = new RadialGradientBrush(Color.FromRgb(0x2A, 0x34, 0x48), Color.FromRgb(0x12, 0x18, 0x24));
        dc.DrawGeometry(hub, new Pen(new SolidColorBrush(Color.FromRgb(0x46, 0x55, 0x72)), Math.Max(1, R / 140)), Annulus(c, 0.105 * R, 0.235 * R));
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), Math.Max(0.6, R / 300)), c, R - 0.5, R - 0.5);
    }

    private Color CellColor(int cell)
    {
        var col = _cellColor[cell] switch { 1 => COk, 2 => CRetry, 3 => CBad, 4 => CSkipped, 5 => COkLight, _ => CSilver };
        float fl = _flash[cell];
        if (fl > 0) col = Mix(col, CFlash, Math.Round(fl * 8) / 8 * 0.85); // quantised so runs stay long
        if (_hasHighlight && !_highlight[cell]) col = Dim(col, 0.3);
        return col;
    }

    private static Geometry Annulus(Point c, double inner, double outer)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new EllipseGeometry(c, outer, outer));
        g.Children.Add(new EllipseGeometry(c, inner, inner));
        g.Freeze();
        return g;
    }

    /// <summary>Annular sector for segments [from, to) of a ring; angle 0 matches Math.Atan2 + π (as used for hit testing).</summary>
    private static void AddSector(StreamGeometryContext g, Point c, double ri, double ro, int from, int to)
    {
        double a0 = -Math.PI + 2 * Math.PI * from / Segments, a1 = -Math.PI + 2 * Math.PI * to / Segments;
        if (to - from == Segments) // a whole ring: two half arcs (an arc cannot end where it starts)
        {
            AddSector(g, c, ri, ro, from, from + Segments / 2);
            AddSector(g, c, ri, ro, from + Segments / 2, to);
            return;
        }
        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        bool large = a1 - a0 > Math.PI;
        g.BeginFigure(P(ro, a0), true, true);
        g.ArcTo(P(ro, a1), new Size(ro, ro), 0, large, SweepDirection.Clockwise, false, false);
        g.LineTo(P(ri, a1), false, false);
        g.ArcTo(P(ri, a0), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, false, false);
    }

    private sealed class VectorSurface(DiscView owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.DrawSurface(dc, RenderSize);
    }

    private void UpdateRing()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _ring.Data = Arc(w, h, Math.Min(w, h) / 2 - 6, Progress);
        _errRing.Data = Arc(w, h, Math.Min(w, h) / 2 - 13, ErrorProgress);
    }

    private static Geometry? Arc(double w, double h, double rr, double fraction)
    {
        double cx = w / 2, cy = h / 2, p = Math.Clamp(fraction, 0, 1);
        if (p <= 0.0005) return null;
        if (p >= 0.9995) return new EllipseGeometry(new Point(cx, cy), rr, rr);
        double a = p * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(cx, cy - rr), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(cx + rr * Math.Sin(a), cy - rr * Math.Cos(a)), new Size(rr, rr), 0, p > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
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
