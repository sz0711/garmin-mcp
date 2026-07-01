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
}

/// <summary>
/// Computes a first-half-vs-second-half pacing verdict from an activity's laps. Uses grade-adjusted
/// pace (see <see cref="RunningEconomy"/>) per half rather than raw pace, so an uneven elevation
/// profile between the two halves (e.g. a hill in the back half of an out-and-back) doesn't get
/// mistaken for a pacing decision the athlete didn't actually make. This is a safe use of grade
/// adjustment: both sides of the comparison are computed the same way, unlike the fixed,
/// never-adjusted thresholds elsewhere in the app that grade adjustment must NOT be compared against
/// (see PaceZones.cs/AlertEngine.cs for that lesson).
/// </summary>
public static class SplitAnalyzer
{
    // A pacing verdict needs a clear signal, not noise — small pace variance (terrain, traffic
    // lights, wind) is normal; only call it a deliberate negative/positive split beyond this margin.
    private const double EvenSplitThresholdPct = 3.0;

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

        double PaceForLaps(List<LapDto> group)
        {
            var distanceKm = group.Sum(l => l.Distance) / 1000.0;
            var durationMin = group.Sum(l => l.Duration) / 60.0;
            var elevationGainM = group.Sum(l => l.ElevationGain);
            return RunningEconomy.GradeAdjustedPaceSecPerKm(distanceKm, durationMin, elevationGainM);
        }

        var firstPace = PaceForLaps(firstHalf);
        var secondPace = PaceForLaps(secondHalf);
        // Sanity bound (2:00-15:00/km) rather than trusting the computation blindly — guards against
        // a unit mismatch (e.g. if this library's Distance/Duration fields ever turn out not to be
        // meters/seconds) silently producing a nonsensical pace instead of a visibly-skipped result.
        if (firstPace is < 120 or > 900 || secondPace is < 120 or > 900) return null;

        var pctDiff = (secondPace - firstPace) / firstPace * 100.0;
        var verdict = pctDiff > EvenSplitThresholdPct ? SplitVerdict.Positive
                    : pctDiff < -EvenSplitThresholdPct ? SplitVerdict.Negative
                    : SplitVerdict.Even;

        return new PacingAnalysis
        {
            ActivityDate = activity.Date,
            ActivityName = activity.Name,
            DistanceKm = Math.Round(totalDistance / 1000.0, 1),
            FirstHalfPaceSecPerKm = (int)Math.Round(firstPace),
            SecondHalfPaceSecPerKm = (int)Math.Round(secondPace),
            Verdict = verdict,
            PercentDifference = Math.Round(pctDiff, 1),
        };
    }
}
