using GarminMcp.Core.Coaching;
using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class CoachingFeaturesTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    [Theory]
    [InlineData("sub 3:45", 13500)]
    [InlineData("3:45", 13500)]
    [InlineData("3:45:30", 13530)]
    [InlineData("unter 4:00", 14400)]
    [InlineData("3h45", 13500)]
    [InlineData("kein ziel", null)]
    [InlineData(null, null)]
    public void GoalParser_ParsesCommonFormats(string? input, int? expected)
        => Assert.Equal(expected, GoalParser.ToSeconds(input));

    [Fact]
    public void PaceCalculator_DerivesZonesFromMarathon()
    {
        var zones = PaceCalculator.FromPredictions(new RacePrediction { MarathonSeconds = 13500 });
        Assert.NotNull(zones);
        Assert.NotEmpty(zones!.Zones);
        // 13500 s / 42.195 km ≈ 320 s/km = 5:20/km
        Assert.Equal(320, zones.MarathonPaceSecPerKm);
        Assert.NotNull(zones.ByKey("easy"));
        Assert.Equal("5:20/km", PaceZone.Fmt(320));
    }

    [Fact]
    public void Coach_RecognisesSessionAlreadyDoneToday()
    {
        var days = new List<DayMetrics>
        {
            new() { Date = "2026-06-30", RestingHeartRate = 50, HrvLastNight = 66, SleepHours = 7.6, BodyBatteryHigh = 82 },
        };
        var plan = new TrainingPlanView
        {
            Today = { new PlannedWorkout { Date = "2026-06-30", Type = SessionType.Quality, Title = "Tempo 10k", DistanceKm = 10 } },
        };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-30", Type = "running", Name = "Tempo", DistanceKm = 10, DurationMin = 50, AverageHr = 162 },
        };

        var c = CoachEngine.Evaluate(Today, days, new TrainingReadiness { Score = 70 }, null, plan, null, activities: activities);

        Assert.True(c.TrainedToday);
        Assert.Contains("Erledigt", c.Headline);
        Assert.StartsWith("✅", c.PlanNote);
        Assert.Null(c.TodayTargetPace); // no second target once the day's work is done
    }

    [Fact]
    public void Coach_KeepsPrescribingWhenNotYetTrained()
    {
        var days = new List<DayMetrics>
        {
            new() { Date = "2026-06-30", RestingHeartRate = 50, HrvLastNight = 66, SleepHours = 7.6, BodyBatteryHigh = 82 },
        };
        var plan = new TrainingPlanView
        {
            Today = { new PlannedWorkout { Date = "2026-06-30", Type = SessionType.Easy, Title = "Easy 8k", DistanceKm = 8 } },
        };

        var c = CoachEngine.Evaluate(Today, days, new TrainingReadiness { Score = 75 }, null, plan,
            new RacePrediction { MarathonSeconds = 13500 }, activities: new List<ActivitySummary>());

        Assert.False(c.TrainedToday);
        Assert.DoesNotContain("Erledigt", c.Headline);
        Assert.NotNull(c.TodayTargetPace); // easy pace target offered
    }

    [Fact]
    public void Coach_ProjectsGoal()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30", RestingHeartRate = 50 } };

        var onTrack = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13500 }, goal: "sub 3:45");
        Assert.True(onTrack.OnTrackForGoal);
        Assert.Equal(0, onTrack.GoalGapSeconds);

        var behind = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13800 }, goal: "sub 3:45");
        Assert.False(behind.OnTrackForGoal);
        Assert.Equal(300, behind.GoalGapSeconds);
    }

    [Fact]
    public void AlertEngine_FlagsElevatedRestingHeartRate()
    {
        var days = new List<DayMetrics>();
        for (var i = 0; i < 30; i++)
            days.Add(new DayMetrics { Date = Today.AddDays(-29 + i).ToString("yyyy-MM-dd"), RestingHeartRate = 50, HrvLastNight = 60, SleepHours = 7.6 });
        // last three days clearly elevated
        days[^1].RestingHeartRate = 58; days[^2].RestingHeartRate = 58; days[^3].RestingHeartRate = 58;

        var alerts = AlertEngine.Evaluate(days, null, Today);

        Assert.Contains(alerts, a => a.Title.Contains("Ruhepuls") && a.Level is AlertLevel.Red or AlertLevel.Amber);
    }

    [Fact]
    public void Renderer_WarnsOnStaleData()
    {
        var report = new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch,
            Coaching = new DailyCoaching { Date = "2026-06-30" },
            Days = new() { new DayMetrics { Date = "2026-06-24", RestingHeartRate = 50 } },
        };
        var md = MarkdownRenderer.Render(report, 14);
        Assert.Contains("veraltet", md);
    }

    [Fact]
    public void Renderer_WarnsWhenNoData()
    {
        var report = new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch,
            Coaching = new DailyCoaching { Date = "2026-06-30" },
            Days = new() { new DayMetrics { Date = "2026-06-30" } }, // no actual metrics
        };
        var md = MarkdownRenderer.Render(report, 14);
        Assert.Contains("Keine Daten", md);
    }

    [Fact]
    public void AlertEngine_FlagsHighMonotony()
    {
        var days = new List<DayMetrics>();
        var acts = new List<ActivitySummary>();
        for (var k = 0; k < 7; k++) // identical load every day → maximal monotony
        {
            days.Add(new DayMetrics { Date = Today.AddDays(-k).ToString("yyyy-MM-dd") });
            acts.Add(new ActivitySummary { Id = k + 1, Date = Today.AddDays(-k).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 45, AverageHr = 145 });
        }

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.Contains(alerts, a => a.Title.Contains("Trainingsmonotonie"));
    }

    [Fact]
    public void AlertEngine_AllClearWithLightActivityWeek()
    {
        // A single light run: a check ran (activities present) but nothing is wrong → all-clear, not "too little data".
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>
        {
            new() { Id = 1, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 3, DurationMin = 20, AverageHr = 130 },
        };

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.Single(alerts);
        Assert.Equal(AlertLevel.Good, alerts[0].Level);
    }

    [Fact]
    public void AlertEngine_AllClearWhenNormal()
    {
        var days = new List<DayMetrics>();
        for (var i = 0; i < 30; i++)
            days.Add(new DayMetrics { Date = Today.AddDays(-29 + i).ToString("yyyy-MM-dd"), RestingHeartRate = 50, HrvLastNight = 60, SleepHours = 7.6 });

        var alerts = AlertEngine.Evaluate(days, null, Today);

        Assert.Single(alerts);
        Assert.Equal(AlertLevel.Good, alerts[0].Level);
    }
}
