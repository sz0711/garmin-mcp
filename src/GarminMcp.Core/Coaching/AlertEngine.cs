using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

public enum AlertLevel
{
    Good,   // explicit all-clear
    Info,   // worth noticing
    Amber,  // caution
    Red,    // act now
}

/// <summary>A health/training early-warning signal derived from multi-day trends.</summary>
public sealed class HealthAlert
{
    public AlertLevel Level { get; set; }
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// Early-warning system. Unlike <see cref="CoachEngine"/> (which judges today), this looks
/// at multi-day trends across the accumulated history — elevated resting HR, suppressed HRV,
/// training-load spikes (ACWR), accumulated sleep debt and acute illness patterns — so
/// problems surface before they derail training. Everything degrades gracefully: with too
/// little history a given check is simply skipped.
/// </summary>
public static class AlertEngine
{
    public static List<HealthAlert> Evaluate(
        IReadOnlyList<DayMetrics> days, TrainingStatusInfo? status, DateOnly today,
        IReadOnlyList<ActivitySummary>? activities = null,
        int? daysToRace = null, PaceZones? paces = null)
    {
        var alerts = new List<HealthAlert>();
        var todayKey = today.ToString("yyyy-MM-dd");

        var ordered = days
            .Where(d => string.CompareOrdinal(d.Date, todayKey) <= 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0) return alerts;

        // Baselines from the trailing ~28 days, excluding the 3 most recent days we're testing
        // (Skip count must match the recent window so the two are disjoint — no self-contamination).
        var recent = ordered.Take(3).ToList();
        var baselineWindow = ordered.Skip(3).Take(28).ToList();
        double? rhrBase = Avg(baselineWindow, d => d.RestingHeartRate);
        double? hrvBase = Avg(baselineWindow, d => d.HrvLastNight);

        var latest = ordered[0];
        var checksRun = 0;

        // --- Resting HR elevated over several days ---
        if (rhrBase is double rb && rb > 0)
        {
            var recentRhr = recent.Select(d => d.RestingHeartRate).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            if (recentRhr.Count >= 2)
            {
                checksRun++;
                var avgRecent = recentRhr.Average();
                var delta = avgRecent - rb;
                var daysHigh = recentRhr.Count(v => v >= rb + 5);
                if (delta >= 6 || daysHigh >= 3)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "❤️‍🔥", Title = "Ruhepuls deutlich erhöht",
                        Detail = $"Ø der letzten {recentRhr.Count} Tage {avgRecent:0} bpm – {delta:+0;-0} bpm über deiner ~{rb:0}-bpm-Baseline. Häufig ein Zeichen für unvollständige Erholung, Stress oder eine beginnende Infektion." });
                else if (delta >= 3.5 || daysHigh >= 2)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "❤️", Title = "Ruhepuls leicht erhöht",
                        Detail = $"Ø der letzten Tage {avgRecent:0} bpm, etwas über deiner ~{rb:0}-bpm-Baseline. Beobachten und Erholung priorisieren." });
            }
        }

        // --- HRV suppressed over several days ---
        if (hrvBase is double hb && hb > 0)
        {
            var recentHrv = recent.Select(d => d.HrvLastNight).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            if (recentHrv.Count >= 2)
            {
                checksRun++;
                var avgRecent = recentHrv.Average();
                var pct = (avgRecent - hb) / hb * 100.0;
                if (pct <= -12)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "💔", Title = "HRV deutlich unter Baseline",
                        Detail = $"Ø der letzten Tage {avgRecent:0} ms – {pct:0}% unter deiner ~{hb:0}-ms-Baseline. Dein Nervensystem ist stark belastet; plane echte Erholung ein." });
                else if (pct <= -7)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "💛", Title = "HRV unter Baseline",
                        Detail = $"Ø der letzten Tage {avgRecent:0} ms ({pct:0}% unter ~{hb:0} ms). Intensität zurücknehmen, bis sich die HRV erholt." });
            }
        }

        // --- Training-load ratio (ACWR) ---
        var acwr = status?.Acwr ?? latest.Acwr;
        if (acwr is double a && a > 0)
        {
            checksRun++;
            if (a > 1.5)
                alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "🚀", Title = "Trainingslast-Spitze",
                    Detail = $"ACWR {a:0.0} (>1,5): Deine akute Last liegt weit über der chronischen – das ist das klassische Verletzungs-Risikofenster. Umfang/Intensität ein paar Tage drosseln." });
            else if (a > 1.3)
                alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "📈", Title = "Trainingslast steigt schnell",
                    Detail = $"ACWR {a:0.0} (Ziel 0,8–1,3): Aufbau ok, aber nicht weiter forcieren – sonst kippt es ins Risikofenster." });
            else if (a < 0.8)
                alerts.Add(new HealthAlert { Level = AlertLevel.Info, Icon = "📉", Title = "Wenig Trainingsreiz",
                    Detail = $"ACWR {a:0.0} (<0,8): Die Last fällt – ok in der Taper-/Erholungsphase, sonst droht Formverlust." });
        }

        // --- Accumulated sleep debt over the last 7 days ---
        const double sleepTarget = 7.5;
        var weekSleep = ordered.Take(7).Select(d => d.SleepHours).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (weekSleep.Count >= 4)
        {
            checksRun++;
            var debt = weekSleep.Sum(s => Math.Max(0, sleepTarget - s));
            var avg = weekSleep.Average();
            if (debt >= 8 || avg < 6)
                alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "🥱", Title = "Hohes Schlafdefizit",
                    Detail = $"~{debt:0} h Defizit über die letzten {weekSleep.Count} Tage (Ø {avg:0.0} h/Nacht). Schlaf ist der stärkste Erholungshebel – heute früher ins Bett." });
            else if (debt >= 5 || avg < 6.8)
                alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "😴", Title = "Schlafdefizit baut sich auf",
                    Detail = $"~{debt:0} h Defizit über {weekSleep.Count} Tage (Ø {avg:0.0} h). Auf konstante, ausreichende Nächte achten." });
        }

        // --- Pulse-ox (SpO2) low — possible altitude, congestion, or sleep-disordered breathing ---
        var recentSpo2 = recent.Select(d => d.SpO2Avg).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        if (recentSpo2.Count >= 2)
        {
            checksRun++;
            var avgSpo2 = recentSpo2.Average();
            if (avgSpo2 < 90)
                alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "🫁", Title = "Sauerstoffsättigung niedrig",
                    Detail = $"Ø der letzten {recentSpo2.Count} Tage {avgSpo2:0} % SpO₂ – deutlich unter dem üblichen Bereich. Falls das anhält, ärztlich abklären lassen (z. B. Schlafapnoe, Atemwegsinfekt)." });
            else if (avgSpo2 < 94)
                alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "🫁", Title = "Sauerstoffsättigung leicht erniedrigt",
                    Detail = $"Ø der letzten Tage {avgSpo2:0} % SpO₂ – kann auf Höhenlage, Verstopfung oder unruhigen Schlaf hindeuten. Beobachten." });
        }

        // --- Running-economy trend: a sustained cadence drop at a SIMILAR pace can signal fatigue.
        // Cadence is strongly speed-dependent, so comparing runs from different session types (e.g.
        // easy runs vs. a tempo/interval block) would confound a training-mix change with fatigue. ---
        if (activities is not null)
        {
            var runs = activities
                .Where(a => a.CadenceSpm.HasValue && a.DistanceKm is > 0 && a.DurationMin is > 0
                            && DateOnly.TryParse(a.Date, out var ad) && ad <= today)
                .OrderByDescending(a => a.Date, StringComparer.Ordinal)
                .ToList();
            // Require the most recent qualifying run to be current — a runner who stopped running
            // weeks ago shouldn't get a stale "form is slipping" alert from an old training block.
            var recentEnough = runs.Count > 0 && DateOnly.TryParse(runs[0].Date, out var lastRunDate)
                && today.DayNumber - lastRunDate.DayNumber <= 14;
            if (recentEnough && runs.Count >= 6) // 3 recent runs + at least 3 to form a real baseline
            {
                checksRun++;
                var recentRuns = runs.Take(3).ToList();
                var baseRuns = runs.Skip(3).Take(6).ToList();
                var recentCad = recentRuns.Average(a => a.CadenceSpm!.Value);
                var baseCad = baseRuns.Average(a => a.CadenceSpm!.Value);
                double PaceSecPerKm(ActivitySummary a) => a.DurationMin!.Value * 60.0 / a.DistanceKm!.Value;
                var recentPace = recentRuns.Average(PaceSecPerKm);
                var basePace = baseRuns.Average(PaceSecPerKm);
                var paceSimilar = basePace > 0 && Math.Abs(recentPace - basePace) / basePace <= 0.15;
                var drop = baseCad - recentCad;
                if (paceSimilar && drop >= 4)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "🦶", Title = "Laufökonomie lässt nach",
                        Detail = $"Kadenz der letzten 3 Läufe Ø {recentCad:0} spm, {drop:0} spm unter dem Schnitt der {baseRuns.Count} Läufe davor (Ø {baseCad:0} spm) bei ähnlichem Tempo. Kann Ermüdung oder Formverlust zeigen – auf Lauftechnik achten oder Erholung priorisieren." });
            }
        }

        // --- Training monotony / strain (Foster): same load every day is risky ---
        if (activities is not null)
        {
            var daily = new double[7];
            foreach (var act in activities)
            {
                if (!DateOnly.TryParse(act.Date, out var ad)) continue;
                var back = today.DayNumber - ad.DayNumber;
                if (back is >= 0 and <= 6) daily[back] += LoadModel.ActivityLoad(act);
            }
            var total = daily.Sum();
            var trainingDays = daily.Count(x => x > 0);
            if (trainingDays >= 1)   // activity history is present → this check counts as "run"
            {
                checksRun++;
                if (total >= 150 && trainingDays >= 3)
                {
                    var mean = daily.Average();
                    var sd = Math.Sqrt(daily.Select(x => (x - mean) * (x - mean)).Average());
                    var monotony = sd > 0.01 ? mean / sd : 3.0; // identical daily load → maximal monotony
                    if (monotony >= 2.0)
                        alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "🪨", Title = "Hohe Trainingsmonotonie",
                            Detail = $"Monotonie {monotony:0.0} (Ziel <2,0): deine Tage ähneln sich zu sehr. Mehr Kontrast – klar harte und klar lockere Tage plus echte Ruhetage – senkt das Übertrainings-/Verletzungsrisiko." });
                }
            }
        }

        // --- Easy-run pace discipline: runs the athlete's own effort classification already treats
        // as "easy" (low HR, same distance-dependent cutoff PaceCalculator uses to build the easy
        // zone itself) but that were paced faster than the prescribed easy band. This is the common,
        // hard-to-notice training error where HR looks fine but the legs never get the true recovery
        // stimulus an easy day is for — moderate-intensity creep that erodes the hard/easy contrast. ---
        if (activities is not null && paces?.ByKey("easy") is { } easyZone)
        {
            var easyRuns = activities
                .Where(a => a.IsRun && a.DistanceKm is >= 3 and <= 25 && a.DurationMin is > 0
                            && a.AverageHr is int hr && hr > 0 && hr < (a.DistanceKm >= 8 ? 150 : 155)
                            && !(a.AnaerobicEffect is double ae && ae >= 2.0) // fartlek/net-downhill: HR can average low despite a fast pace
                            && DateOnly.TryParse(a.Date, out var ad) && ad <= today && today.DayNumber - ad.DayNumber <= 14)
                .OrderByDescending(a => a.Date, StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (easyRuns.Count >= 4)
            {
                checksRun++;
                double Pace(ActivitySummary a) => a.DurationMin!.Value * 60.0 / a.DistanceKm!.Value;
                // The zone's own low bound can itself be dragged faster by a chronic overpacing habit
                // (it's partly derived FROM these same recent runs' paces) — comparing against it alone
                // would let sustained drift quietly raise its own bar and mask exactly the pattern this
                // alert exists to catch. FormulaEasyLowSecPerKm is threshold-anchored and drift-immune;
                // taking the slower (larger) of the two prevents that self-referential blind spot.
                var tooFastThreshold = paces.FormulaEasyLowSecPerKm is int fl ? Math.Max(easyZone.LowSecPerKm, fl) : easyZone.LowSecPerKm;
                var tooFast = easyRuns.Count(a => Pace(a) < tooFastThreshold);
                if (tooFast >= 3)
                    alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "🐇", Title = "Lockere Läufe oft zu schnell",
                        Detail = $"{tooFast} von {easyRuns.Count} zuletzt lockeren Läufen (niedrige Herzfrequenz) waren schneller als {PaceZone.Fmt(tooFastThreshold)} — der untere Rand deiner Locker-Zone. Für echten Erholungsnutzen bewusst langsamer laufen, auch wenn es sich leicht anfühlt — sonst verschwimmt der Kontrast zu den harten Tagen." });
            }
        }

        // --- Taper execution: is weekly load actually declining during the taper window? A common
        // taper-anxiety mistake is quietly keeping (or even raising) volume out of fear of losing
        // fitness — exactly what the taper is designed to prevent by trading a little fitness for a
        // lot of freshness. Skip once inside the final few days (daysToRace &lt; 5): by then there's
        // nothing meaningful left to reduce, and a week-over-week comparison would just be noise.
        // The pre-taper baseline is the BEST (highest-load) of the three weeks before "this week", not
        // just the single immediately-preceding one — a deliberate cutback/step-back week placed right
        // before taper start would otherwise look like the peak and demand a reduction that's already
        // been made (the same "don't let a single lighter week masquerade as the real baseline" idea
        // CoachEngine's longest-run lookback already applies for the same reason). Also stays quiet
        // when the existing ACWR check already reports low acute load as taper-appropriate — firing
        // both would read as contradictory advice on the same report. ---
        if (activities is not null && daysToRace is int dtr3 && dtr3 is >= 5 and <= 21
            && !(acwr is double aLow && aLow < 0.8))
        {
            var loadByDay = new double[28];
            foreach (var act in activities)
            {
                if (!DateOnly.TryParse(act.Date, out var ad)) continue;
                var back = today.DayNumber - ad.DayNumber;
                if (back is >= 0 and <= 27) loadByDay[back] += LoadModel.ActivityLoad(act);
            }
            var thisWeek = loadByDay.Take(7).Sum();
            var preTaperPeak = new[] { loadByDay.Skip(7).Take(7).Sum(), loadByDay.Skip(14).Take(7).Sum(), loadByDay.Skip(21).Take(7).Sum() }.Max();
            if (preTaperPeak >= 150) // only judge against a week with meaningful training to taper from
            {
                checksRun++;
                var changePct = (thisWeek - preTaperPeak) / preTaperPeak * 100.0;
                if (changePct > -5) // flat or rising vs. the real pre-taper peak, when it should be clearly declining
                    alerts.Add(new HealthAlert { Level = AlertLevel.Amber, Icon = "📉", Title = "Taper reduziert die Last noch nicht",
                        Detail = $"Trainingslast diese Woche {changePct:+0;-0}% ggü. deiner Spitzenwoche vor dem Taper — in der Taper-Phase ({dtr3} Tage bis zum Rennen) sollte der Umfang spürbar sinken (üblich 20–40 % weniger pro Woche). Umfang jetzt bewusst zurücknehmen, damit du frisch statt übertrainiert an den Start gehst." });
            }
        }

        // --- Acute illness / over-reaching pattern (latest day) ---
        if (rhrBase is double rb2 && hrvBase is double hb2 &&
            latest.RestingHeartRate is int lr && latest.HrvLastNight is int lh && latest.SleepHours is double ls)
        {
            var rhrUp = lr - rb2 >= 6;
            var hrvDown = (lh - hb2) / hb2 * 100.0 <= -10;
            if (rhrUp && hrvDown && ls < 6.5 && !alerts.Any(x => x.Title.StartsWith("Ruhepuls")))
                alerts.Add(new HealthAlert { Level = AlertLevel.Red, Icon = "🤒", Title = "Mögliches Krankheits-/Überlastungsmuster",
                    Detail = $"Ruhepuls hoch ({lr} bpm), HRV niedrig ({lh} ms) und wenig Schlaf ({ls:0.0} h) treffen zusammen. Heute Ruhe, kein hartes Training – im Zweifel auskurieren." });
        }

        // De-duplicate, order by severity (Red first), and add an all-clear if nothing fired.
        var result = alerts
            .GroupBy(x => x.Title).Select(g => g.First())
            .OrderByDescending(x => (int)x.Level)
            .ToList();

        if (result.Count == 0)
            result.Add(checksRun > 0
                ? new HealthAlert { Level = AlertLevel.Good, Icon = "✅", Title = "Keine Warnsignale",
                    Detail = "Ruhepuls, HRV, Schlaf und Trainingslast liegen in deinem normalen Bereich. Grünes Licht – weiter so." }
                : new HealthAlert { Level = AlertLevel.Info, Icon = "ℹ️", Title = "Zu wenig Verlaufsdaten",
                    Detail = "Noch nicht genug Historie für eine belastbare Beurteilung – die Frühwarnsignale werden aussagekräftiger, sobald sich mehr Tage angesammelt haben." });

        return result;
    }

    private static double? Avg(IEnumerable<DayMetrics> days, Func<DayMetrics, int?> selector)
    {
        var v = days.Select(selector).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }
}
