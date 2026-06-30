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
        IReadOnlyList<DayMetrics> days, TrainingStatusInfo? status, DateOnly today)
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
