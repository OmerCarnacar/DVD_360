namespace Dvd360.Core;

/// <summary>Language for messages coming from the engine (the app switches it).</summary>
public static class CoreText
{
    public static bool Turkish { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string L(string tr, string en) => Turkish ? tr : en;
}
