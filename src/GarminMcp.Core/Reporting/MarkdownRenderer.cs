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
        AppendKpiBar(sb, report, days, today);
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

        var phase = dtr <= 7 ? "Race Week" : dtr <= 21 ? "Taper" : "Vor-Taper";
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

        var focus = new List<string>();
        if (c?.PlannedThisWeek is int pt && pt > 0) focus.Add($"{pt} geplante Einheiten");
        if (c?.NextLongRun is not null) focus.Add($"Longrun {c.NextLongRun.Date}");
        if (c?.NextQuality is not null) focus.Add($"Schärfe {c.NextQuality.Date}");
        if (focus.Count > 0) sb.AppendLine($"- 🎯 Fokus diese Woche: {string.Join(" · ", focus)}");
        sb.AppendLine();
    }

    // ---- 4-week trend digest -------------------------------------------------
    private static void AppendTrends(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var rStart = today.AddDays(-6); var rEnd = today;          // last 7 days
        var pStart = today.AddDays(-27); var pEnd = today.AddDays(-21); // the 7 days ~4 weeks ago
        var wStart = today.AddDays(-34);                            // 5-week window for sparse point metrics

        var rows = new List<string>();

        void AvgRow(string label, Func<DayMetrics, int?> sel, string unit, bool lowerBetter)
        {
            var cur = AvgRange(report.Days, rStart, rEnd, sel);
            if (cur is null) return;
            var past = AvgRange(report.Days, pStart, pEnd, sel);
            rows.Add($"| {label} | {cur.Value.ToString("0", CultureInfo.InvariantCulture)}{unit} | {TrendCell(cur, past, lowerBetter, v => v.ToString("0", CultureInfo.InvariantCulture))} |");
        }

        AvgRow("❤️ Ruhepuls", d => d.RestingHeartRate, " bpm", lowerBetter: true);
        AvgRow("💓 HRV", d => d.HrvLastNight, " ms", lowerBetter: false);
        AvgRow("😴 Schlaf-Score", d => d.SleepScore, "", lowerBetter: false);

        var wCur = AvgRange(report.Days, rStart, rEnd, d => d.WeightKg);
        if (wCur is double wc)
        {
            var wPast = AvgRange(report.Days, pStart, pEnd, d => d.WeightKg);
            rows.Add($"| ⚖️ Gewicht | {wc.ToString("0.0", CultureInfo.InvariantCulture)} kg | {TrendCell(wCur, wPast, null, v => v.ToString("0.0", CultureInfo.InvariantCulture))} |");
        }

        var (vF, vL) = FirstLast(report.Days, wStart, today, d => d.Vo2Max);
        if (vL is double vl)
            rows.Add($"| 🫁 VO₂max | {vl.ToString("0.0", CultureInfo.InvariantCulture)} | {TrendCell(vL, vF, false, v => v.ToString("0.0", CultureInfo.InvariantCulture))} |");

        var (pmc, _, _, _) = LoadModel.Compute(report.Activities, today, 35);
        if (pmc.Count >= 2)
            rows.Add($"| 🏋️ Fitness (CTL) | {pmc[^1].Ctl.ToString("0", CultureInfo.InvariantCulture)} | {TrendCell(pmc[^1].Ctl, pmc[0].Ctl, false, v => v.ToString("0", CultureInfo.InvariantCulture))} |");

        var (mF, mL) = FirstLast(report.Days, wStart, today, d => d.MarathonSeconds is int s ? s : (double?)null);
        if (mL is double ml)
            rows.Add($"| ⏱️ Marathon-Prognose | {FormatTime((int)ml)} | {TrendCellTime(mL, mF)} |");

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

    private static (double? First, double? Last) FirstLast(
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

        sb.AppendLine($"## Letzter Tag — {latest.Date}");
        sb.AppendLine();
        sb.AppendLine($"- ❤️ Ruhepuls: {Val(latest.RestingHeartRate, "bpm")}");
        sb.AppendLine($"- 💓 HRV (letzte Nacht): {Val(latest.HrvLastNight, "ms")}{(latest.HrvStatus is null ? "" : $" ({latest.HrvStatus})")}");
        sb.AppendLine($"- 😴 Schlaf: {Val(latest.SleepHours, "h")}{(latest.SleepScore is int ss ? $" · Score {ss}/100" : "")}");
        if (latest.SleepDeepMin is not null || latest.SleepLightMin is not null || latest.SleepRemMin is not null)
            sb.AppendLine($"  - 🛌 Phasen: Tief {Dur(latest.SleepDeepMin)} · Leicht {Dur(latest.SleepLightMin)} · REM {Dur(latest.SleepRemMin)} · Wach {Dur(latest.SleepAwakeMin)}");
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
                .Select(kv => $"{kv.Key} {kv.Value:0.#} km"));
            sb.AppendLine($"Aufteilung: {split}");
            sb.AppendLine();
        }
    }

    // ---- Charts (PNG images; the GitHub mobile app does not render Mermaid) ----------
    private static void AppendChartImages(StringBuilder sb, IReadOnlyList<ChartRef>? charts)
    {
        var gallery = charts?.Where(c => !c.File.EndsWith("hero.png", StringComparison.Ordinal)).ToList();
        if (gallery is null || gallery.Count == 0) return;
        sb.AppendLine("## 📈 Entwicklung");
        sb.AppendLine();
        foreach (var c in gallery)
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
            var detail = parts.Count > 0 ? " — " + string.Join(", ", parts) : "";
            sb.AppendLine($"- **{a.Date}** {a.Name ?? a.Type ?? "Aktivität"} _({a.Type ?? "?"})_{detail}");
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
            sb.AppendLine($"- 📋 Plan heute: {string.Join(", ", c.PlanToday.Select(p => p.Title ?? p.Type.ToString()))}{(c.PlanNote is null ? "" : $" — {c.PlanNote}")}");
        else if (c.PlanNote is not null)
            sb.AppendLine($"- 📋 {c.PlanNote}");

        if (!string.IsNullOrWhiteSpace(c.TodayTargetPace))
            sb.AppendLine($"- 🎯 Zieltempo heute: {c.TodayTargetPace}");

        if (c.NextLongRun is not null)
            sb.AppendLine($"- 🛣️ Nächster Longrun: {c.NextLongRun.Date} ({DescribePlan(c.NextLongRun)})");
        if (c.NextQuality is not null)
            sb.AppendLine($"- ⚡ Nächste harte Einheit: {c.NextQuality.Date} ({DescribePlan(c.NextQuality)})");

        var statusBits = new List<string>();
        if (c.TrainingStatus is not null) statusBits.Add($"Status: {c.TrainingStatus}");
        if (c.Acwr is double acwr) statusBits.Add($"ACWR {acwr:0.0}");
        if (c.Vo2Max is double v) statusBits.Add($"VO₂max {v:0.0}");
        if (statusBits.Count > 0) sb.AppendLine($"- 📈 {string.Join(" · ", statusBits)}");

        var formBits = new List<string>();
        if (c.Ctl is double ctl) formBits.Add($"Fitness {ctl:0}");
        if (c.Atl is double atl) formBits.Add($"Fatigue {atl:0}");
        if (c.Tsb is double tsb) formBits.Add($"Form {tsb:+0;-0;0} ({FormLabel(tsb)})");
        if (formBits.Count > 0) sb.AppendLine($"- 🏋️ {string.Join(" · ", formBits)}");

        if (c.PlannedThisWeek is int planned && planned > 0)
        {
            var km = c.PlannedKmThisWeek is double pkm && pkm > 0
                ? $" · {c.DoneKmThisWeek ?? 0:0.#}/{pkm:0.#} km"
                : "";
            sb.AppendLine($"- ✅ Planerfüllung diese Woche: {c.DoneThisWeek ?? 0}/{planned} Einheiten{km}");
        }
        if (c.SleepConsistencyMin is double scm)
            sb.AppendLine($"- 🛏️ Schlaf-Konsistenz: ±{scm:0} min (Zubettgeh-Zeit)");

        if (c.RaceDate is not null)
        {
            var racePart = $"🏁 Rennen: {c.RaceDate}" + (c.DaysToRace is int d ? $" (in {d} Tagen)" : "");
            if (c.Race?.MarathonSeconds is int ms) racePart += $" · Marathon-Prognose {FormatTime(ms)}";
            if (!string.IsNullOrWhiteSpace(c.Goal)) racePart += $" · Ziel {c.Goal}";
            racePart += GoalVerdict(c);
            sb.AppendLine($"- {racePart}");
        }
        else if (c.Race?.MarathonSeconds is int ms2)
        {
            sb.AppendLine($"- 🏁 Marathon-Prognose {FormatTime(ms2)}{(string.IsNullOrWhiteSpace(c.Goal) ? "" : $" · Ziel {c.Goal}")}{GoalVerdict(c)}");
        }

        if (c.TaperNote is not null) sb.AppendLine($"- ⏳ {c.TaperNote}");

        if (c.Nutrition is { } n)
        {
            sb.AppendLine($"- 🍽️ Ernährung heute ({n.DayType}): ~{n.CalorieTarget} kcal — KH {n.CarbsG} g · Eiweiß {n.ProteinG} g · Fett {n.FatG} g{(n.WeightKg is double w ? $" (bei {w} kg)" : "")}");
            sb.AppendLine($"  - {n.Guidance}");
        }

        sb.AppendLine();
    }

    // ---- Helpers -------------------------------------------------------------
    private static string DescribePlan(PlannedWorkout p) =>
        $"{p.Title ?? p.Type.ToString()}{(p.DistanceKm is double km ? $", {km} km" : p.DurationMin is double m ? $", {m:0} min" : "")}";

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

    private static double? AvgRange(IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end, Func<DayMetrics, int?> sel)
    {
        var v = days.Where(d => DateOnly.TryParse(d.Date, out var dd) && dd >= start && dd <= end)
            .Select(sel).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    private static double? AvgRange(IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end, Func<DayMetrics, double?> sel)
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

    private static string Short(string isoDate) => isoDate.Length >= 10 ? isoDate[5..] : isoDate;
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
