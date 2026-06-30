using GarminMcp.Core.Coaching;
using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class DashboardLayoutTests
{
    [Fact]
    public void TrainingWeek_SummarizesCurrentAndPrevious()
    {
        var today = new DateOnly(2026, 6, 30); // Tuesday
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-29", Type = "running", DistanceKm = 10, DurationMin = 50, ElevationGainM = 80 },
            new() { Id = 2, Date = "2026-06-30", Type = "running", DistanceKm = 8, DurationMin = 40, ElevationGainM = 40 },
            new() { Id = 3, Date = "2026-06-23", Type = "running", DistanceKm = 12, DurationMin = 60, ElevationGainM = 100 }, // prev week
        };
        var days = new List<DayMetrics>
        {
            new() { Date = "2026-06-29", IntensityMinutes = 30 },
            new() { Date = "2026-06-30", IntensityMinutes = 20 },
            new() { Date = "2026-06-23", IntensityMinutes = 45 },
        };

        var (cur, prev) = TrainingWeek.Summarize(activities, days, today);

        Assert.Equal(2, cur.Sessions);
        Assert.Equal(18, cur.Km);
        Assert.Equal(10, cur.LongestKm);
        Assert.Equal(50, cur.IntensityMinutes);
        Assert.Equal(1, prev.Sessions);
        Assert.Equal(12, prev.Km);
    }

    [Fact]
    public void Render_IncludesNewSections_AndWritesSample()
    {
        var report = SampleReport();
        var md = MarkdownRenderer.Render(report, showDays: 21);

        Assert.Contains("Readiness", md);                 // KPI bar / coach
        Assert.Contains("🗓️ Wochenüberblick", md);
        Assert.Contains("📈 Entwicklung", md);
        Assert.Contains("Ø7T", md);                       // rolling-average overlay
        Assert.Contains("<details>", md);                 // collapsible
        Assert.Contains("Phasen: Tief", md);              // sleep stages
        Assert.Contains("Fitness", md);                   // Form (CTL/ATL) chart + line
        Assert.Contains("Planerfüllung", md);             // plan adherence
        Assert.Contains("Zubettgeh", md);                 // bedtime chart
        Assert.Contains("pie", md);                       // sport split pie
        Assert.Contains("Schlaf-Konsistenz", md);

        // Dump a sample for visual inspection (outside the repo).
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "garmin-dashboard-sample.md"), md);
    }

    private static GarminReport SampleReport()
    {
        var start = new DateOnly(2026, 6, 10);
        var rng = new[] { 0, 1, -1, 2, -2, 1, 0, -1, 2, 1, -1, 0, 1, -2, 2, 0, -1, 1, 0, -1, 1 };
        var days = new List<DayMetrics>();
        for (var i = 0; i < 21; i++)
        {
            var date = start.AddDays(i);
            days.Add(new DayMetrics
            {
                Date = date.ToString("yyyy-MM-dd"),
                RestingHeartRate = 49 + rng[i],
                HrvLastNight = 68 + rng[i] * 2,
                HrvStatus = "BALANCED",
                SleepHours = Math.Round(7.0 + rng[i] * 0.2, 1),
                SleepDeepMin = 80, SleepLightMin = 230, SleepRemMin = 90, SleepAwakeMin = 18,
                Steps = 9000 + i * 120,
                StressAvg = 30 + rng[i] * 3,
                BodyBatteryHigh = 88 + rng[i], BodyBatteryLow = 22 + rng[i],
                Calories = 2400 + i * 10,
                IntensityMinutes = (i % 3 == 0) ? 45 : 10,
                BedtimeHour = Math.Round(23.0 + rng[i] * 0.15, 2),
            });
        }
        // accumulated metrics for the most recent days
        days[^1].Vo2Max = 54; days[^2].Vo2Max = 53.8; days[^3].Vo2Max = 53.9;
        days[^1].Acwr = 1.1; days[^2].Acwr = 1.0; days[^3].Acwr = 0.95;
        days[^1].ReadinessScore = 72; days[^2].ReadinessScore = 65; days[^3].ReadinessScore = 80;
        days[^1].MarathonSeconds = 13500; days[^2].MarathonSeconds = 13560;

        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-28", Name = "Long Run", Type = "running", DistanceKm = 18, DurationMin = 105, ElevationGainM = 180, Calories = 1200, AverageHr = 150 },
            new() { Id = 2, Date = "2026-06-30", Name = "Tempo", Type = "running", DistanceKm = 10, DurationMin = 52, ElevationGainM = 60, Calories = 700, AverageHr = 162 },
            new() { Id = 3, Date = "2026-06-26", Name = "Easy", Type = "running", DistanceKm = 8, DurationMin = 45, ElevationGainM = 40, Calories = 520, AverageHr = 138 },
            new() { Id = 4, Date = "2026-06-22", Name = "Long Run", Type = "running", DistanceKm = 16, DurationMin = 95, ElevationGainM = 150, Calories = 1050, AverageHr = 148 },
        };

        var coaching = CoachEngine.Evaluate(
            new DateOnly(2026, 6, 30), days, new TrainingReadiness { Score = 72, Level = "MODERATE" },
            new TrainingStatusInfo { StatusPhrase = "PRODUCTIVE_1", Vo2Max = 54, Acwr = 1.1 },
            new TrainingPlanView { Today = { new PlannedWorkout { Date = "2026-06-30", Type = SessionType.Quality, Title = "Tempo 10k" } }, DaysToRace = 26, RaceDate = "2026-07-26" },
            new RacePrediction { MarathonSeconds = 13500 }, goal: "sub 3:45", weightKg: 70);
        coaching.Ctl = 46; coaching.Atl = 41; coaching.Tsb = 5;
        coaching.PlannedThisWeek = 4; coaching.DoneThisWeek = 3;
        coaching.SleepConsistencyMin = 24;

        return new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch,
            Coaching = coaching,
            CoachInsight = "Heute eine kontrollierte Tempo-Einheit – deine Erholung trägt das.",
            Days = days,
            Activities = activities,
        };
    }
}
