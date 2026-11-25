using System.Net;
using System.Reflection;
using System.Text;
using Dvd360.Core.Iso9660;

namespace Dvd360.Core;

/// <summary>A self-contained HTML page describing a rescue: disc, fill and error area, a sector map and every file.</summary>
public static class RescueReport
{
    public static void WriteHtml(string path, DiscInfo info, SectorMap map, IsoFileSystem? fs, string title)
    {
        string L(string tr, string en) => CoreText.L(tr, en);
        static string H(string s) => WebUtility.HtmlEncode(s);
        long total = map.Count, skipped = map.CountOf(SectorState.Skipped), useful = Math.Max(1, total - skipped);
        long ok = map.CountOf(SectorState.Ok), retry = map.CountOf(SectorState.NeedsRetry), bad = map.CountOf(SectorState.Bad);
        long unread = map.CountOf(SectorState.Untried);
        double fill = 100.0 * ok / useful, err = 100.0 * (retry + bad) / useful, rest = Math.Max(0, 100 - fill - err);
        string P(double v) => CoreText.Turkish ? $"%{v:N1}" : $"{v:N1}%";
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

        var sb = new StringBuilder();
        sb.Append($$"""
<!doctype html>
<html lang="{{(CoreText.Turkish ? "tr" : "en")}}"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{H(title)}}</title>
<style>
:root{--bg:#0e131d;--card:#161d2b;--line:#232c3e;--text:#e8ecf4;--muted:#8c97ab;--ok:#2ed573;--err:#ff4d5e;--dead:#b3122e;--wait:#c9d1dc}
@media (prefers-color-scheme: light){:root{--bg:#f6f7fa;--card:#fff;--line:#e3e6ee;--text:#151a24;--muted:#5d677a;--wait:#c3cad6} }
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
main{max-width:960px;margin:0 auto;padding:28px 16px}
h1{font-size:26px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 10px}
.muted{color:var(--muted)}.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px;margin:12px 0}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.big{font-size:26px;font-weight:600}.ok{color:var(--ok)}.err{color:var(--err)}
.bar{display:flex;height:14px;border-radius:7px;overflow:hidden;background:var(--line)}
.map{display:grid;grid-template-columns:repeat(100,1fr);gap:1px}.map i{display:block;aspect-ratio:1}
table{width:100%;border-collapse:collapse;font-size:14px}td,th{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left}
td.n{text-align:right;white-space:nowrap}.legend span{margin-right:16px}.dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:5px}
</style></head><body><main>
<h1>{{H(title)}}</h1>
<div class="muted">{{H(L("Kurtarma raporu", "Rescue report"))}} · {{DateTime.Now:g}} · DVD 360 {{version}}</div>
<div class="card grid">
<div><div class="muted">{{L("Doluluk", "Filled")}}</div><div class="big ok">{{P(fill)}}</div></div>
<div><div class="muted">{{L("Hatalı alan", "Error area")}}</div><div class="big err">{{P(err)}}</div></div>
<div><div class="muted">{{L("Okunmamış", "Unread")}}</div><div class="big">{{P(rest)}}</div></div>
<div><div class="muted">{{L("Disk", "Disc")}}</div><div>{{H(info.MediaType)}} · {{H(info.VolumeLabel)}}<br><span class="muted">{{total:N0}} {{L("sektör", "sectors")}}</span></div></div>
</div>
<div class="bar"><div style="width:{{fill.ToString(System.Globalization.CultureInfo.InvariantCulture)}}%;background:var(--ok)"></div><div style="width:{{err.ToString(System.Globalization.CultureInfo.InvariantCulture)}}%;background:var(--err)"></div></div>
<h2>{{L("Disk haritası", "Disc map")}}</h2>
<div class="muted">{{L("Diskin başından sonuna; her kare yaklaşık ", "From the start of the disc to the end; each square is about ")}}{{Math.Max(1, total / 2000):N0}} {{L("sektör", "sectors")}}.</div>
<div class="card"><div class="map">
""");
        const int cells = 2000;
        var raw = map.Raw;
        for (int c = 0; c < cells; c++)
        {
            long a = c * total / cells, b = Math.Max(a + 1, (c + 1) * total / cells);
            bool anyBad = false, anyRetry = false, anyUnread = false, anyOk = false;
            for (long i = a; i < b && i < total; i++)
            {
                switch ((SectorState)raw[(int)i])
                {
                    case SectorState.Bad: anyBad = true; break;
                    case SectorState.NeedsRetry: anyRetry = true; break;
                    case SectorState.Untried: anyUnread = true; break;
                    case SectorState.Ok: anyOk = true; break;
                }
            }
            string color = anyBad ? "var(--dead)" : anyRetry ? "var(--err)" : anyUnread ? "var(--wait)" : anyOk ? "var(--ok)" : "var(--line)";
            sb.Append($"<i style=\"background:{color}\"></i>");
        }
        sb.Append($"""
</div><div class="legend muted" style="margin-top:10px">
<span><i class="dot" style="background:var(--ok)"></i>{L("dolu", "filled")}</span>
<span><i class="dot" style="background:var(--err)"></i>{L("hatalı", "errors")}</span>
<span><i class="dot" style="background:var(--dead)"></i>{L("kalıcı bozuk", "permanently bad")}</span>
<span><i class="dot" style="background:var(--wait)"></i>{L("okunmadı", "unread")}</span>
</div></div>
""");

        if (fs != null)
        {
            var files = fs.Files.ToList();
            int damaged = 0;
            var rows = new StringBuilder();
            foreach (var f in files)
            {
                var t = map.Summarize(f.Extent, f.SectorCount);
                long n = Math.Max(1, t.Ok + t.Bad + t.Retry + t.Pending);
                long errors = t.Bad + t.Retry;
                if (errors + t.Pending > 0) damaged++;
                string state = errors + t.Pending == 0 ? $"<span class=\"ok\">{L("Sağlam", "Intact")}</span>"
                    : errors > 0 ? $"<span class=\"err\">{errors:N0} {L("hatalı", "errors")}</span>" : L("eksik", "incomplete");
                rows.Append($"<tr><td>{H(f.FullPath.Replace(f.Name, f.OutputName))}</td><td class=\"n\">{SizeText(f.DataSize)}</td><td class=\"n\">{P(f.SectorCount == 0 ? 100 : 100.0 * t.Ok / n)}</td><td>{state}</td></tr>");
            }
            sb.Append($"""
<h2>{L("Dosyalar", "Files")} <span class="muted">({files.Count:N0}, {L("sorunlu", "with problems")}: {damaged:N0})</span></h2>
<div class="card"><table><tr><th>{L("Dosya", "File")}</th><th class="n">{L("Boyut", "Size")}</th><th class="n">{L("Dolu", "Filled")}</th><th>{L("Durum", "State")}</th></tr>
{rows}</table></div>
""");
        }
        sb.Append($"""
<p class="muted">{L("Hatalı sektör kaldıysa: diski yumuşak bir bezle ortadan dışa doğru silin, okuma hızını düşürün ve \"Bozukları tekrar dene\"ye basın. Başka bir sürücü de deneyebilirsiniz.",
                    "If errors remain: wipe the disc from the centre outwards with a soft cloth, lower the read speed and press \"Retry bad sectors\". Another drive may also help.")}</p>
<p class="muted">DVD 360 — <a href="https://github.com/OmerCarnacar/DVD_360" style="color:inherit">github.com/OmerCarnacar/DVD_360</a></p>
</main></body></html>
""");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string SizeText(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:N1} {u[i]}";
    }
}
