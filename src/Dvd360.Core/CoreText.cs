namespace Dvd360.Core;

/// <summary>Language for messages coming from the engine (the app switches it).</summary>
public static class CoreText
{
    public static bool Turkish { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string L(string tr, string en) => Turkish ? tr : en;

    /// <summary>"19 yıl 7 ay önce • 7.164 gün" — how long ago something was written.</summary>
    public static string Age(DateTime when, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now);
        if (when > today) return L("gelecek tarih", "future date");
        int days = (int)(today.Date - when.Date).TotalDays;
        int months = (today.Year - when.Year) * 12 + today.Month - when.Month - (today.Day < when.Day ? 1 : 0);
        int years = months / 12; months %= 12;
        string span = years > 0
            ? (months > 0 ? L($"{years} yıl {months} ay", $"{years} y {months} mo") : L($"{years} yıl", $"{years} years"))
            : months > 0 ? L($"{months} ay", $"{months} months")
            : days == 0 ? L("bugün", "today") : L($"{days} gün", $"{days} days");
        if (days == 0) return span;
        return L($"{span} önce • {days:N0} gün", $"{span} ago • {days:N0} days");
    }
}
