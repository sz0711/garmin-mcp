using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

/// <summary>
/// Elevation- and effort-aware running metrics computed purely from data already extracted per
/// activity (distance, duration, elevation gain, heart rate) — no new Garmin API calls needed.
/// </summary>
public static class RunningEconomy
{
    /// <summary>
    /// Grade-adjusted pace (sec per flat-equivalent km): the pace this effort would correspond to
    /// on flat ground. Climbing costs roughly the same energy as covering extra flat distance — a
    /// commonly-cited approximation (in the range Minetti's energy-cost-of-running-on-gradient
    /// research supports for moderate grades) is that 1 m of elevation gain costs about as much as
    /// 10 extra flat meters. This uses TOTAL elevation gain over the whole activity (the only signal
    /// available), not a per-segment gradient profile, so it's a rough, average-effort correction —
    /// not a precise per-kilometer grade-adjusted-pace model. Good enough to stop a genuinely hilly
    /// run's naturally slower raw pace from being mistaken for a change in effort/fitness.
    /// </summary>
    public static double GradeAdjustedPaceSecPerKm(double distanceKm, double durationMin, double? elevationGainM)
    {
        if (distanceKm <= 0 || durationMin <= 0) return 0;
        var adjustedKm = distanceKm + (elevationGainM ?? 0) * 0.01; // 1 m gain ~= 10 extra flat meters
        return adjustedKm > 0 ? durationMin * 60.0 / adjustedKm : durationMin * 60.0 / distanceKm;
    }

    /// <summary>
    /// Efficiency Factor: grade-adjusted speed (m/min) per heartbeat. A classic, VO2max-test-free
    /// endurance-fitness proxy (popularized by Joe Friel for cycling/running) — a RISING EF at
    /// similar effort over weeks indicates improving aerobic fitness (more speed per heartbeat);
    /// a falling EF can indicate fatigue/detraining. Independent of and complementary to VO2max
    /// (which Garmin only updates occasionally) since it's derived fresh from every run.
    /// </summary>
    public static double? EfficiencyFactor(double distanceKm, double durationMin, int? avgHr, double? elevationGainM)
    {
        if (avgHr is not int hr || hr <= 0 || durationMin <= 0) return null;
        var adjustedKm = distanceKm + (elevationGainM ?? 0) * 0.01;
        if (adjustedKm <= 0) return null;
        var speedMPerMin = adjustedKm * 1000.0 / durationMin;
        return Math.Round(speedMPerMin / hr, 2);
    }

    /// <summary>Grade-adjusted pace for an <see cref="ActivitySummary"/>, or null if distance/duration
    /// aren't both known.</summary>
    public static double? GradeAdjustedPaceSecPerKm(ActivitySummary a) =>
        a.DistanceKm is > 0 && a.DurationMin is > 0
            ? GradeAdjustedPaceSecPerKm(a.DistanceKm.Value, a.DurationMin.Value, a.ElevationGainM)
            : null;
}
