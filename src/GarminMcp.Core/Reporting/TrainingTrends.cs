namespace GarminMcp.Core.Reporting;

/// <summary>A metric's current (last 7 days) value vs. its value ~4 weeks ago, for trend comparison.
/// <c>Past</c> is null when there isn't enough history yet to compare against.</summary>
public sealed record TrendPoint(double Current, double? Past);

/// <summary>
/// 4-week training trend snapshot: the same current-vs-4-weeks-ago value pairs the dashboard's
/// "📈 Trends" table renders (see <see cref="MarkdownRenderer"/>), computed once here so other
/// consumers — notably the <c>garmin_training_trends</c> MCP tool — don't have to re-derive them
/// from raw day-by-day data and risk drifting out of sync with the dashboard's own numbers.
/// </summary>
public sealed class TrainingTrends
{
    public TrendPoint? RestingHeartRate { get; set; }
    public TrendPoint? Hrv { get; set; }
    public TrendPoint? SleepScore { get; set; }
    public TrendPoint? SpO2 { get; set; }
    public TrendPoint? WeightKg { get; set; }
    public TrendPoint? BodyFatPercent { get; set; }
    public TrendPoint? MuscleMassKg { get; set; }
    public TrendPoint? VisceralFatRating { get; set; }
    public TrendPoint? Vo2Max { get; set; }
    public TrendPoint? FitnessCtl { get; set; }
    public TrendPoint? MarathonPredictionSeconds { get; set; }

    public static TrainingTrends Compute(GarminReport report, DateOnly today)
    {
        var rStart = today.AddDays(-6); var rEnd = today;                        // last 7 days
        var pStart = today.AddDays(-34); var pEnd = today.AddDays(-28);          // the 7 days exactly 4 weeks ago (28 days back from each window boundary)
        var wStart = today.AddDays(-34);                                        // same start as pStart, for sparse point metrics needing the full span

        TrendPoint? AvgPointI(Func<DayMetrics, int?> sel)
        {
            var cur = MarkdownRenderer.AvgRange(report.Days, rStart, rEnd, sel);
            return cur is double c ? new TrendPoint(c, MarkdownRenderer.AvgRange(report.Days, pStart, pEnd, sel)) : null;
        }
        TrendPoint? AvgPointD(Func<DayMetrics, double?> sel)
        {
            var cur = MarkdownRenderer.AvgRange(report.Days, rStart, rEnd, sel);
            return cur is double c ? new TrendPoint(c, MarkdownRenderer.AvgRange(report.Days, pStart, pEnd, sel)) : null;
        }

        var (pmc, _, _, _) = LoadModel.Compute(report.Activities, today, 35);
        TrendPoint? ctl = null;
        if (pmc.Count >= 2 && DateOnly.TryParse(pmc[0].Date, out var pastCtlDate))
        {
            // CTL cold-starts at 0 on whichever day is the EARLIEST activity in the input, then
            // EWMAs forward with a 42-day time constant — comparing "past" (pmc[0]) against
            // "current" (pmc[^1]) is only a meaningful trend once the EWMA had genuine lead-in time
            // to converge before pmc[0]. A fresh, short-window fetch (e.g. this being called from
            // the garmin_training_trends MCP tool, which builds a standalone report rather than the
            // dashboard's long-lived merged history) can't distinguish "training only started
            // recently" from "we just didn't fetch further back" — so without real evidence of
            // lead-in, a near-zero cold-start CTL would masquerade as a large, spurious "fitness
            // improved" delta. Only trust it when at least one activity predates pmc[0]'s date by
            // that same 42-day margin.
            var earliestActivity = report.Activities
                .Select(a => DateOnly.TryParse(a.Date, out var d) ? d : (DateOnly?)null)
                .Where(d => d.HasValue).Select(d => d!.Value)
                .DefaultIfEmpty(DateOnly.MaxValue)
                .Min();
            if (earliestActivity < pastCtlDate.AddDays(-42))
                ctl = new TrendPoint(pmc[^1].Ctl, pmc[0].Ctl);
        }

        var (vo2First, vo2Last) = MarkdownRenderer.FirstLast(report.Days, wStart, today, d => d.Vo2Max);
        var vo2 = vo2Last is double vl ? new TrendPoint(vl, vo2First) : null;

        var (mFirst, mLast) = MarkdownRenderer.FirstLast(report.Days, wStart, today, d => d.MarathonSeconds is int s ? s : (double?)null);
        var marathon = mLast is double ml ? new TrendPoint(ml, mFirst) : null;

        return new TrainingTrends
        {
            RestingHeartRate = AvgPointI(d => d.RestingHeartRate),
            Hrv = AvgPointI(d => d.HrvLastNight),
            SleepScore = AvgPointI(d => d.SleepScore),
            SpO2 = AvgPointI(d => d.SpO2Avg),
            WeightKg = AvgPointD(d => d.WeightKg),
            BodyFatPercent = AvgPointD(d => d.BodyFatPercent),
            MuscleMassKg = AvgPointD(d => d.MuscleMassKg),
            VisceralFatRating = AvgPointD(d => d.VisceralFatRating),
            Vo2Max = vo2,
            FitnessCtl = ctl,
            MarathonPredictionSeconds = marathon,
        };
    }
}
