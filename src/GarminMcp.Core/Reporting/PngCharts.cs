using System.Globalization;
using SkiaSharp;

namespace GarminMcp.Core.Reporting;

/// <summary>A generated chart image to reference from the dashboard, with a short caption explaining it.</summary>
public sealed record ChartRef(string File, string Title, string Caption = "");

/// <summary>
/// Renders dashboard charts as PNG images (via SkiaSharp, headless) so they display in
/// the GitHub mobile app, which does not render Mermaid. Images are committed into the
/// private repo — no external chart service, so health data never leaves it.
/// </summary>
public static class PngCharts
{
    private const int W = 900, H = 340, PadL = 64, PadR = 24, PadT = 22, PadB = 46;

    /// <summary>Short German explanation per chart (what it says + how to read it).
    /// Fact-checked from a sports-science perspective.</summary>
    private static readonly Dictionary<string, string> Captions = new()
    {
        ["form"] = "Fitness (CTL, blau) ist deine über 42 Tage aufgebaute Belastbarkeit, Fatigue (ATL, orange) die kurzfristige Ermüdung (7 Tage). Liegt Fatigue länger über Fitness, staut sich Müdigkeit; deutlich darunter bist du erholt und formstark.",
        ["tsb"] = "Form (TSB) = Fitness (CTL) minus Fatigue (ATL). Über 0: erholt und wettkampfbereit. Unter 0: ermüdet durch Training. Stark negativ heißt Überlastungsrisiko, dauerhaft hoch positiv Formverlust.",
        ["readiness"] = "Garmins Training Readiness (0–100) bündelt Schlaf, HRV, Erholungszeit und akute Belastung zu einem Bereitschaftswert. Hoch = grünes Licht für Intensität, niedrig = lieber locker oder Ruhetag.",
        ["rhr"] = "Morgendlicher Ruhepuls: ein tendenziell sinkender Wert spricht für bessere Fitness und Erholung. Ein Anstieg klar über den 7-Tage-Schnitt (hell) deutet auf Stress, beginnende Krankheit oder unvollständige Erholung hin.",
        ["hrv"] = "Nächtliche Herzfrequenzvariabilität (ms) als Erholungsmaß. Höhere, stabile Werte zeigen gute Erholung. Fällt sie deutlich unter deinen 7-Tage-Schnitt (hell), braucht dein Körper mehr Regeneration.",
        ["sleep"] = "Schlafdauer pro Nacht. Ziel meist 7–9 Stunden: höhere Balken sind besser. Schlaf ist der stärkste einzelne Hebel für Erholung, HRV und Leistungsfähigkeit.",
        ["steps"] = "Tägliche Schritte als Maß für Alltagsaktivität neben dem Training. Hohe Werte an Ruhetagen zeigen versteckte Belastung, sehr niedrige deuten auf zu wenig Bewegung.",
        ["bodybattery"] = "Body Battery (Tageshoch, 0–100): Garmins Energiespeicher aus HRV, Stress und Aktivität. Hohe Peaks = gut erholt; niedrige Höchstwerte deuten auf unzureichende Regeneration hin.",
        ["stress"] = "Durchschnittlicher Stresslevel (0–100), aus der HRV über den Tag. Niedrig heißt entspanntes Nervensystem; dauerhaft hohe Werte bremsen deine Erholung.",
        ["vo2max"] = "Geschätzte VO₂max (maximale Sauerstoffaufnahme) – dein wichtigster Ausdauer-Fitnessmarker. Sie steigt langsam mit konsequentem Training; ein Aufwärtstrend zeigt wachsende aerobe Leistungsfähigkeit, ein Abfall mögliche Ermüdung oder Formverlust.",
        ["acwr"] = "Verhältnis akuter (7 Tage) zu chronischer (28 Tage) Belastung. Der grüne Bereich 0,8–1,3 ist optimal; weit darüber (>1,5) steigt das Verletzungsrisiko, darunter verlierst du Form.",
        ["bedtime"] = "Zubettgeh-Uhrzeit über die Zeit. Konstante Zeiten (flache Linie) stabilisieren deinen Rhythmus und verbessern Erholung sowie HRV; stark schwankende Zeiten stören den Schlaf.",
        ["weeklykm"] = "Wöchentliche Laufkilometer als Balken. Achte auf gleichmäßigen Aufbau (Faustregel: max. ca. 10 % mehr pro Woche) mit regelmäßigen Entlastungswochen. Zu steile Sprünge erhöhen das Verletzungsrisiko.",
        ["typesplit"] = "Wochenkilometer aufgeteilt nach Sportart. Zeigt, wie ausgewogen dein Training auf Laufen, Rad und Co. verteilt ist – ein hoher Laufanteil bedeutet viel Laufumfang im Verhältnis zu anderen Sportarten.",
    };

    public static List<ChartRef> Generate(GarminReport report, DateOnly today, string outDir)
    {
        var dir = Path.Combine(outDir, "charts");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.png"))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }

        var refs = new List<ChartRef>();

        var days = report.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        if (days.Count > 28) days = days.Skip(days.Count - 28).ToList();
        var labels = days.Select(d => Short(d.Date)).ToList();

        void Add(string id, string title, Action<string> draw)
        {
            var file = Path.Combine(dir, id + ".png");
            draw(file);
            if (File.Exists(file))
                refs.Add(new ChartRef($"charts/{id}.png", title, Captions.GetValueOrDefault(id, "")));
        }

        // Form / Performance-Management-Chart
        var (pmc, _, _, _) = LoadModel.Compute(report.Activities, today, 28);
        if (pmc.Count >= 2)
        {
            var pl = pmc.Select(p => Short(p.Date)).ToList();
            Add("form", "🏋️ Form: Fitness vs. Fatigue", f => Draw(f, pl, new[]
            {
                new Series("Fitness", "#0a84ff", pmc.Select(p => (double?)p.Ctl).ToList(), false),
                new Series("Fatigue", "#ff9f0a", pmc.Select(p => (double?)p.Atl).ToList(), false),
            }));
            Add("tsb", "🎚️ Form (TSB)", f => Draw(f, pl, new[]
            {
                new Series("TSB", "#f5b301", pmc.Select(p => (double?)p.Tsb).ToList(), false),
            }, zeroLine: true));
        }

        LineChart(Add, "readiness", "🎯 Readiness", labels, days.Select(d => d.ReadinessScore is int r ? (double?)r : null).ToList(), "#5856d6");

        var rhr = days.Select(d => d.RestingHeartRate is int v ? (double?)v : null).ToList();
        Add("rhr", "❤️ Ruhepuls + Ø7T (bpm)", f => Draw(f, labels, new[]
        {
            new Series("Wert", "#ff5a5f", rhr, false),
            new Series("Ø7T", "#ffb3b5", Rolling7(rhr), false),
        }));

        var hrv = days.Select(d => d.HrvLastNight is int v ? (double?)v : null).ToList();
        Add("hrv", "💓 HRV + Ø7T (ms)", f => Draw(f, labels, new[]
        {
            new Series("Wert", "#0a84ff", hrv, false),
            new Series("Ø7T", "#a9d2ff", Rolling7(hrv), false),
        }));

        BarChart(Add, "sleep", "😴 Schlaf (h)", labels, days.Select(d => d.SleepHours).ToList(), "#30d158");
        BarChart(Add, "steps", "👟 Schritte", labels, days.Select(d => d.Steps is int v ? (double?)v : null).ToList(), "#5e5ce6");
        LineChart(Add, "bodybattery", "🔋 Body Battery (Peak)", labels, days.Select(d => d.BodyBatteryHigh is int v ? (double?)v : null).ToList(), "#34c759");
        BarChart(Add, "stress", "😰 Stress (Ø)", labels, days.Select(d => d.StressAvg is int v ? (double?)v : null).ToList(), "#ff375f");
        LineChart(Add, "vo2max", "🫁 VO₂max", labels, days.Select(d => d.Vo2Max).ToList(), "#bf5af2");

        var acwr = days.Select(d => d.Acwr).ToList();
        if (acwr.Count(x => x.HasValue) >= 2)
            Add("acwr", "📊 ACWR (Ziel 0,8–1,3)", f => Draw(f, labels, new[] { new Series("ACWR", "#5e9eff", acwr, false) }, band: (0.8, 1.3)));

        LineChart(Add, "bedtime", "🛏️ Zubettgeh-Zeit (Std.)", labels, days.Select(d => d.BedtimeHour).ToList(), "#5ac8fa");

        var (weekLabels, weekValues) = WeeklyKm(report.Activities);
        BarChart(Add, "weeklykm", "🏃 Wochenkilometer (km)", weekLabels, weekValues, "#ff9f0a");

        var (cur, _) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.KmByType.Count > 0)
        {
            var tl = cur.KmByType.OrderByDescending(kv => kv.Value).ToList();
            BarChart(Add, "typesplit", "🥧 Diese Woche nach Sportart (km)",
                tl.Select(kv => kv.Key).ToList(), tl.Select(kv => (double?)kv.Value).ToList(), "#af52de");
        }

        return refs;
    }

    private static void LineChart(Action<string, string, Action<string>> add, string id, string title,
        IReadOnlyList<string> labels, IReadOnlyList<double?> values, string color)
    {
        if (values.Count(v => v.HasValue) < 2) return;
        add(id, title, f => Draw(f, labels, new[] { new Series("", color, values, false) }));
    }

    private static void BarChart(Action<string, string, Action<string>> add, string id, string title,
        IReadOnlyList<string> labels, IReadOnlyList<double?> values, string color)
    {
        if (values.Count(v => v.HasValue) < 1) return;
        add(id, title, f => Draw(f, labels, new[] { new Series("", color, values, true) }, zeroBaseline: true));
    }

    private sealed record Series(string Name, string Color, IReadOnlyList<double?> Values, bool Bar);

    private static void Draw(string file, IReadOnlyList<string> labels, IReadOnlyList<Series> series,
        (double Lo, double Hi)? band = null, bool zeroLine = false, bool zeroBaseline = false)
    {
        var present = series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return;

        double min = present.Min(), max = present.Max();
        if (band is { } b) { min = Math.Min(min, b.Lo); max = Math.Max(max, b.Hi); }
        if (zeroBaseline) min = Math.Min(min, 0);
        if (zeroLine) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
        if (max <= min) { max = min + 1; }
        var padRange = (max - min) * 0.08;
        max += padRange;
        // Bars grow from the zero baseline — never pad below it (avoids nonsensical negative axis labels).
        if (!zeroBaseline) min -= padRange;

        var n = labels.Count;
        double plotW = W - PadL - PadR, plotH = H - PadT - PadB;
        double X(int i) => n <= 1 ? PadL + plotW / 2 : PadL + (double)i / (n - 1) * plotW;
        double Y(double v) => PadT + (1 - (v - min) / (max - min)) * plotH;

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var grid = new SKPaint { Color = new SKColor(0xFF, 0xE3, 0xE3, 0xE6), StrokeWidth = 1, IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(0x88, 0x88, 0x8C), TextSize = 17, IsAntialias = true };

        // gridlines + y labels
        for (var k = 0; k <= 2; k++)
        {
            var y = (float)(PadT + k / 2.0 * plotH);
            var val = max - k / 2.0 * (max - min);
            canvas.DrawLine(PadL, y, W - PadR, y, grid);
            var s = Round(val);
            canvas.DrawText(s, PadL - 6 - text.MeasureText(s), y + 5, text);
        }

        // band
        if (band is { } bd)
        {
            using var bandPaint = new SKPaint { Color = new SKColor(0x30, 0xD1, 0x58, 0x26), IsAntialias = true };
            var yHi = (float)Y(bd.Hi);
            var yLo = (float)Y(bd.Lo);
            canvas.DrawRect(PadL, yHi, (float)plotW, yLo - yHi, bandPaint);
        }

        // zero line
        if (zeroLine && min < 0 && max > 0)
        {
            using var zp = new SKPaint { Color = new SKColor(0xAA, 0xAA, 0xAA), StrokeWidth = 1, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0) };
            var y0 = (float)Y(0);
            canvas.DrawLine(PadL, y0, W - PadR, y0, zp);
        }

        // x labels — evenly spaced, as many as fit without overlapping (bars: centred on the bar)
        if (n > 0)
        {
            var isBar = series.Any(s => s.Bar);
            var slot = plotW / Math.Max(1, n);
            float LabelX(int i) => isBar ? (float)(PadL + i * slot + slot / 2) : (float)X(i);

            var maxLabels = Math.Max(2, (int)(plotW / 120)); // ~120px per label keeps them readable
            var step = (int)Math.Ceiling((double)n / maxLabels);
            var idx = new List<int>();
            for (var i = 0; i < n; i += step) idx.Add(i);
            if (idx.Count == 0 || idx[^1] != n - 1) idx.Add(n - 1);

            foreach (var i in idx.Distinct())
            {
                var lbl = labels[i];
                var cx = LabelX(i) - text.MeasureText(lbl) / 2;
                cx = Math.Clamp(cx, PadL, W - PadR - text.MeasureText(lbl)); // stay inside the plot
                canvas.DrawText(lbl, cx, H - 16, text);
            }
        }

        // series
        foreach (var ser in series)
        {
            var col = SKColor.Parse(ser.Color);
            if (ser.Bar)
            {
                using var fill = new SKPaint { Color = col.WithAlpha(0xDD), IsAntialias = true };
                var slot = plotW / Math.Max(1, n);
                var bw = (float)(slot * 0.66);
                var baseY = (float)Y(Math.Max(0, min));
                for (var i = 0; i < ser.Values.Count; i++)
                {
                    if (ser.Values[i] is not double v) continue;
                    var x = (float)(PadL + i * slot + (slot - bw) / 2);
                    var y = (float)Y(v);
                    canvas.DrawRect(x, Math.Min(y, baseY), bw, Math.Abs(baseY - y), fill);
                }
            }
            else
            {
                using var stroke = new SKPaint { Color = col, StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
                using var path = new SKPath();
                var started = false;
                for (var i = 0; i < ser.Values.Count; i++)
                {
                    if (ser.Values[i] is not double v) continue;
                    var px = (float)X(i);
                    var py = (float)Y(v);
                    if (!started) { path.MoveTo(px, py); started = true; }
                    else path.LineTo(px, py);
                }
                canvas.DrawPath(path, stroke);
            }
        }

        // legend (only when multiple named series)
        var named = series.Where(s => !string.IsNullOrEmpty(s.Name)).ToList();
        if (named.Count > 1)
        {
            using var legendText = new SKPaint { Color = new SKColor(0x55, 0x55, 0x59), TextSize = 16, IsAntialias = true };
            var lx = (float)(W - PadR);
            foreach (var ser in Enumerable.Reverse(named))
            {
                var tw = legendText.MeasureText(ser.Name);
                lx -= tw;
                canvas.DrawText(ser.Name, lx, PadT - 6, legendText);
                lx -= 8;
                using var sw = new SKPaint { Color = SKColor.Parse(ser.Color), IsAntialias = true };
                canvas.DrawRect(lx - 14, PadT - 18, 12, 12, sw);
                lx -= 14 + 14;
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.OpenWrite(file);
        data.SaveTo(fs);
    }

    private static List<double?> Rolling7(IReadOnlyList<double?> values)
    {
        var result = new List<double?>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var window = new List<double>();
            for (var j = Math.Max(0, i - 6); j <= i; j++)
                if (values[j] is double v) window.Add(v);
            result.Add(window.Count > 0 ? Math.Round(window.Average(), 1) : null);
        }
        return result;
    }

    private static (List<string> Labels, List<double?> Values) WeeklyKm(IReadOnlyList<ActivitySummary> activities)
    {
        var byWeek = new Dictionary<int, double>();
        foreach (var a in activities)
        {
            if (a.DistanceKm is not double km || km <= 0) continue;
            if (!DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            var dt = d.ToDateTime(TimeOnly.MinValue);
            var key = ISOWeek.GetYear(dt) * 100 + ISOWeek.GetWeekOfYear(dt);
            byWeek[key] = byWeek.GetValueOrDefault(key) + km;
        }
        var ordered = byWeek.OrderBy(kv => kv.Key).ToList();
        if (ordered.Count > 12) ordered = ordered.Skip(ordered.Count - 12).ToList();
        return (
            ordered.Select(kv => "KW" + (kv.Key % 100).ToString("00")).ToList(),
            ordered.Select(kv => (double?)Math.Round(kv.Value, 1)).ToList());
    }

    private static string Short(string isoDate) => isoDate.Length >= 10 ? isoDate[5..] : isoDate;

    private static string Round(double v) =>
        (Math.Abs(v) >= 100 ? Math.Round(v) : Math.Round(v, 1)).ToString(CultureInfo.InvariantCulture);
}
