using System.Globalization;
using GarminMcp.Core.Coaching;
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
        ["weight"] = "Körpergewicht über die Zeit. Entscheidend ist der stabile Trend, nicht der einzelne Tag (kurzfristige Schwankungen sind meist Wasser/Glykogen). Starke ungewollte Abnahme kann auf zu hohe Belastung oder Unterversorgung hindeuten.",
        ["marathon"] = "Garmins prognostizierte Marathon-Zeit über die Zeit – niedriger ist schneller. Die grüne Linie ist deine Zielzeit: liegt die Prognose darunter, bist du auf Kurs.",
        ["heatmap"] = "Trainingslast pro Tag der letzten 12 Wochen (Dauer × Intensität) – je dunkler, desto höher. Zeigt auf einen Blick Konstanz, Belastungsblöcke und Ruhetage. Gleichmäßige Muster mit klaren Erholungstagen sind ideal.",
        ["sleepstages"] = "Schlafphasen pro Nacht, gestapelt: Tiefschlaf (körperliche Regeneration), Leichtschlaf, REM (mentale Erholung) und Wachzeit. Viel Tief- und REM-Schlaf bei wenig Wachphasen spricht für erholsamen Schlaf.",
        ["sleepscore"] = "Garmins Schlaf-Score (0–100) bündelt Dauer, Tiefe, Erholung und Unruhe der Nacht zu einem Wert. Konstant hohe Werte sind ein starkes Zeichen guter Regeneration.",
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

        // Designed at-a-glance hero card (rendered at the very top of the dashboard).
        Add("hero", "", f => DrawHeroCard(f, report, today));

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

        // Training-load calendar heatmap (last 12 weeks) — a flagship "consistency at a glance" view.
        var load = DailyLoad(report.Activities);
        if (load.Count > 0)
            Add("heatmap", "🗓️ Trainingslast-Kalender (12 Wochen)", f => DrawHeatmap(f, today, load, 12));

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

        // Sleep stages, stacked (deep / light / REM / awake)
        var deepH = days.Select(d => d.SleepDeepMin is int v ? (double?)Math.Round(v / 60.0, 2) : null).ToList();
        var lightH = days.Select(d => d.SleepLightMin is int v ? (double?)Math.Round(v / 60.0, 2) : null).ToList();
        var remH = days.Select(d => d.SleepRemMin is int v ? (double?)Math.Round(v / 60.0, 2) : null).ToList();
        var awakeH = days.Select(d => d.SleepAwakeMin is int v ? (double?)Math.Round(v / 60.0, 2) : null).ToList();
        if (new[] { deepH, lightH, remH }.Any(s => s.Count(x => x.HasValue) >= 2))
            Add("sleepstages", "🛌 Schlafphasen (h)", f => DrawStacked(f, labels, new[]
            {
                new StackSeg("Tief", "#1f6feb", deepH),
                new StackSeg("Leicht", "#58a6ff", lightH),
                new StackSeg("REM", "#a371f7", remH),
                new StackSeg("Wach", "#d0d7de", awakeH),
            }));
        LineChart(Add, "sleepscore", "😴 Schlaf-Score (0–100)", labels, days.Select(d => d.SleepScore is int v ? (double?)v : null).ToList(), "#30d158");
        BarChart(Add, "steps", "👟 Schritte", labels, days.Select(d => d.Steps is int v ? (double?)v : null).ToList(), "#5e5ce6");
        LineChart(Add, "bodybattery", "🔋 Body Battery (Peak)", labels, days.Select(d => d.BodyBatteryHigh is int v ? (double?)v : null).ToList(), "#34c759");
        BarChart(Add, "stress", "😰 Stress (Ø)", labels, days.Select(d => d.StressAvg is int v ? (double?)v : null).ToList(), "#ff375f");
        LineChart(Add, "vo2max", "🫁 VO₂max", labels, days.Select(d => d.Vo2Max).ToList(), "#bf5af2");
        LineChart(Add, "weight", "⚖️ Gewicht (kg)", labels, days.Select(d => d.WeightKg).ToList(), "#64d2ff");

        // Marathon-prediction trend with the goal time as a reference line.
        var marathon = days.Select(d => d.MarathonSeconds is int s ? (double?)Math.Round(s / 60.0, 1) : null).ToList();
        if (marathon.Count(x => x.HasValue) >= 2)
        {
            (double, string)? goalRef = report.Coaching?.GoalSeconds is int gs ? (gs / 60.0, $"Ziel {gs / 3600}:{(gs % 3600) / 60:00}") : null;
            Add("marathon", "⏱️ Marathon-Prognose (min)", f => Draw(f, labels,
                new[] { new Series("Prognose", "#ff9f0a", marathon, false) }, refLine: goalRef));
        }

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
        (double Lo, double Hi)? band = null, bool zeroLine = false, bool zeroBaseline = false,
        (double Value, string Label)? refLine = null)
    {
        var present = series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return;

        double min = present.Min(), max = present.Max();
        if (band is { } b) { min = Math.Min(min, b.Lo); max = Math.Max(max, b.Hi); }
        if (refLine is { } rl) { min = Math.Min(min, rl.Value); max = Math.Max(max, rl.Value); }
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

        // reference line (e.g. goal time)
        if (refLine is { } rf)
        {
            using var rp = new SKPaint { Color = new SKColor(0x30, 0xD1, 0x58), StrokeWidth = 2, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
            var yr = (float)Y(rf.Value);
            canvas.DrawLine(PadL, yr, W - PadR, yr, rp);
            using var rt = new SKPaint { Color = new SKColor(0x1f, 0x9d, 0x44), TextSize = 15, IsAntialias = true };
            canvas.DrawText(rf.Label, PadL + 4, yr - 5, rt);
        }

        // x labels — evenly spaced, as many as fit without overlapping (bars: centred on the bar)
        if (n > 0)
        {
            var isBar = series.Any(s => s.Bar);
            var slot = plotW / Math.Max(1, n);
            float LabelX(int i) => isBar ? (float)(PadL + i * slot + slot / 2) : (float)X(i);

            // Evenly distribute k labels across [0, n-1] (first & last always shown, equal spacing).
            // Spacing adapts to the widest label so long categorical names (e.g. sport types) don't overlap.
            var maxLblW = labels.Count > 0 ? labels.Max(l => text.MeasureText(l)) : 40f;
            var minSpacing = Math.Max(130.0, maxLblW + 22);
            var k = Math.Max(2, Math.Min(n, (int)Math.Round(plotW / minSpacing)));
            var idx = new List<int>();
            for (var j = 0; j < k; j++) idx.Add((int)Math.Round((double)j * (n - 1) / (k - 1)));

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
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
        data.SaveTo(fs);
    }

    // Consecutive days up to today with at least one logged activity.
    private static int ActivityStreak(IReadOnlyList<ActivitySummary> acts, DateOnly today)
    {
        var set = acts.Select(a => a.Date).ToHashSet(StringComparer.Ordinal);
        var d = today;
        if (!set.Contains(d.ToString("yyyy-MM-dd"))) d = d.AddDays(-1); // today may be unlogged yet
        var n = 0;
        while (set.Contains(d.ToString("yyyy-MM-dd"))) { n++; d = d.AddDays(-1); }
        return n;
    }

    // Per-day training load (TRIMP proxy: duration × HR intensity), summed across activities.
    private static Dictionary<string, double> DailyLoad(IReadOnlyList<ActivitySummary> acts)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in acts)
        {
            if (a.DurationMin is not double min || min <= 0) continue;
            var intensity = a.AverageHr is int hr && hr > 0 ? Math.Clamp(hr / 140.0, 0.6, 2.0) : 1.0;
            d[a.Date] = d.GetValueOrDefault(a.Date) + min * intensity;
        }
        return d;
    }

    private static readonly string[] MonthsDe =
        { "Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" };

    /// <summary>GitHub-style contribution calendar of daily training load (weeks × weekdays).</summary>
    private static void DrawHeatmap(string file, DateOnly today, Dictionary<string, double> load, int weeks)
    {
        const int cell = 26, gap = 5, step = cell + gap;
        const int leftPad = 36, topPad = 24, bottomPad = 30, rightPad = 14;
        var width = leftPad + weeks * step + rightPad;
        var height = topPad + 7 * step + bottomPad;

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-(weeks - 1) * 7);

        double maxLoad = 0;
        for (var i = 0; i < weeks * 7; i++)
        {
            var date = start.AddDays(i);
            if (date > today) continue;
            if (load.TryGetValue(date.ToString("yyyy-MM-dd"), out var v)) maxLoad = Math.Max(maxLoad, v);
        }
        if (maxLoad <= 0) maxLoad = 1;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var empty = new SKColor(0xEB, 0xED, 0xF0);
        SKColor[] scale = { new(0xC6, 0xE8, 0xC0), new(0x7B, 0xC9, 0x6F), new(0x34, 0xC7, 0x59), new(0x1B, 0x7A, 0x30) };

        using var label = new SKPaint { Color = new SKColor(0x88, 0x88, 0x8C), TextSize = 14, IsAntialias = true };

        string[] wd = { "Mo", "", "Mi", "", "Fr", "", "" };
        for (var r = 0; r < 7; r++)
            if (wd[r].Length > 0)
                canvas.DrawText(wd[r], 6, topPad + r * step + cell - 7, label);

        var lastMonth = -1;
        for (var w = 0; w < weeks; w++)
        {
            var colMonday = start.AddDays(w * 7);
            if (colMonday.Month != lastMonth)
            {
                lastMonth = colMonday.Month;
                canvas.DrawText(MonthsDe[colMonday.Month - 1], leftPad + w * step, topPad - 8, label);
            }
            for (var r = 0; r < 7; r++)
            {
                var date = start.AddDays(w * 7 + r);
                if (date > today) continue;
                float x = leftPad + w * step, y = topPad + r * step;
                var col = empty;
                if (load.TryGetValue(date.ToString("yyyy-MM-dd"), out var v) && v > 0)
                {
                    var bucket = (int)Math.Ceiling(v / maxLoad * 4);
                    col = scale[Math.Clamp(bucket - 1, 0, 3)];
                }
                using var p = new SKPaint { Color = col, IsAntialias = true };
                canvas.DrawRoundRect(x, y, cell, cell, 5, 5, p);
            }
        }

        // legend: weniger ▢▢▢▢▢ mehr
        var legendY = (float)(height - 12);
        float lx = leftPad;
        canvas.DrawText("weniger", lx, legendY, label);
        lx += label.MeasureText("weniger") + 8;
        foreach (var c in new[] { empty }.Concat(scale))
        {
            using var sp = new SKPaint { Color = c, IsAntialias = true };
            canvas.DrawRoundRect(lx, legendY - 13, 15, 15, 4, 4, sp);
            lx += 19;
        }
        canvas.DrawText("mehr", lx + 2, legendY, label);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
        data.SaveTo(fs);
    }

    /// <summary>A designed one-glance summary card (no emoji — Linux font safety).</summary>
    private static void DrawHeroCard(string file, GarminReport report, DateOnly today)
    {
        var c = report.Coaching;
        const int w = 900, h = 300;
        var ink = new SKColor(0x1C, 0x1C, 0x1E);
        var sub = new SKColor(0x8A, 0x8A, 0x8E);
        var accent = (c?.Readiness ?? Readiness.Amber) switch
        {
            Readiness.Green => new SKColor(0x30, 0xD1, 0x58),
            Readiness.Red => new SKColor(0xFF, 0x45, 0x3A),
            _ => new SKColor(0xFF, 0x9F, 0x0A),
        };

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var title = new SKPaint { Color = sub, TextSize = 22, IsAntialias = true };
        canvas.DrawText($"HEUTE · {today:dd.MM.yyyy}", 28, 44, title);

        // top-right: streak + race countdown (plain text — no emoji, Linux font safety)
        var streak = ActivityStreak(report.Activities, today);
        var meta = new List<string>();
        if (streak > 0) meta.Add($"Streak {streak} {(streak == 1 ? "Tag" : "Tage")}");
        if (c?.DaysToRace is int dtr && dtr >= 0) meta.Add($"noch {dtr} {(dtr == 1 ? "Tag" : "Tage")} bis zum Ziel");
        if (meta.Count > 0)
        {
            var rt = string.Join("   ·   ", meta);
            canvas.DrawText(rt, w - 28 - title.MeasureText(rt), 44, title);
        }

        // Readiness pill (left)
        const float px = 28, py = 66, pw = 250, ph = 150;
        using (var bg = new SKPaint { Color = accent.WithAlpha(0x20), IsAntialias = true })
            canvas.DrawRoundRect(px, py, pw, ph, 18, 18, bg);
        using (var dot = new SKPaint { Color = accent, IsAntialias = true })
            canvas.DrawCircle(px + 46, py + 52, 30, dot);
        using (var st = new SKPaint { Color = SKColors.White, TextSize = 27, IsAntialias = true, FakeBoldText = true, TextAlign = SKTextAlign.Center })
            canvas.DrawText(c?.ReadinessScore?.ToString() ?? "–", px + 46, py + 62, st);
        using (var lab = new SKPaint { Color = sub, TextSize = 15, IsAntialias = true })
            canvas.DrawText("READINESS", px + 90, py + 40, lab);
        var rec = c is null ? "–" : new string(c.Headline.SkipWhile(ch => !char.IsLetterOrDigit(ch)).ToArray());
        using (var rp = new SKPaint { Color = ink, TextSize = 21, IsAntialias = true, FakeBoldText = true })
        {
            var y = py + 66;
            foreach (var ln in Wrap(rec, rp, pw - 104).Take(3)) { canvas.DrawText(ln, px + 90, y, rp); y += 25; }
        }

        // Stat tiles (3 × 2, right)
        var latest = report.Days.Where(d => d.HasAnyData)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal).FirstOrDefault();
        var (cur, _) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        var tiles = new (string Label, string Value)[]
        {
            ("RUHEPULS", latest?.RestingHeartRate is int r ? $"{r}" : "–"),
            ("HRV", latest?.HrvLastNight is int hv ? $"{hv}" : "–"),
            ("SCHLAF", latest?.SleepHours is double sh ? $"{sh:0.0}h" : "–"),
            ("BODY BATT.", latest?.BodyBatteryHigh is int bb ? $"{bb}" : "–"),
            ("FORM", c?.Tsb is double tsb ? tsb.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "–"),
            ("KM/WOCHE", cur.Km > 0 ? cur.Km.ToString("0.#", CultureInfo.InvariantCulture) : "–"),
        };
        const float gx = 300, gy = 66, gh = 150;
        var gw = w - 28 - gx;
        const int cols = 3, rows = 2;
        var cw = gw / cols; var chh = gh / rows;
        using var tileLab = new SKPaint { Color = sub, TextSize = 15, IsAntialias = true };
        using var tileVal = new SKPaint { Color = ink, TextSize = 34, IsAntialias = true, FakeBoldText = true };
        for (var i = 0; i < tiles.Length; i++)
        {
            var col = i % cols; var row = i / cols;
            var tx = gx + col * cw; var ty = gy + row * chh;
            canvas.DrawText(tiles[i].Label, tx, ty + 22, tileLab);
            canvas.DrawText(tiles[i].Value, tx, ty + 58, tileVal);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
        data.SaveTo(fs);
    }

    private static List<string> Wrap(string text, SKPaint paint, float maxWidth)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' '))
        {
            var t = cur.Length == 0 ? word : cur + " " + word;
            if (paint.MeasureText(t) <= maxWidth) cur = t;
            else { if (cur.Length > 0) lines.Add(cur); cur = word; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private sealed record StackSeg(string Name, string Color, IReadOnlyList<double?> Values);

    /// <summary>Stacked bar chart (e.g. sleep stages per night).</summary>
    private static void DrawStacked(string file, IReadOnlyList<string> labels, IReadOnlyList<StackSeg> segs)
    {
        var n = labels.Count;
        double max = 0;
        for (var i = 0; i < n; i++)
        {
            double t = 0;
            foreach (var s in segs) t += s.Values.Count > i ? s.Values[i] ?? 0 : 0;
            max = Math.Max(max, t);
        }
        if (max <= 0) return;
        max *= 1.08;

        double plotW = W - PadL - PadR, plotH = H - PadT - PadB;
        double Y(double v) => PadT + (1 - v / max) * plotH;
        var slot = plotW / Math.Max(1, n);
        var bw = (float)(slot * 0.66);

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var grid = new SKPaint { Color = new SKColor(0xFF, 0xE3, 0xE3, 0xE6), StrokeWidth = 1, IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(0x88, 0x88, 0x8C), TextSize = 17, IsAntialias = true };

        for (var k = 0; k <= 2; k++)
        {
            var y = (float)(PadT + k / 2.0 * plotH);
            var val = max - k / 2.0 * max;
            canvas.DrawLine(PadL, y, W - PadR, y, grid);
            var s = Round(val);
            canvas.DrawText(s, PadL - 6 - text.MeasureText(s), y + 5, text);
        }

        // x labels — evenly spaced
        if (n > 0)
        {
            var k = Math.Max(2, Math.Min(n, (int)Math.Round(plotW / 130.0)));
            for (var j = 0; j < k; j++)
            {
                var i = (int)Math.Round((double)j * (n - 1) / (k - 1));
                var lbl = labels[i];
                var cx = (float)(PadL + i * slot + slot / 2) - text.MeasureText(lbl) / 2;
                cx = Math.Clamp(cx, PadL, W - PadR - text.MeasureText(lbl));
                canvas.DrawText(lbl, cx, H - 16, text);
            }
        }

        for (var i = 0; i < n; i++)
        {
            double acc = 0;
            var x = (float)(PadL + i * slot + (slot - bw) / 2);
            foreach (var s in segs)
            {
                var v = s.Values.Count > i ? s.Values[i] ?? 0 : 0;
                if (v <= 0) continue;
                var y1 = (float)Y(acc + v);
                var y0 = (float)Y(acc);
                using var p = new SKPaint { Color = SKColor.Parse(s.Color).WithAlpha(0xE6), IsAntialias = true };
                canvas.DrawRect(x, y1, bw, y0 - y1, p);
                acc += v;
            }
        }

        // legend
        using var legendText = new SKPaint { Color = new SKColor(0x55, 0x55, 0x59), TextSize = 16, IsAntialias = true };
        var lx = (float)(W - PadR);
        foreach (var s in Enumerable.Reverse(segs.ToList()))
        {
            var tw = legendText.MeasureText(s.Name);
            lx -= tw;
            canvas.DrawText(s.Name, lx, PadT - 6, legendText);
            lx -= 8;
            using var sw = new SKPaint { Color = SKColor.Parse(s.Color), IsAntialias = true };
            canvas.DrawRect(lx - 14, PadT - 18, 12, 12, sw);
            lx -= 14 + 14;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
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
