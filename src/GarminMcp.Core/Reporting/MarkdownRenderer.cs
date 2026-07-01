using System.Globalization;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>Renders the report as a phone-friendly Markdown dashboard (GitHub mobile app).</summary>
public static class MarkdownRenderer
{
    public static string Render(GarminReport report, int showDays = 14, IReadOnlyList<ChartRef>? charts = null)
    {
        var today = report.Coaching is { } cc && DateOnly.TryParse(cc.Date, out var d0)
            ? d0
            : report.Days.Count > 0 && DateOnly.TryParse(report.Days.Max(x => x.Date), out var d1)
                ? d1
                : DateOnly.FromDateTime(report.GeneratedAtUtc.UtcDateTime);

        var days = report.Days
            .OrderByDescending(x => x.Date, StringComparer.Ordinal)
            .Take(showDays)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 🏃 Garmin Dashboard");
        sb.AppendLine();
        sb.AppendLine($"_Aktualisiert: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine();

        var hero = charts?.FirstOrDefault(c => c.File.EndsWith("hero.png", StringComparison.Ordinal));
        if (hero is not null)
        {
            sb.AppendLine($"![Übersicht]({hero.File})");
            sb.AppendLine();
        }

        AppendStaleness(sb, report, today);
        if (hero is null) AppendKpiBar(sb, report, days, today); // the hero card already shows the glance metrics
        AppendAlerts(sb, report.Alerts);
        AppendCoaching(sb, report);
        AppendRaceCountdown(sb, report.Coaching);
        AppendTodaysWorkout(sb, report.Coaching);
        AppendUpcoming(sb, report.Coaching, today);
        AppendPaceZones(sb, report.Coaching?.Paces);
        AppendLatestDay(sb, days);
        AppendWeek(sb, report, today);
        AppendWeeklyReview(sb, report, today);
        AppendTrends(sb, report, today);
        AppendSeasonBests(sb, report.PersonalBests);
        AppendChartImages(sb, charts);
        AppendDetails(sb, report, days);

        return sb.ToString();
    }

    // ---- Glance bar ----------------------------------------------------------
    private static void AppendKpiBar(StringBuilder sb, GarminReport report, List<DayMetrics> days, DateOnly today)
    {
        var latest = days.FirstOrDefault(d => d.HasAnyData);
        if (latest is null && report.Coaching is null) return;

        var prior = days.Where(d => string.CompareOrdinal(d.Date, latest?.Date ?? "") < 0).ToList();
        var parts = new List<string>();

        if (report.Coaching is { } c)
        {
            var dot = c.Readiness switch { Readiness.Green => "🟢", Readiness.Amber => "🟡", _ => "🔴" };
            parts.Add($"{dot} **Readiness {(c.ReadinessScore?.ToString() ?? c.Readiness.ToString())}**");
        }
        if (latest?.RestingHeartRate is int rhr)
            parts.Add($"❤️ {rhr} bpm{Arrow(rhr, Avg(prior, x => x.RestingHeartRate))}");
        if (latest?.HrvLastNight is int hrv)
            parts.Add($"💓 {hrv} ms{Arrow(hrv, Avg(prior, x => x.HrvLastNight))}");
        if (latest?.SleepHours is double sl)
            parts.Add($"😴 {sl:0.0} h");
        if (latest?.BodyBatteryHigh is int bb)
            parts.Add($"🔋 {bb}");

        var (cur, _) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.Km > 0) parts.Add($"🏃 {cur.Km:0.#} km/Wo");
        if (report.Coaching?.Tsb is double tsb) parts.Add($"🎚️ Form {tsb:+0;-0;0}");
        var streak = Streak(report.Activities, today);
        if (streak > 0) parts.Add($"🔥 {streak} {(streak == 1 ? "Tag" : "Tage")} Streak");
        if (report.Coaching?.DaysToRace is int dtr) parts.Add($"🏁 {dtr} T");

        if (parts.Count == 0) return;
        sb.AppendLine("> " + string.Join("  ·  ", parts));
        sb.AppendLine();
    }

    // ---- Stale-data / auth-failure warning -----------------------------------
    private static void AppendStaleness(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var latest = report.Days
            .Where(d => d.HasAnyData)
            .Select(d => DateOnly.TryParse(d.Date, out var dd) ? (DateOnly?)dd : null)
            .Where(d => d.HasValue).Select(d => d!.Value)
            .DefaultIfEmpty().Max();

        if (latest == default)
        {
            sb.AppendLine("> ⚠️ **Keine Daten** — der letzte Sync hat keine Garmin-Daten geliefert (Token abgelaufen/widerrufen oder API nicht erreichbar?). Prüfe die GitHub-Action.");
            sb.AppendLine();
            return;
        }

        var gap = today.DayNumber - latest.DayNumber;
        if (gap >= 2)
        {
            sb.AppendLine($"> ⚠️ **Daten möglicherweise veraltet** — letzter Datenpunkt {latest:yyyy-MM-dd} (vor {gap} Tagen). Möglicher Sync-/Token-Fehler; prüfe die GitHub-Action.");
            sb.AppendLine();
        }
    }

    // ---- Early-warning system ------------------------------------------------
    private static void AppendAlerts(StringBuilder sb, IReadOnlyList<HealthAlert> alerts)
    {
        if (alerts is null || alerts.Count == 0) return;
        var actionable = alerts.Where(a => a.Level != AlertLevel.Good).ToList();
        if (actionable.Count == 0)
        {
            var g = alerts[0];
            sb.AppendLine($"> {g.Icon} **{g.Title}** — {g.Detail}");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("## 🚨 Frühwarnsystem");
        sb.AppendLine();
        foreach (var a in actionable)
        {
            var badge = a.Level switch { AlertLevel.Red => "🔴", AlertLevel.Amber => "🟡", _ => "🔵" };
            sb.AppendLine($"- {badge} {a.Icon} **{a.Title}** — {a.Detail}");
        }
        sb.AppendLine();
    }

    // ---- Race countdown (only near the goal race) ----------------------------
    private static void AppendRaceCountdown(StringBuilder sb, DailyCoaching? c)
    {
        if (c?.DaysToRace is not int dtr || dtr < 0 || dtr > 28) return;

        var phase = dtr <= 7 ? "Wettkampfwoche" : dtr <= 21 ? "Taper" : "Vor-Taper";
        sb.AppendLine("## 🏁 Race-Countdown");
        sb.AppendLine();
        sb.AppendLine($"**Noch {dtr} {(dtr == 1 ? "Tag" : "Tage")}**{(c.RaceDate is null ? "" : $" bis {c.RaceDate}")} · Phase: {phase}");
        sb.AppendLine();
        if (c.Race?.MarathonSeconds is int ms)
        {
            var line = $"- ⏱️ Prognose {FormatTime(ms)}";
            if (!string.IsNullOrWhiteSpace(c.Goal)) line += $" · Ziel {c.Goal}";
            line += GoalVerdict(c);
            sb.AppendLine(line);
        }
        if (!string.IsNullOrWhiteSpace(c.EnduranceCaveat)) sb.AppendLine($"- 🏃 {c.EnduranceCaveat}");
        if (c.TaperNote is not null) sb.AppendLine($"- ⏳ {c.TaperNote}");
        if (c.NextQuality is not null) sb.AppendLine($"- ⚡ Nächste Schärfe: {c.NextQuality.Date} ({DescribePlan(c.NextQuality)})");
        sb.AppendLine();
    }

    // ---- Today's structured workout ------------------------------------------
    private static void AppendTodaysWorkout(StringBuilder sb, DailyCoaching? c)
    {
        if (c is null || c.TrainedToday) return; // already trained → handled in the coach block
        var w = c.PlanToday.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Detail))
                ?? c.PlanToday.FirstOrDefault(p => p.Type != SessionType.Rest);
        if (w is null) return;

        sb.AppendLine("## 🏋️ Heutige Einheit");
        sb.AppendLine();
        sb.AppendLine($"**{w.Title ?? w.Type.ToString()}**");
        sb.AppendLine();

        var rendered = false;
        if (!string.IsNullOrWhiteSpace(w.Detail))
        {
            // Detail = "Name [sport] (est): step → step → …". Split on the step separator first,
            // then strip the header prefix from the first step. Step text only ever has colon-digit
            // (10:00, 4:30/km) — never ": " — so the last ": " in steps[0] is the header boundary,
            // even if the workout name itself contains ": ".
            var steps = w.Detail!.Split(" → ");
            if (steps.Length > 1)
            {
                var sep = steps[0].LastIndexOf(": ", StringComparison.Ordinal);
                if (sep >= 0) steps[0] = steps[0][(sep + 2)..];
                foreach (var s in steps) sb.AppendLine($"- {s.Trim()}");
                rendered = true;
            }
            else { sb.AppendLine($"> {w.Detail}"); rendered = true; }
        }
        if (!rendered)
        {
            var bits = new List<string>();
            if (w.DistanceKm is double km) bits.Add($"{km:0.#} km");
            if (w.DurationMin is double m) bits.Add($"{m:0} min");
            if (bits.Count > 0) sb.AppendLine(string.Join(" · ", bits));
        }
        if (!string.IsNullOrWhiteSpace(c.TodayTargetPace))
        {
            sb.AppendLine();
            sb.AppendLine($"🎯 Zieltempo: {c.TodayTargetPace}");
        }
        sb.AppendLine();
    }

    // ---- Week ahead (upcoming planned sessions) ------------------------------
    private static void AppendUpcoming(StringBuilder sb, DailyCoaching? c, DateOnly today)
    {
        if (c is null || c.Upcoming.Count == 0) return;
        var horizon = today.AddDays(7);
        var rows = c.Upcoming
            .Where(p => DateOnly.TryParse(p.Date, out var d) && d > today && d <= horizon)
            .OrderBy(p => p.Date, StringComparer.Ordinal)
            .Take(8)
            .ToList();
        if (rows.Count == 0) return;

        sb.AppendLine("## 📆 Woche voraus");
        sb.AppendLine();
        foreach (var p in rows)
        {
            var when = DateOnly.TryParse(p.Date, out var d) ? $"{Weekday(d)} {d:dd.MM.}" : p.Date;
            sb.AppendLine($"- **{when}** {SessionIcon(p.Type)} {DescribePlan(p)}");
        }
        sb.AppendLine();
    }

    private static string Weekday(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mo", DayOfWeek.Tuesday => "Di", DayOfWeek.Wednesday => "Mi",
        DayOfWeek.Thursday => "Do", DayOfWeek.Friday => "Fr", DayOfWeek.Saturday => "Sa", _ => "So",
    };

    private static string SessionIcon(SessionType t) => t switch
    {
        SessionType.Rest => "😴", SessionType.Easy => "🟢", SessionType.Long => "🛣️",
        SessionType.Quality => "⚡", SessionType.Strength => "💪", SessionType.Race => "🏁", _ => "🏃",
    };

    // ---- Weekly review (last completed week) ----------------------------------
    private static void AppendWeeklyReview(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var c = report.Coaching;
        var (_, prev) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (prev.Sessions == 0 && (c?.PlannedLastWeek ?? 0) == 0 && string.IsNullOrWhiteSpace(report.WeeklyInsight)) return;

        var offset = ((int)today.DayOfWeek + 6) % 7;
        var curStart = today.AddDays(-offset);
        var lastStart = curStart.AddDays(-7);
        var lastEnd = curStart.AddDays(-1);
        var beforeStart = curStart.AddDays(-14);
        var beforeEnd = curStart.AddDays(-8);

        sb.AppendLine("## 📅 Wochenrückblick (letzte Woche)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(report.WeeklyInsight))
        {
            sb.AppendLine(report.WeeklyInsight!.Trim());
            sb.AppendLine();
        }
        sb.AppendLine($"- 🏃 {prev.Km:0.#} km in {prev.Sessions} Einheiten (längster {prev.LongestKm:0.#} km) · ⚡ {prev.IntensityMinutes} Intensitätsmin");
        if (c?.PlannedLastWeek is int pl && pl > 0)
        {
            var km = c.PlannedKmLastWeek is double pkm && pkm > 0 ? $" · {c.DoneKmLastWeek ?? 0:0.#}/{pkm:0.#} km" : "";
            sb.AppendLine($"- ✅ Planerfüllung: {c.DoneLastWeek ?? 0}/{pl} Einheiten{km}");
        }

        var sleepLast = AvgRange(report.Days, lastStart, lastEnd, d => d.SleepHours);
        var rhrLast = AvgRange(report.Days, lastStart, lastEnd, d => d.RestingHeartRate);
        var hrvLast = AvgRange(report.Days, lastStart, lastEnd, d => d.HrvLastNight);
        if (sleepLast is not null || rhrLast is not null || hrvLast is not null)
        {
            var parts = new List<string>();
            if (sleepLast is double sl) parts.Add($"😴 Ø {sl:0.0} h{Arrow(sl, AvgRange(report.Days, beforeStart, beforeEnd, d => d.SleepHours))}");
            if (rhrLast is double rl) parts.Add($"❤️ Ø {rl:0} bpm{Arrow(rl, AvgRange(report.Days, beforeStart, beforeEnd, d => d.RestingHeartRate))}");
            if (hrvLast is double hl) parts.Add($"💓 Ø {hl:0} ms{Arrow(hl, AvgRange(report.Days, beforeStart, beforeEnd, d => d.HrvLastNight))}");
            sb.AppendLine($"- {string.Join(" · ", parts)} _(vs. Vorwoche)_");
        }

        sb.AppendLine();
        // (The forward-looking "Fokus diese Woche" lived here but belongs to "📆 Woche voraus".)
    }

    // ---- 4-week trend digest -------------------------------------------------
    private static void AppendTrends(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var t = TrainingTrends.Compute(report, today);
        var rows = new List<string>();

        void Row(string label, TrendPoint? p, string unit, bool? lowerBetter, string fmt)
        {
            if (p is null) return;
            rows.Add($"| {label} | {p.Current.ToString(fmt)}{unit} | {TrendCell(p.Current, p.Past, lowerBetter, v => v.ToString(fmt))} |");
        }

        Row("❤️ Ruhepuls", t.RestingHeartRate, " bpm", lowerBetter: true, "0");
        Row("💓 HRV", t.Hrv, " ms", lowerBetter: false, "0");
        Row("😴 Schlaf-Score", t.SleepScore, "", lowerBetter: false, "0");
        Row("🫁 SpO₂", t.SpO2, " %", lowerBetter: false, "0");
        Row("⚖️ Gewicht", t.WeightKg, " kg", lowerBetter: null, "0.0");
        Row("🧬 Körperfett", t.BodyFatPercent, " %", lowerBetter: null, "0.0");
        // Neutral (lowerBetter: null) like weight/body-fat above: body-composition direction isn't
        // unambiguously "good" or "bad" without knowing the athlete's actual goal, and pushing a
        // health judgment here risks being overconfident (e.g. very low body fat can itself be a
        // training-risk signal, not a win).
        Row("💪 Muskelmasse", t.MuscleMassKg, " kg", lowerBetter: null, "0.0");
        Row("🫀 Viszeralfett", t.VisceralFatRating, "", lowerBetter: null, "0");
        Row("🫁 VO₂max", t.Vo2Max, "", lowerBetter: false, "0.0");
        Row("🏋️ Fitness (CTL)", t.FitnessCtl, "", lowerBetter: false, "0");

        if (t.MarathonPredictionSeconds is { } mp)
            rows.Add($"| ⏱️ Marathon-Prognose | {FormatTime((int)mp.Current)} | {TrendCellTime(mp.Current, mp.Past)} |");

        if (rows.Count < 2) return;
        sb.AppendLine("## 📈 Trends (4 Wochen)");
        sb.AppendLine();
        sb.AppendLine("| Metrik | Aktuell | Δ 4 Wochen |");
        sb.AppendLine("|---|--:|--:|");
        foreach (var r in rows) sb.AppendLine(r);
        sb.AppendLine();
    }

    private static string TrendCell(double? cur, double? past, bool? lowerBetter, Func<double, string> fmtAbs)
    {
        if (cur is null || past is null) return "–";
        var d = cur.Value - past.Value;
        if (Math.Abs(d) < 1e-9) return "→ stabil";
        var arrow = d > 0 ? "▲" : "▼";
        var s = (d > 0 ? "+" : "−") + fmtAbs(Math.Abs(d));
        var judge = lowerBetter is bool lb ? (((lb && d < 0) || (!lb && d > 0)) ? " ✅" : " ⚠️") : "";
        return $"{arrow} {s}{judge}";
    }

    private static string TrendCellTime(double? cur, double? past)
    {
        if (cur is null || past is null) return "–";
        var d = (int)(cur.Value - past.Value); // seconds; negative = faster = better
        if (Math.Abs(d) < 1) return "→ stabil";
        var arrow = d > 0 ? "▲" : "▼";
        var judge = d < 0 ? " ✅" : " ⚠️";
        return $"{arrow} {(d > 0 ? "+" : "−")}{FormatTime(Math.Abs(d))}{judge}";
    }

    internal static (double? First, double? Last) FirstLast(
        IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end, Func<DayMetrics, double?> sel)
    {
        var pts = days
            .Where(d => DateOnly.TryParse(d.Date, out var dd) && dd >= start && dd <= end)
            .OrderBy(d => d.Date, StringComparer.Ordinal)
            .Select(sel).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return pts.Count == 0 ? (null, null) : (pts[0], pts[^1]);
    }

    // ---- Training pace zones --------------------------------------------------
    private static void AppendPaceZones(StringBuilder sb, PaceZones? paces)
    {
        if (paces is null || paces.Zones.Count == 0) return;
        sb.AppendLine("## 🎯 Zieltempo-Zonen");
        sb.AppendLine();
        sb.AppendLine("_Abgeleitet aus deinen Garmin-Renn-Prognosen._");
        sb.AppendLine();
        sb.AppendLine("| Zone | Tempo |");
        sb.AppendLine("|---|--:|");
        foreach (var z in paces.Zones)
            sb.AppendLine($"| {z.Name} | {z.Range} |");
        sb.AppendLine();
    }

    // ---- Personal bests ------------------------------------------------------
    private static void AppendSeasonBests(StringBuilder sb, IReadOnlyList<PersonalBest> bests)
    {
        if (bests is null || bests.Count == 0) return;
        sb.AppendLine("## 🏅 Bestzeiten");
        sb.AppendLine();
        sb.AppendLine("| Distanz | Bestwert | Datum |");
        sb.AppendLine("|---|--:|--:|");
        foreach (var b in bests)
            sb.AppendLine($"| {b.Label} | **{b.Value}** | {b.Date ?? "–"} |");
        sb.AppendLine();
    }

    private static void AppendLatestDay(StringBuilder sb, List<DayMetrics> days)
    {
        var latest = days.FirstOrDefault(d => d.HasAnyData);
        if (latest is null) return;

        sb.AppendLine($"## 📍 Letzter Tag — {latest.Date}");
        sb.AppendLine();
        sb.AppendLine($"- ❤️ Ruhepuls: {Val(latest.RestingHeartRate, "bpm")}");
        sb.AppendLine($"- 💓 HRV (letzte Nacht): {Val(latest.HrvLastNight, "ms")}{(latest.HrvStatus is null ? "" : $" ({latest.HrvStatus})")}");
        sb.AppendLine($"- 😴 Schlaf: {Val(latest.SleepHours, "h")}{(latest.SleepScore is int ss ? $" · Score {ss}/100" : "")}");
        if (latest.SleepDeepMin is not null || latest.SleepLightMin is not null || latest.SleepRemMin is not null)
            sb.AppendLine($"  - 🛌 Phasen: Tief {Dur(latest.SleepDeepMin)} · Leicht {Dur(latest.SleepLightMin)} · REM {Dur(latest.SleepRemMin)} · Wach {Dur(latest.SleepAwakeMin)}");
        if (latest.SleepRespirationRate is not null || latest.SpO2Avg is not null)
        {
            var bits = new List<string>();
            if (latest.SleepRespirationRate is double r) bits.Add($"Atemfrequenz Ø {r:0.0}/min");
            if (latest.SpO2Avg is int sa) bits.Add($"SpO₂ Ø {sa} %{(latest.SpO2Low is int sl ? $" (min {sl} %)" : "")}");
            sb.AppendLine($"  - 🫁 {string.Join(" · ", bits)}");
        }
        sb.AppendLine($"- 🔋 Body Battery: {(latest.BodyBatteryLow is null && latest.BodyBatteryHigh is null ? "–" : $"{latest.BodyBatteryLow?.ToString() ?? "?"} → {latest.BodyBatteryHigh?.ToString() ?? "?"}")}");
        sb.AppendLine($"- 😰 Stress (Ø): {Val(latest.StressAvg, "")}");
        sb.AppendLine($"- 👟 Schritte: {Val(latest.Steps, "")}");
        sb.AppendLine($"- 🔥 Kalorien: {Val(latest.Calories, "kcal")}");
        sb.AppendLine($"- ⚡ Intensitätsminuten: {latest.IntensityMinutes}");
        sb.AppendLine();
    }

    // ---- Weekly overview -----------------------------------------------------
    private static void AppendWeek(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var (cur, prev) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.Sessions == 0 && prev.Sessions == 0) return;

        sb.AppendLine("## 🗓️ Wochenüberblick (Mo–So)");
        sb.AppendLine();
        sb.AppendLine("| Metrik | Diese Woche | Letzte Woche |");
        sb.AppendLine("|---|--:|--:|");
        sb.AppendLine($"| 🏃 Distanz | **{cur.Km:0.#} km**{Arrow(cur.Km, prev.Km)} {PctParen(cur.Km, prev.Km)} | {prev.Km:0.#} km |");
        sb.AppendLine($"| ⏱️ Zeit | {cur.Hours:0.#} h{Arrow(cur.Hours, prev.Hours)} | {prev.Hours:0.#} h |");
        sb.AppendLine($"| 🔁 Einheiten | {cur.Sessions}{Arrow(cur.Sessions, prev.Sessions)} | {prev.Sessions} |");
        sb.AppendLine($"| 📏 Längster Lauf | {cur.LongestKm:0.#} km{Arrow(cur.LongestKm, prev.LongestKm)} | {prev.LongestKm:0.#} km |");
        sb.AppendLine($"| ⛰️ Höhenmeter | {cur.ElevationM:0} m{Arrow(cur.ElevationM, prev.ElevationM)} | {prev.ElevationM:0} m |");
        sb.AppendLine($"| ⚡ Intensitätsminuten | {cur.IntensityMinutes}{Arrow(cur.IntensityMinutes, prev.IntensityMinutes)} | {prev.IntensityMinutes} |");
        sb.AppendLine();

        if (cur.IntensityMinutes > 0)
        {
            var pct = (int)Math.Round(cur.IntensityMinutes / 150.0 * 100);
            var mark = cur.IntensityMinutes >= 150 ? "✅" : "▹";
            sb.AppendLine($"{mark} WHO-Ziel (150 Intensitätsminuten/Woche): {cur.IntensityMinutes}/150 ({pct} %)");
            sb.AppendLine();
        }

        if (cur.KmByType.Count > 0)
        {
            var split = string.Join(" · ", cur.KmByType
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{SportDe(kv.Key)} {kv.Value:0.#} km"));
            // Deliberately NOT running-only (unlike "🏃 Distanz" above) — this is the full cross-training
            // picture, so the sum can legitimately exceed the running-only distance row.
            sb.AppendLine($"Trainingsumfang nach Sportart (alle Aktivitäten): {split}");
            sb.AppendLine();
        }
    }

    // ---- Charts (PNG images; the GitHub mobile app does not render Mermaid) ----------
    // Grouped into collapsible <details> by theme: with ~20 charts, one giant flat gallery
    // was the longest, least scannable part of the phone view. Group order is fixed (not
    // discovery order) so the layout stays stable as charts are added/removed over time.
    private static readonly string[] ChartGroupOrder =
        { "🏋️ Form & Belastung", "❤️ Herz & Erholung", "😴 Schlaf", "📊 Training & Sonstiges" };

    private static string ChartGroup(string file) => IdOf(file) switch
    {
        "form" or "tsb" or "heatmap" or "acwr" or "vo2max" or "cadence" => "🏋️ Form & Belastung",
        "readiness" or "rhr" or "hrv" or "bodybattery" or "stress" or "spo2" => "❤️ Herz & Erholung",
        "sleep" or "sleepstages" or "sleepscore" or "bedtime" => "😴 Schlaf",
        _ => "📊 Training & Sonstiges", // weeklykm, typesplit, marathon, steps, weight, bodyfat, …
    };

    private static string IdOf(string file)
    {
        var name = file[(file.LastIndexOf('/') + 1)..];
        return name.EndsWith(".png", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static void AppendChartImages(StringBuilder sb, IReadOnlyList<ChartRef>? charts)
    {
        var gallery = charts?.Where(c => !c.File.EndsWith("hero.png", StringComparison.Ordinal)).ToList();
        if (gallery is null || gallery.Count == 0) return;

        sb.AppendLine("## 📈 Entwicklung");
        sb.AppendLine();
        sb.AppendLine("_Nach Themen gruppiert – zum Öffnen antippen._");
        sb.AppendLine();

        var byGroup = gallery.ToLookup(c => ChartGroup(c.File));
        foreach (var groupName in ChartGroupOrder)
        {
            var items = byGroup[groupName].ToList();
            if (items.Count == 0) continue;

            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{groupName} ({items.Count})</summary>");
            sb.AppendLine();
            foreach (var c in items)
            {
                sb.AppendLine($"**{c.Title}**");
                sb.AppendLine();
                sb.AppendLine($"![{c.Title}]({c.File})");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(c.Caption))
                {
                    sb.AppendLine($"> {c.Caption}");
                    sb.AppendLine();
                }
            }
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    // ---- Collapsible detail --------------------------------------------------
    private static void AppendDetails(StringBuilder sb, GarminReport report, List<DayMetrics> days)
    {
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>📋 Verlauf (Tabelle)</summary>");
        sb.AppendLine();
        sb.AppendLine("| Datum | Ruhe-HR | HRV | Schlaf | Schritte | Stress | Body Battery | kcal |");
        sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|");
        foreach (var d in days)
        {
            var bb = d.BodyBatteryLow is null && d.BodyBatteryHigh is null
                ? "–"
                : $"{d.BodyBatteryLow?.ToString() ?? "?"}→{d.BodyBatteryHigh?.ToString() ?? "?"}";
            sb.AppendLine($"| {d.Date} | {Cell(d.RestingHeartRate)} | {Cell(d.HrvLastNight)} | {Cell(d.SleepHours)} | {Cell(d.Steps)} | {Cell(d.StressAvg)} | {bb} | {Cell(d.Calories)} |");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        var activities = report.Activities
            .OrderByDescending(a => a.Date, StringComparer.Ordinal)
            .ThenByDescending(a => a.Id)
            .Take(12)
            .ToList();
        if (activities.Count == 0) return;

        sb.AppendLine("<details>");
        sb.AppendLine("<summary>🏃 Letzte Aktivitäten</summary>");
        sb.AppendLine();
        foreach (var a in activities)
        {
            var parts = new List<string>();
            if (a.DistanceKm is not null) parts.Add($"{a.DistanceKm} km");
            if (a.DurationMin is not null) parts.Add($"{a.DurationMin} min");
            if (a.ElevationGainM is not null) parts.Add($"{a.ElevationGainM:0} hm");
            if (a.Calories is not null) parts.Add($"{a.Calories} kcal");
            if (a.AverageHr is not null) parts.Add($"ø {a.AverageHr} bpm");
            if (a.CadenceSpm is double cad) parts.Add($"{cad:0} spm");
            if (a.GroundContactTimeMs is double gct) parts.Add($"GCT {gct:0} ms");
            if (a.VerticalOscillationCm is double vo) parts.Add($"VO {vo:0.#} cm");
            if (a.StrideLengthCm is double sl) parts.Add($"{sl:0} cm Schrittlänge");
            var detail = parts.Count > 0 ? " — " + string.Join(", ", parts) : "";
            sb.AppendLine($"- **{a.Date}** {a.Name ?? SportDe(a.Type)} _({SportDe(a.Type)})_{detail}");

            var effectBits = new List<string>();
            if (a.AerobicEffect is double ae && ae > 0) effectBits.Add($"Aerob {ae:0.0}");
            if (a.AnaerobicEffect is double an && an > 0) effectBits.Add($"Anaerob {an:0.0}");
            var effectLabelDe = EffectLabelDe(a.EffectLabel);
            if (effectBits.Count > 0 || effectLabelDe is not null)
            {
                var text = effectBits.Count > 0
                    ? string.Join(" · ", effectBits) + (effectLabelDe is not null ? $" ({effectLabelDe})" : "")
                    : effectLabelDe;
                sb.AppendLine($"  - 🎯 Trainingseffekt: {text}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    // ---- Coaching block ------------------------------------------------------
    private static void AppendCoaching(StringBuilder sb, GarminReport report)
    {
        var c = report.Coaching;
        if (c is null) return;

        sb.AppendLine($"## 🧠 Coach — {c.Date}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(report.CoachInsight))
        {
            sb.AppendLine(report.CoachInsight!.Trim());
            sb.AppendLine();
        }

        var score = c.ReadinessScore is int sc
            ? $"  ·  Readiness {sc}{(c.ReadinessLevel is null ? "" : $" ({c.ReadinessLevel})")}"
            : "";
        sb.AppendLine($"**{c.Headline}**{score}");
        sb.AppendLine();

        foreach (var r in c.Rationale)
            sb.AppendLine($"- {r}");

        if (c.PlanToday.Count > 0)
            sb.AppendLine($"- 📋 Plan heute: {string.Join(", ", c.PlanToday.Select(p => string.IsNullOrWhiteSpace(p.Title) ? SessionTypeNames.German(p.Type) : p.Title))}{(c.PlanNote is null ? "" : $" — {c.PlanNote}")}");
        else if (c.PlanNote is not null)
            sb.AppendLine($"- 📋 {c.PlanNote}");

        if (!string.IsNullOrWhiteSpace(c.TodayTargetPace))
            sb.AppendLine($"- 🎯 Zieltempo heute: {c.TodayTargetPace}");

        // 🏁 Race-Countdown (AppendRaceCountdown) renders whenever DaysToRace is in [0,28] —
        // regardless of whether a marathon-time prediction is available — and, when it does, it
        // already shows both NextQuality and TaperNote itself. Suppress them here in that window so
        // they aren't printed twice on the same dashboard render.
        var raceCountdownRenders = c.DaysToRace is >= 0 and <= 28;

        if (c.NextLongRun is not null)
            sb.AppendLine($"- 🛣️ Nächster Longrun: {c.NextLongRun.Date} ({DescribePlan(c.NextLongRun)})");
        if (c.NextQuality is not null && !raceCountdownRenders)
            sb.AppendLine($"- ⚡ Nächste harte Einheit: {c.NextQuality.Date} ({DescribePlan(c.NextQuality)})");

        // Status/form/adherence metrics move behind a fold: they're genuinely useful, but stacking
        // them as top-level bullets buried the block's actual purpose (today's action) under status
        // numbers that mostly duplicate the dedicated 📈 Trends / 🏋️ Form charts elsewhere. Nothing
        // is removed — it's one tap away — the default view is just the actionable bullets.
        var statusBits = new List<string>();
        if (c.TrainingStatus is not null) statusBits.Add($"Status: {c.TrainingStatus}");
        if (c.Acwr is double acwr) statusBits.Add($"ACWR {acwr:0.0}");
        if (c.Vo2Max is double v) statusBits.Add($"VO₂max {v:0.0}");

        var formBits = new List<string>();
        if (c.Ctl is double ctl) formBits.Add($"Fitness {ctl:0}");
        if (c.Atl is double atl) formBits.Add($"Fatigue {atl:0}");
        if (c.Tsb is double tsb) formBits.Add($"Form {tsb:+0;-0;0} ({FormLabel(tsb)})");

        var adherenceLine = c.PlannedThisWeek is int planned && planned > 0
            ? $"✅ Planerfüllung diese Woche: {c.DoneThisWeek ?? 0}/{planned} Einheiten{(c.PlannedKmThisWeek is double pkm && pkm > 0 ? $" · {c.DoneKmThisWeek ?? 0:0.#}/{pkm:0.#} km" : "")}"
            : null;
        var sleepConsistencyLine = c.SleepConsistencyMin is double scm ? $"🛏️ Schlaf-Konsistenz: ±{scm:0} min (Zubettgeh-Zeit)" : null;

        if (statusBits.Count > 0 || formBits.Count > 0 || adherenceLine is not null || sleepConsistencyLine is not null)
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>📊 Details zu Status, Form &amp; Planerfüllung</summary>");
            sb.AppendLine();
            if (statusBits.Count > 0) sb.AppendLine($"- 📈 {string.Join(" · ", statusBits)}");
            if (formBits.Count > 0) sb.AppendLine($"- 🏋️ {string.Join(" · ", formBits)}");
            if (adherenceLine is not null) sb.AppendLine($"- {adherenceLine}");
            if (sleepConsistencyLine is not null) sb.AppendLine($"- {sleepConsistencyLine}");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        // The marathon prognosis + goal verdict has a dedicated home (🏁 Race-Countdown when close to
        // race day AND a prediction is actually available that day, and usually the 📈 Trends digest
        // too, once ≥2 data points exist) — only repeat it here if the countdown won't show it, so it
        // isn't tripled on every render, but never let the goal silently vanish on a day Garmin's race
        // prediction happens to be unavailable (checking MarathonSeconds, not just the day window).
        var raceCountdownWillShow = c.DaysToRace is >= 0 and <= 28 && c.Race?.MarathonSeconds is not null;
        // TrainingPlanReader only ever nulls DaysToRace when RaceDate refers to a race already in the
        // past (it falls back to the most recent past race once no upcoming one exists) — don't show a
        // forward-looking "on track for goal" verdict for an event that has already happened.
        var isPastRace = c.RaceDate is not null && c.DaysToRace is null;
        if (!isPastRace && !raceCountdownWillShow && c.RaceDate is not null)
        {
            var racePart = $"🏁 Rennen: {c.RaceDate}" + (c.DaysToRace is int d ? $" (in {d} Tagen)" : "");
            if (c.Race?.MarathonSeconds is int ms) racePart += $" · Marathon-Prognose {FormatTime(ms)}";
            if (!string.IsNullOrWhiteSpace(c.Goal)) racePart += $" · Ziel {c.Goal}";
            racePart += GoalVerdict(c);
            sb.AppendLine($"- {racePart}");
        }
        else if (!isPastRace && !raceCountdownWillShow && c.Race?.MarathonSeconds is int ms2)
        {
            sb.AppendLine($"- 🏁 Marathon-Prognose {FormatTime(ms2)}{(string.IsNullOrWhiteSpace(c.Goal) ? "" : $" · Ziel {c.Goal}")}{GoalVerdict(c)}");
        }
        // Same suppression as above: only shown here when Race-Countdown (which carries its own copy)
        // won't render — never duplicated, never shown for a race that has already happened.
        if (!isPastRace && !raceCountdownWillShow && !string.IsNullOrWhiteSpace(c.EnduranceCaveat))
            sb.AppendLine($"- 🏃 {c.EnduranceCaveat}");

        if (c.TaperNote is not null && !raceCountdownRenders) sb.AppendLine($"- ⏳ {c.TaperNote}");

        if (c.Nutrition is { } n)
        {
            sb.AppendLine($"- 🍽️ Ernährung heute ({n.DayType}): ~{n.CalorieTarget} kcal — KH {n.CarbsG} g · Eiweiß {n.ProteinG} g · Fett {n.FatG} g{(n.WeightKg is double w ? $" (bei {w} kg)" : "")}");
            sb.AppendLine($"  - {n.Guidance}");
        }

        sb.AppendLine();
    }

    // ---- Helpers -------------------------------------------------------------
    private static string DescribePlan(PlannedWorkout p) =>
        $"{(string.IsNullOrWhiteSpace(p.Title) ? SessionTypeNames.German(p.Type) : p.Title)}{(p.DistanceKm is double km ? $", {km} km" : p.DurationMin is double m ? $", {m:0} min" : "")}";

    internal static string FormatTime(int seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Abs(seconds));
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string GoalVerdict(DailyCoaching c)
    {
        if (c.OnTrackForGoal is not bool ot) return "";
        if (ot)
            return c.GoalGapSeconds is int g ? $" · ✅ auf Kurs ({FormatTime(g)} Puffer)" : " · ✅ auf Kurs";
        return c.GoalGapSeconds is int g2 ? $" · ⚠️ {FormatTime(g2)} über Ziel" : " · ⚠️ hinter Ziel";
    }

    private static string Arrow(double cur, double? prev)
    {
        if (prev is not double p || p == 0) return "";
        if (cur > p * 1.02) return " ▲";
        if (cur < p * 0.98) return " ▼";
        return "";
    }

    private static string PctParen(double cur, double prev)
    {
        if (prev <= 0) return "";
        var pct = (cur - prev) / prev * 100;
        return $"({pct:+0;-0;0}%)";
    }

    private static double? Avg(IEnumerable<DayMetrics> days, Func<DayMetrics, int?> selector)
    {
        var v = days.Select(selector).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    // internal (not private): also called by TrainingTrends.Compute, the shared computation behind
    // both this markdown digest and the garmin_training_trends MCP tool.
    internal static double? AvgRange(IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end, Func<DayMetrics, int?> sel)
    {
        var v = days.Where(d => DateOnly.TryParse(d.Date, out var dd) && dd >= start && dd <= end)
            .Select(sel).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    internal static double? AvgRange(IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end, Func<DayMetrics, double?> sel)
    {
        var v = days.Where(d => DateOnly.TryParse(d.Date, out var dd) && dd >= start && dd <= end)
            .Select(sel).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    private static int Streak(IReadOnlyList<ActivitySummary> activities, DateOnly today)
    {
        var set = activities.Select(a => a.Date).ToHashSet();
        var d = today;
        if (!set.Contains(d.ToString("yyyy-MM-dd"))) d = d.AddDays(-1); // today may be unlogged yet
        var n = 0;
        while (set.Contains(d.ToString("yyyy-MM-dd"))) { n++; d = d.AddDays(-1); }
        return n;
    }

    private static string FormLabel(double tsb) => tsb switch
    {
        > 15 => "sehr frisch",
        > 5 => "frisch",
        >= -10 => "ausbalanciert",
        >= -20 => "ermüdet",
        _ => "stark ermüdet",
    };

    /// <summary>German label for a Garmin sport-type key (e.g. running → Laufen).</summary>
    internal static string SportDe(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Sonstiges";
        return type.ToLowerInvariant() switch
        {
            "running" => "Laufen",
            "treadmill_running" => "Laufband",
            "trail_running" => "Trail-Lauf",
            "track_running" => "Bahn-Lauf",
            "indoor_running" => "Laufen (drinnen)",
            "walking" => "Gehen",
            "hiking" => "Wandern",
            "cycling" or "road_biking" => "Radfahren",
            "indoor_cycling" or "virtual_ride" => "Radfahren (drinnen)",
            "mountain_biking" => "Mountainbike",
            "swimming" or "lap_swimming" or "open_water_swimming" => "Schwimmen",
            "strength_training" => "Krafttraining",
            "cardio" or "indoor_cardio" => "Cardio",
            "yoga" => "Yoga",
            "pilates" => "Pilates",
            "other" or "andere" => "Sonstiges",
            _ => char.ToUpperInvariant(type[0]) + type[1..].Replace('_', ' '),
        };
    }

    /// <summary>German label for Garmin's raw per-activity training-effect classification (e.g.
    /// "TEMPO_TRAINING_EFFECT_LABEL" → "Tempo"). Garmin's exact label set isn't publicly documented,
    /// so this matches on well-known keywords (mirroring CoachEngine.Humanize's approach for training
    /// status) and degrades to a readable phrase — never raw English — for anything unrecognized.</summary>
    internal static string? EffectLabelDe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.ToUpperInvariant();
        if (s.Contains("RECOVERY")) return "Erholung";
        if (s.Contains("THRESHOLD")) return "Schwelle";
        if (s.Contains("ANAEROBIC")) return "Anaerobe Kapazität";
        if (s.Contains("VO2MAX") || s.Contains("VO2_MAX")) return "VO₂max";
        if (s.Contains("TEMPO")) return "Tempo";
        if (s.Contains("SPRINT")) return "Sprint";
        if (s.Contains("SPEED")) return "Speed";
        if (s.Contains("BASE") || s.Contains("AEROBIC")) return "Grundlagenausdauer";
        if (s.Contains("MAINTAINING") || s.Contains("MAINTENANCE")) return "Erhaltend";
        if (s.Contains("OVERREACHING") || s.Contains("OVERLOAD")) return "Übertraining";
        if (s.Contains("UNKNOWN") || s.Contains("NONE")) return null;

        // Unrecognized label: strip Garmin's usual suffix and title-case rather than showing nothing.
        var stripped = raw.Replace("_TRAINING_EFFECT_LABEL", "", StringComparison.OrdinalIgnoreCase)
                           .Replace("_EFFECT_LABEL", "", StringComparison.OrdinalIgnoreCase)
                           .Replace('_', ' ').Trim().ToLowerInvariant();
        return stripped.Length == 0 ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stripped);
    }

    private static string Val(int? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Val(double? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Cell(int? v) => v?.ToString() ?? "–";
    private static string Cell(double? v) => v?.ToString() ?? "–";

    private static string Dur(int? minutes)
    {
        if (minutes is not int m) return "–";
        return m >= 60 ? $"{m / 60}:{m % 60:00} h" : $"{m} min";
    }
}
