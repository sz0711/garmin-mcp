using System.Globalization;
using System.Text.RegularExpressions;
using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

/// <summary>A single training pace band (seconds per km).</summary>
public sealed record PaceZone(string Name, string Key, int LowSecPerKm, int HighSecPerKm)
{
    public string Range => LowSecPerKm == HighSecPerKm
        ? Fmt(LowSecPerKm)
        : $"{Fmt(LowSecPerKm)}–{Fmt(HighSecPerKm)}";

    public static string Fmt(int secPerKm) => $"{secPerKm / 60}:{secPerKm % 60:00}/km";
}

/// <summary>Training pace bands derived from Garmin race predictions.</summary>
public sealed class PaceZones
{
    public List<PaceZone> Zones { get; set; } = new();
    public int? MarathonPaceSecPerKm { get; set; }

    // The formula-only (threshold-anchored) easy-zone floor, computed independent of any observed-
    // pace data. Callers that need to judge whether a run was "too fast for easy" (e.g. AlertEngine)
    // should use this rather than Zones["easy"].LowSecPerKm alone: the zone's actual low bound can be
    // dragged faster by a chronic overpacing habit (since it's partly derived FROM the athlete's own
    // recent easy-run paces), which would let sustained drift quietly raise its own bar and silently
    // stop triggering exactly when the drift is most real. This floor never moves with that drift.
    public int? FormulaEasyLowSecPerKm { get; set; }

    public PaceZone? ByKey(string key) => Zones.FirstOrDefault(z => z.Key == key);
}

/// <summary>
/// Derives easy/marathon/threshold/interval training paces from Garmin's race-time
/// predictions (5K/10K/HM/M). Pragmatic Daniels-style anchoring: intervals ≈ 5K pace,
/// threshold ≈ half-marathon/10K pace, marathon pace from the marathon prediction. Returns
/// null if there are no usable predictions.
///
/// Easy/Recovery pace prefers the athlete's OWN demonstrated easy-effort pace (from recent
/// low-HR runs) over a marathon-anchored formula. Why: the marathon prediction extrapolates to
/// 42.2 km and can be notably conservative for an athlete who hasn't yet built marathon-specific
/// endurance (see CoachEngine's EnduranceCaveat) — a marathon-anchored easy zone inherits that
/// conservatism and can end up slower than paces the athlete already runs comfortably. Threshold
/// (from a real recent HM/10K, when available) is a more reliable anchor and is used as the
/// fallback when there isn't yet enough easy-run history to trust.
/// </summary>
public static class PaceCalculator
{
    public static PaceZones? FromPredictions(
        RacePrediction? race, IReadOnlyList<ActivitySummary>? recentActivities = null, DateOnly? today = null)
    {
        if (race is null) return null;

        int? mp = PacePerKm(race.MarathonSeconds, 42.195);
        int? hm = PacePerKm(race.HalfMarathonSeconds, 21.0975);
        int? tenK = PacePerKm(race.TenKSeconds, 10.0);
        int? fiveK = PacePerKm(race.FiveKSeconds, 5.0);

        // Fill gaps with sensible relationships when a prediction is missing. Prefer real
        // shorter-distance predictions; the marathon-only fallbacks use VDOT-style offsets so the
        // high-intensity bands aren't compressed too slow (threshold ≈ MP−22 s, interval ≈ MP−50 s).
        mp ??= hm is int h ? h + 18 : tenK is int t ? t + 30 : fiveK is int f ? f + 45 : null;
        var threshold = hm ?? (tenK is int tt ? tt + 10 : mp is int m1 ? m1 - 22 : null);
        var interval = fiveK ?? (tenK is int t2 ? t2 - 15 : threshold is int th ? th - 28 : null);
        if (mp is null && threshold is null && interval is null) return null;

        var zones = new List<PaceZone>();
        int? formulaEasyLow = null;
        if (threshold is int thrForEasy)
        {
            var observed = ObservedEasyPaceSecPerKm(recentActivities, today);
            var formulaCenter = thrForEasy + 65;
            formulaEasyLow = formulaCenter - 15;
            // Plausibility guard: GA1/easy must be meaningfully slower than threshold pace by
            // definition. A wrist-HR sensor can under-read on a genuinely hard effort (dropout/
            // cadence-lock artifacts), which — over enough runs to pass the sample-size floor —
            // could otherwise pull the "observed" pace close to or faster than threshold. Only
            // trust it once it's at least 10 s/km slower than threshold; a smaller-but-real gap
            // (the exact scenario this feature targets — an athlete whose easy pace sits closer to
            // threshold than the generic +65 formula assumes) still passes this guard.
            var easyCenter = observed is int obs && obs >= thrForEasy + 10 ? obs : formulaCenter;
            zones.Add(new PaceZone("Locker (GA1)", "easy", easyCenter - 15, easyCenter + 20));
            zones.Add(new PaceZone("Erholung", "recovery", easyCenter + 20, easyCenter + 50));
        }
        if (mp is int marathon)
            zones.Add(new PaceZone("Marathon", "marathon", marathon, marathon));
        if (threshold is int thr)
            zones.Add(new PaceZone("Schwelle/Tempo", "threshold", thr - 5, thr + 5));
        if (interval is int itv)
            zones.Add(new PaceZone("Intervall (VO₂max)", "interval", itv - 6, itv + 4));

        return zones.Count == 0 ? null : new PaceZones { Zones = zones, MarathonPaceSecPerKm = mp, FormulaEasyLowSecPerKm = formulaEasyLow };
    }

    /// <summary>Median grade-adjusted pace (sec/km) of recent clearly-aerobic runs, or null if there
    /// isn't enough evidence to trust it over the formula. Distance bounds exclude noise (very short
    /// strides/warmups) and long runs (often paced slower than pure "easy" due to accumulated
    /// fatigue). The HR threshold mirrors CoachEngine's own distance-dependent quality-session cutoff
    /// (avgHr &gt;= 155, or &gt;= 150 once the run is 8 km or longer) so a run counts as "easy"
    /// evidence here exactly when CoachEngine itself would NOT call it a quality session. Also
    /// excludes runs with a meaningful anaerobic training effect: average HR lags/blends over a
    /// session, so a fartlek (surges + jog recoveries) or a net-downhill run can average out to a
    /// low HR while still being paced faster than a genuine easy effort — HR alone can't catch that.
    /// Uses RAW pace, not grade-adjusted pace: this feeds a plausibility guard (see FromPredictions)
    /// that compares the result against a never-grade-adjusted formula threshold. Grade-adjusting
    /// only one side of that comparison would let ordinary route elevation — not just steep terrain —
    /// systematically bias the guard, since grade adjustment always makes pace look faster
    /// (adversarially reviewed and confirmed: a fix would require recalibrating the guard itself,
    /// not just this method — safer to keep this raw and confine grade adjustment to the standalone
    /// Efficiency Factor trend in TrainingTrends.cs, which doesn't interact with any fixed threshold).</summary>
    private static int? ObservedEasyPaceSecPerKm(IReadOnlyList<ActivitySummary>? activities, DateOnly? today)
    {
        if (activities is null || activities.Count == 0) return null;
        var cutoff = today is DateOnly t ? t.AddDays(-90) : (DateOnly?)null;

        var paces = activities
            .Where(a => a.IsRun && a.DistanceKm is >= 3 and <= 25 && a.DurationMin is > 0
                        && a.AverageHr is int hr && hr > 0 && hr < (a.DistanceKm >= 8 ? 150 : 155)
                        && !(a.AnaerobicEffect is double ae && ae >= 2.0)
                        && (cutoff is null || (DateOnly.TryParse(a.Date, out var ad) && ad >= cutoff && ad <= today)))
            .Select(a => a.DurationMin!.Value * 60.0 / a.DistanceKm!.Value)
            .OrderBy(p => p)
            .ToList();

        if (paces.Count < 3) return null; // too little evidence — trust the formula instead
        var mid = paces.Count / 2;
        // True median: average the two middle values for an even-sized sample (plain integer
        // division on Count/2 would otherwise silently pick the upper-middle value only).
        var median = paces.Count % 2 == 0 ? (paces[mid - 1] + paces[mid]) / 2.0 : paces[mid];
        return (int)Math.Round(median);
    }

    /// <summary>The target pace band for the recommended session type (display text), if known.</summary>
    public static string? TargetForSession(PaceZones zones, SessionType session) => session switch
    {
        SessionType.Easy => zones.ByKey("easy")?.Range,
        SessionType.Long => zones.ByKey("easy") is { } e ? $"{e.Range} (Longrun-Tempo)" : null,
        SessionType.Race => zones.ByKey("marathon")?.Range,
        SessionType.Quality => Join(zones.ByKey("threshold")?.Range is { } t ? $"Tempo {t}" : null,
                                    zones.ByKey("interval")?.Range is { } i ? $"Intervall {i}" : null),
        _ => zones.ByKey("easy")?.Range,
    };

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : $"{a} · {b}";

    private static int? PacePerKm(int? seconds, double km) =>
        seconds is int s && s > 0 && km > 0 ? (int)Math.Round(s / km) : null;
}

/// <summary>Parses a goal-time string ("sub 3:45", "3:45:00", "3h45") into seconds.</summary>
public static class GoalParser
{
    public static int? ToSeconds(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return null;
        var g = goal.Trim().ToLowerInvariant().Replace("sub", "").Replace("unter", "").Trim();

        // h:mm:ss or h:mm
        var m = Regex.Match(g, @"(\d{1,2}):(\d{2})(?::(\d{2}))?");
        if (m.Success)
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return h * 3600 + min * 60 + sec;
        }

        // "3h45" / "3h45m"
        m = Regex.Match(g, @"(\d{1,2})\s*h\s*(\d{1,2})?");
        if (m.Success)
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return h * 3600 + min * 60;
        }
        return null;
    }
}
