using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Dvd360.Core;

namespace Dvd360.App;

public enum DiscViewMode { Empty, Detecting, Idle, Reading, Done }

/// <summary>
/// An animated disc whose surface fills in, ring by ring, as sectors are read: the data spiral of a real
/// CD/DVD runs from the centre outwards, so each sector is drawn where it physically is.
/// </summary>
public sealed class DiscView : Grid
{
    private const int Px = 420;          // bitmap size
    private const int Rings = 56, Segments = 120, Cells = Rings * Segments;
    private const double InnerR = 0.30, OuterR = 0.985; // as a fraction of the radius

    private readonly WriteableBitmap _bitmap = new(Px, Px, 96, 96, PixelFormats.Bgra32, null);
    private readonly int[] _pixelCell = new int[Px * Px];   // -1 = not part of the data area
    private readonly byte[] _pixelShade = new byte[Px * Px];
    private readonly uint[] _pixels = new uint[Px * Px];
    private readonly int[] _cellColor = new int[Cells];
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly Ellipse _emptySlot, _head, _sheen;
    private readonly Line _arm;
    private readonly TextBlock _center = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock _caption = new() { FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB3, 0xC7)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -26), IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private DiscViewMode _mode = (DiscViewMode)(-1);
    private double _headFraction = -1;
    private SectorMap? _lastMap;
    private long _lastLba, _total = 1;
    private readonly bool[] _highlight = new bool[Cells];
    private bool _hasHighlight;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };

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

    private static readonly uint Silver = Bgra(0xC9, 0xD1, 0xDC), Ok = Bgra(0x2E, 0xD5, 0x73), OkLight = Bgra(0x8E, 0xE6, 0xB0),
        Bad = Bgra(0xFF, 0x4D, 0x5E), Retry = Bgra(0xFF, 0xB0, 0x20), Skipped = Bgra(0x4A, 0x52, 0x63), Hub = Bgra(0xE8, 0xEC, 0xF2), HubRing = Bgra(0x9A, 0xA4, 0xB4);

    public DiscView()
    {
        Width = Height = 300;
        PrecomputeGeometry();

        _image.Source = _bitmap;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = new TransformGroup { Children = { _spin, _pop } };

        _emptySlot = new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x6A)), StrokeThickness = 3, StrokeDashArray = [4, 3] };
        _sheen = new Ellipse
        {
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.45),
                 new GradientStop(Color.FromArgb(0x30, 0x9F, 0xD8, 0xFF), 0.62), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.8)],
                new Point(0, 0), new Point(1, 1)),
        };
        _arm = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x60, 0x70, 0x88)), StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _head = new Ellipse
        {
            Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0x5E, 0xC8, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x5E, 0xC8, 0xFF), BlurRadius = 18, ShadowDepth = 0, Opacity = 1 },
        };
        Canvas.SetLeft(_head, 0);
        Canvas.SetTop(_head, 0);
        _overlay.Children.Add(_arm);
        _overlay.Children.Add(_head);

        Children.Add(_emptySlot);
        Children.Add(_image);
        Children.Add(_sheen);
        Children.Add(_overlay);
        Children.Add(_center);
        Children.Add(_caption);
        SizeChanged += (_, _) => PlaceHead(_headFraction < 0 ? 0 : _headFraction, animate: false);
        Background = Brushes.Transparent; // receive mouse events everywhere
        _tip.PlacementTarget = this;
        MouseMove += (_, e) => SectorHovered?.Invoke(SectorAt(e.GetPosition(this)));
        MouseLeave += (_, _) => { SectorHovered?.Invoke(null); HoverContent = null; };
        SetMode(DiscViewMode.Empty, Loc.T("InsertDisc"));
    }

    private static uint Bgra(byte r, byte g, byte b, byte a = 255) => (uint)(a << 24 | r << 16 | g << 8 | b);

    private void PrecomputeGeometry()
    {
        double c = (Px - 1) / 2.0, R = Px / 2.0;
        double r0 = InnerR * R, r1 = OuterR * R;
        for (int y = 0; y < Px; y++)
        for (int x = 0; x < Px; x++)
        {
            int i = y * Px + x;
            double dx = x - c, dy = y - c, r = Math.Sqrt(dx * dx + dy * dy);
            _pixelCell[i] = -1;
            if (r > R) { _pixelShade[i] = 0; continue; }
            if (r < r0)
            {
                _pixelCell[i] = r < 0.11 * R ? -3 : r < 0.13 * R ? -4 : r < 0.24 * R ? -2 : -4; // hole, hub ring, hub, clear ring
                continue;
            }
            if (r > r1) { _pixelCell[i] = -4; continue; }
            // equal area per sector (constant linear velocity): fraction of data area inside radius r
            double f = (r * r - r0 * r0) / (r1 * r1 - r0 * r0);
            double ringPos = f * Rings;
            int ring = Math.Min(Rings - 1, (int)ringPos);
            double ang = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI);
            int seg = Math.Min(Segments - 1, (int)(ang * Segments));
            _pixelCell[i] = ring * Segments + seg;
            double frac = ringPos - ring;
            _pixelShade[i] = (byte)(96 + 4 * Math.Sin(frac * Math.PI)); // soft ring relief, no hard lines (avoids moiré)
        }
    }

    public void SetMode(DiscViewMode mode, string caption)
    {
        _caption.Text = caption;
        if (mode == _mode) return;
        var previous = _mode;
        _mode = mode;
        bool hasDisc = mode is not DiscViewMode.Empty;
        _emptySlot.Visibility = hasDisc ? Visibility.Collapsed : Visibility.Visible;
        _image.Visibility = _sheen.Visibility = hasDisc ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = mode is DiscViewMode.Reading ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDisc) _center.Text = "";

        // Spin: fast while reading, slow when idle, stopped when empty/done.
        double seconds = mode switch { DiscViewMode.Reading => 0.9, DiscViewMode.Detecting => 1.6, DiscViewMode.Idle => 14, _ => 0 };
        if (seconds > 0)
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_spin.Angle, _spin.Angle + 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever, IsCumulative = true });
        else
        {
            double a = _spin.Angle;
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(a, a + 40, TimeSpan.FromSeconds(1.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        // Slide-in "pop" when a disc appears.
        if (hasDisc && previous is DiscViewMode.Empty or (DiscViewMode)(-1))
        {
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
            _pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
        }
        if (mode == DiscViewMode.Empty) Render(null, 0);
    }

    /// <summary>Redraws the surface from the sector map (null = fresh silver disc).</summary>
    public void Render(SectorMap? map, long currentLba)
    {
        _lastMap = map;
        _lastLba = currentLba;
        if (map != null) _total = map.Count;
        Array.Fill(_cellColor, -1);
        double percent = 0;
        if (map != null)
        {
            var raw = map.Raw;
            long n = raw.Length;
            // per cell: bit flags of states present, and how many ok
            var flags = new byte[Cells];
            var okCount = new int[Cells];
            var total = new int[Cells];
            for (long s = 0; s < n; s++)
            {
                int cell = (int)(s * Cells / n);
                byte st = raw[(int)s];
                flags[cell] |= (byte)(1 << st);
                total[cell]++;
                if (st == (byte)SectorState.Ok) okCount[cell]++;
            }
            for (int c = 0; c < Cells; c++)
            {
                byte f = flags[c];
                _cellColor[c] =
                    (f & (1 << (int)SectorState.Bad)) != 0 ? 3 :
                    (f & (1 << (int)SectorState.NeedsRetry)) != 0 ? 2 :
                    f == 1 << (int)SectorState.Ok ? 1 :
                    (f & (1 << (int)SectorState.Ok)) != 0 ? 5 :
                    f == 1 << (int)SectorState.Skipped ? 4 :
                    (f & 1) != 0 ? 0 : 1;
            }
            percent = 100.0 * map.CountOf(SectorState.Ok) / Math.Max(1, n - map.CountOf(SectorState.Skipped));
            _center.Text = $"{Math.Floor(percent * 10) / 10:0.#}%";
            PlaceHead((double)currentLba / Math.Max(1, n), animate: true);
        }
        for (int i = 0; i < _pixels.Length; i++)
        {
            int cell = _pixelCell[i];
            uint color = cell switch
            {
                -1 => 0,
                -3 => 0,
                -2 => Hub,
                -4 => Bgra(0xB4, 0xBC, 0xC8, 0xD0),
                _ => (cell < Cells ? _cellColor[cell] : -1) switch
                {
                    1 => Ok, 2 => Retry, 3 => Bad, 4 => Skipped, 5 => OkLight, _ => Silver,
                },
            };
            if (cell >= 0) color = Shade(color, _hasHighlight && cell < Cells && !_highlight[cell] ? (byte)(_pixelShade[i] * 32 / 100) : _pixelShade[i]);
            _pixels[i] = color;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, Px, Px), _pixels, Px * 4, 0);
    }

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
        if (_mode != DiscViewMode.Empty) Render(_lastMap, _lastLba);
    }

    /// <summary>Which sector is under a point, taking the current spin into account.</summary>
    private long? SectorAt(Point p)
    {
        if (_mode is DiscViewMode.Empty) return null;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return null;
        double R = size / 2 * _pop.ScaleX, r0 = InnerR * R, r1 = OuterR * R;
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

    private static uint Shade(uint c, byte pct)
    {
        if (pct >= 100) return c;
        uint r = (c >> 16 & 0xFF) * pct / 100, g = (c >> 8 & 0xFF) * pct / 100, b = (c & 0xFF) * pct / 100;
        return (c & 0xFF000000) | r << 16 | g << 8 | b;
    }

    private void PlaceHead(double fraction, bool animate)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double R = size / 2, r0 = InnerR * R, r1 = OuterR * R;
        double r = Math.Sqrt(r0 * r0 + fraction * (r1 * r1 - r0 * r0));
        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        // the head sits on an arm coming in from the lower right, like a real drive's sled
        double angle = Math.PI / 4;
        double hx = cx + r * Math.Cos(angle), hy = cy + r * Math.Sin(angle);
        double ax = cx + (R + 26) * Math.Cos(angle), ay = cy + (R + 26) * Math.Sin(angle);
        _arm.X2 = ax; _arm.Y2 = ay;
        var dur = TimeSpan.FromMilliseconds(animate ? 350 : 0);
        _arm.BeginAnimation(Line.X1Property, new DoubleAnimation(hx, dur));
        _arm.BeginAnimation(Line.Y1Property, new DoubleAnimation(hy, dur));
        _head.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(hx - 7, dur));
        _head.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(hy - 7, dur));
        _headFraction = fraction;
    }
}
