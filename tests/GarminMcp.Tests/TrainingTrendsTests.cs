using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class TrainingTrendsTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    private static GarminReport BuildReport(Action<List<DayMetrics>> populate)
    {
        var days = new List<DayMetrics>();
        for (var i = 0; i < 35; i++)
            days.Add(new DayMetrics { Date = Today.AddDays(-i).ToString("yyyy-MM-dd") });
        populate(days);
        return new GarminReport { Days = days };
    }

    [Fact]
    public void Compute_ComparesLast7Days_VsFourWeeksAgo()
    {
        var report = BuildReport(days =>
        {
            for (var i = 0; i <= 6; i++) days[i].RestingHeartRate = 45;   // last 7 days
            for (var i = 21; i <= 27; i++) days[i].RestingHeartRate = 50; // ~4 weeks ago
        });

        var trends = TrainingTrends.Compute(report, Today);

        Assert.NotNull(trends.RestingHeartRate);
        Assert.Equal(45, trends.RestingHeartRate!.Current);
        Assert.Equal(50, trends.RestingHeartRate.Past);
    }

    [Fact]
    public void Compute_ReturnsNull_ForMetricWithNoCurrentData()
    {
        var report = BuildReport(_ => { }); // no BodyFatPercent set anywhere

        var trends = TrainingTrends.Compute(report, Today);

        Assert.Null(trends.BodyFatPercent);
    }

    [Fact]
    public void Compute_ReturnsPastAsNull_WhenNotEnoughHistoryToCompareAgainst()
    {
        var report = BuildReport(days =>
        {
            for (var i = 0; i <= 6; i++) days[i].HrvLastNight = 60; // only recent data, nothing 4 weeks back
        });

        var trends = TrainingTrends.Compute(report, Today);

        Assert.NotNull(trends.Hrv);
        Assert.Equal(60, trends.Hrv!.Current);
        Assert.Null(trends.Hrv.Past);
    }

    [Fact]
    public void Compute_DerivesFitnessCtl_WhenEnoughLeadInHistoryExists()
    {
        // An activity 80 days back gives the 42-day EWMA genuine lead-in before the "past"
        // comparison point (~34 days back), so the CTL trend can be trusted.
        var report = BuildReport(_ => { });
        report.Activities.Add(new ActivitySummary { Id = 1, Date = Today.AddDays(-80).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 48, AverageHr = 140 });
        report.Activities.Add(new ActivitySummary { Id = 2, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 48, AverageHr = 140 });

        var trends = TrainingTrends.Compute(report, Today);

        Assert.NotNull(trends.FitnessCtl);
        Assert.True(trends.FitnessCtl!.Current > 0); // CTL has built up from the two logged runs
    }

    [Fact]
    public void Compute_ReturnsNullFitnessCtl_WhenNotEnoughLeadInHistory()
    {
        // Only 2 runs, both well within the fetch window itself — CTL cold-starts on the earliest
        // one (day -20), so pmc[0] (the "past" point, ~34 days back) would just be EWMA-warmup
        // near zero, not a real historical fitness level. Must NOT report a (spurious) trend here —
        // this is the exact scenario a fresh, unmerged MCP/REST call sees (no accumulated history).
        var report = BuildReport(_ => { });
        report.Activities.Add(new ActivitySummary { Id = 1, Date = Today.AddDays(-20).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 48, AverageHr = 140 });
        report.Activities.Add(new ActivitySummary { Id = 2, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 48, AverageHr = 140 });

        var trends = TrainingTrends.Compute(report, Today);

        Assert.Null(trends.FitnessCtl);
    }

    [Fact]
    public void Compute_DerivesMarathonPrediction_FirstVsLastInWindow()
    {
        var report = BuildReport(days =>
        {
            days.First(d => d.Date == Today.AddDays(-25).ToString("yyyy-MM-dd")).MarathonSeconds = 14800; // first in the ~34-day window
            days.First(d => d.Date == Today.ToString("yyyy-MM-dd")).MarathonSeconds = 14400;              // last (fastest = improved)
        });

        var trends = TrainingTrends.Compute(report, Today);

        Assert.NotNull(trends.MarathonPredictionSeconds);
        Assert.Equal(14400, trends.MarathonPredictionSeconds!.Current);
        Assert.Equal(14800, trends.MarathonPredictionSeconds.Past);
    }

    [Fact]
    public void Compute_MatchesRenderedMarkdownTrendsTable()
    {
        // TrainingTrends.Compute must stay the single source of truth AppendTrends renders from —
        // a value diverging here would mean the MCP tool and the dashboard disagree about "current".
        var report = BuildReport(days =>
        {
            for (var i = 0; i <= 6; i++) days[i].RestingHeartRate = 45;
            for (var i = 21; i <= 27; i++) days[i].RestingHeartRate = 50;
        });

        var trends = TrainingTrends.Compute(report, Today);
        var md = MarkdownRenderer.Render(report, showDays: 35, new List<ChartRef>());

        Assert.Contains($"{trends.RestingHeartRate!.Current:0} bpm", md);
    }
}
