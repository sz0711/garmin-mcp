using Garmin.Connect.Models;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

/// <summary>Within-run pacing verdict: negative split (2nd half faster — the textbook-good marathon
/// pacing strategy), even, or positive split (2nd half slower — "fading", the most common marathon
/// pacing mistake).</summary>
public enum SplitVerdict { Negative, Even, Positive }

/// <summary>Pacing analysis for a single (typically long) run, derived from its lap/split data.</summary>
public sealed class PacingAnalysis
{
    public string ActivityDate { get; set; } = "";
    public string? ActivityName { get; set; }
    public double DistanceKm { get; set; }
    public int FirstHalfPaceSecPerKm { get; set; }
    public int SecondHalfPaceSecPerKm { get; set; }
    public SplitVerdict Verdict { get; set; }
    public double PercentDifference { get; set; } // positive = second half slower
    /// <summary>Aerobic decoupling / "cardiac drift": how much HR-per-pace efficiency (see
    /// <see cref="RunningEconomy.EfficiencyFactor"/>) worsened from the first half to the second.
    /// Positive = heart rate crept up relative to pace (normal in small amounts on long runs; a
    /// large value can flag an inadequate aerobic base, dehydration, or heat). Null when heart-rate
    /// data isn't available for the laps.</summary>
    public double? AerobicDecouplingPercent { get; set; }
}

/// <summary>
/// Computes a first-half-vs-second-half pacing verdict from an activity's laps. Uses grade-adjusted
/// pace (see <see cref="RunningEconomy"/>) per half rather than raw pace, so an uneven elevation
/// profile between the two halves (e.g. a hill in the back half of an out-and-back) doesn't get
/// mistaken for a pacing decision the athlete didn't actually make. This is a safe use of grade
/// adjustment: both sides of the comparison are computed the same way, unlike the fixed,
/// never-adjusted thresholds elsewhere in the app that grade adjustment must NOT be compared against
/// (see PaceZones.cs/AlertEngine.cs for that lesson). Also derives aerobic decoupling from the same
/// laps — no extra Garmin API call, since heart rate is already part of the split data.
/// </summary>
public static class SplitAnalyzer
{
    // A pacing verdict needs a clear signal, not noise — small pace variance (terrain, traffic
    // lights, wind) is normal; only call it a deliberate negative/positive split beyond this margin.
    private const double EvenSplitThresholdPct = 3.0;

    private readonly record struct HalfAggregate(double DistanceKm, double DurationMin, double ElevationGainM, double? AvgHr);

    public static PacingAnalysis? Analyze(GarminActivitySplits splits, ActivitySummary activity)
    {
        var laps = splits.LapDtOs?
            .Where(l => l.Distance > 0 && l.Duration > 0)
            .OrderBy(l => l.LapIndex)
            .ToList();
        if (laps is null || laps.Count < 4) return null; // too few laps to split meaningfully into two real halves

        var totalDistance = laps.Sum(l => l.Distance);
        var halfDistance = totalDistance / 2.0;

        var firstHalf = new List<LapDto>();
        var secondHalf = new List<LapDto>();
        double cumulative = 0;
        foreach (var lap in laps)
        {
            cumulative += lap.Distance;
            (cumulative <= halfDistance ? firstHalf : secondHalf).Add(lap);
        }
        // Guard against a degenerate split (e.g. one giant lap covering more than half the run alone).
        if (firstHalf.Count == 0 || secondHalf.Count == 0) return null;

        var first = Aggregate(firstHalf);
        var second = Aggregate(secondHalf);

        var firstPace = RunningEconomy.GradeAdjustedPaceSecPerKm(first.DistanceKm, first.DurationMin, first.ElevationGainM);
        var secondPace = RunningEconomy.GradeAdjustedPaceSecPerKm(second.DistanceKm, second.DurationMin, second.ElevationGainM);
        // Sanity bound (2:00-15:00/km) rather than trusting the computation blindly — guards against
        // a unit mismatch (e.g. if this library's Distance/Duration fields ever turn out not to be
        // meters/seconds) silently producing a nonsensical pace instead of a visibly-skipped result.
        if (firstPace is < 120 or > 900 || secondPace is < 120 or > 900) return null;

        var pctDiff = (secondPace - firstPace) / firstPace * 100.0;
        var verdict = pctDiff > EvenSplitThresholdPct ? SplitVerdict.Positive
                    : pctDiff < -EvenSplitThresholdPct ? SplitVerdict.Negative
                    : SplitVerdict.Even;

        double? decoupling = null;
        if (first.AvgHr is double fHr && second.AvgHr is double sHr)
        {
            var efFirst = RunningEconomy.EfficiencyFactor(first.DistanceKm, first.DurationMin, (int)Math.Round(fHr), first.ElevationGainM);
            var efSecond = RunningEconomy.EfficiencyFactor(second.DistanceKm, second.DurationMin, (int)Math.Round(sHr), second.ElevationGainM);
            if (efFirst is > 0 && efSecond is > 0)
                decoupling = Math.Round((efFirst.Value - efSecond.Value) / efFirst.Value * 100.0, 1);
        }

        return new PacingAnalysis
        {
            ActivityDate = activity.Date,
            ActivityName = activity.Name,
            DistanceKm = Math.Round(totalDistance / 1000.0, 1),
            FirstHalfPaceSecPerKm = (int)Math.Round(firstPace),
            SecondHalfPaceSecPerKm = (int)Math.Round(secondPace),
            Verdict = verdict,
            PercentDifference = Math.Round(pctDiff, 1),
            AerobicDecouplingPercent = decoupling,
        };
    }

    private static HalfAggregate Aggregate(List<LapDto> group)
    {
        var durationSec = group.Sum(l => l.Duration);
        // Average HR only over laps that actually report one (Garmin's "no HR sensor data"
        // convention for this DTO is 0, not null) -- averaging the 0s in with real values would
        // silently drag the result down, and a coverage floor alone can't catch a HALF where only a
        // few laps have real HR: those zero laps would still count toward the denominator and pull a
        // real average low enough to look like a plausible-but-wrong low HR rather than "no data".
        var hrLaps = group.Where(l => l.AverageHr > 0).ToList();
        var hrDurationSec = hrLaps.Sum(l => l.Duration);
        // Require most of the half's time to have real HR coverage before trusting the average at
        // all -- a couple of sensor-dropout laps mixed with real ones shouldn't produce a number.
        double? avgHr = hrDurationSec >= durationSec * 0.8
            ? hrLaps.Sum(l => l.AverageHr * l.Duration) / hrDurationSec
            : null;
        // Final plausibility bound on the result itself (not just its inputs), same spirit as the
        // pace sanity bound above -- guards against a corrupt individual HR reading skewing the
        // weighted average past the point of being physiologically meaningful.
        if (avgHr is < 30 or > 220) avgHr = null;
        return new HalfAggregate(
            group.Sum(l => l.Distance) / 1000.0,
            durationSec / 60.0,
            group.Sum(l => l.ElevationGain),
            avgHr);
    }
}
