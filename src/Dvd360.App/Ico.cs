using System.Windows;

namespace Dvd360.App;

/// <summary>
/// Attached icon for buttons: <c>local:Ico.Glyph="&#xE768;"</c> shows a vector glyph from Segoe Fluent Icons
/// (Segoe MDL2 Assets on Windows 10) before the button text. Fonts are vectors, so icons stay sharp at any scale.
/// </summary>
public static class Ico
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ico), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static string? GetGlyph(DependencyObject d) => (string?)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string? value) => d.SetValue(GlyphProperty, value);
}
