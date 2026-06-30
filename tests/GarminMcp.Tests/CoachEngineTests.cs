using GarminMcp.Core.Coaching;
using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class CoachEngineTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    // 7 normal prior days (HRV 70, RHR 50, sleep 7.5, BB 90) + today with the given values.
    private static List<DayMetrics> Days(int? hrv, int? rhr, double? sleep, int? bb)
    {
        var list = new List<DayMetrics>();
        for (var i = 7; i >= 1; i--)
        {
            var d = Today.AddDays(-i);
            list.Add(new DayMetrics { Date = d.ToString("yyyy-MM-dd"), HrvLastNight = 70, RestingHeartRate = 50, SleepHours = 7.5, BodyBatteryHigh = 90 });
        }
        list.Add(new DayMetrics { Date = Today.ToString("yyyy-MM-dd"), HrvLastNight = hrv, RestingHeartRate = rhr, SleepHours = sleep, BodyBatteryHigh = bb });
        return list;
    }

    private static TrainingPlanView Plan(SessionType? todayType = null, int? daysToRace = null)
    {
        var v = new TrainingPlanView { DaysToRace = daysToRace };
        if (todayType is SessionType t)
            v.Today.Add(new PlannedWorkout { Date = Today.ToString("yyyy-MM-dd"), Type = t, Title = t.ToString() });
        return v;
    }

    [Fact]
    public void Green_WhenAllNormal()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), null, null, Plan(SessionType.Easy), null);
        Assert.Equal(Readiness.Green, c.Readiness);
        Assert.Equal(SessionType.Easy, c.Recommended);
        Assert.Contains("🟢", c.Headline);
    }

    [Fact]
    public void Red_WhenRestingHrElevated()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 58, 7.5, 90), null, null, Plan(SessionType.Easy), null);
        Assert.Equal(Readiness.Red, c.Readiness);
        Assert.Contains(c.Flags, f => f.Contains("Resting HR"));
    }

    [Fact]
    public void Amber_WhenSleepShort()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 6.5, 90), null, null, Plan(SessionType.Easy), null);
        Assert.Equal(Readiness.Amber, c.Readiness);
    }

    [Fact]
    public void Red_WhenAcwrSpikes()
    {
        var status = new TrainingStatusInfo { Acwr = 1.6 };
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), null, status, Plan(SessionType.Quality), null);
        Assert.Equal(Readiness.Red, c.Readiness);
        Assert.Contains(c.Flags, f => f.Contains("ACWR"));
    }

    [Fact]
    public void IllnessTriad_RecommendsRest()
    {
        // RHR +8, HRV ~14% below baseline, sleep 5.5h
        var c = CoachEngine.Evaluate(Today, Days(60, 58, 5.5, 40), null, null, Plan(SessionType.Quality), null);
        Assert.Equal(Readiness.Red, c.Readiness);
        Assert.Equal(SessionType.Rest, c.Recommended);
        Assert.Contains(c.Flags, f => f.Contains("illness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Quality_OnRed_IsSwapped()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 58, 7.5, 90), null, null, Plan(SessionType.Quality), null);
        Assert.NotEqual(SessionType.Quality, c.Recommended);
        Assert.Contains("swap", c.PlanNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GarminReadiness_Overrides_ComputedGreen()
    {
        var readiness = new TrainingReadiness { Score = 30, Level = "LOW" };
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), readiness, null, Plan(SessionType.Easy), null);
        Assert.Equal(Readiness.Red, c.Readiness);
        Assert.Equal(30, c.ReadinessScore);
    }

    [Fact]
    public void TaperNote_InRaceWeek()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), null, null, Plan(SessionType.Easy, daysToRace: 5), null);
        Assert.NotNull(c.TaperNote);
        Assert.Contains("Race week", c.TaperNote!);
    }

    [Fact]
    public void Evaluate_PopulatesNutrition_WithWeight()
    {
        var c = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), null, null, Plan(SessionType.Long), null, goal: null, weightKg: 72);
        Assert.NotNull(c.Nutrition);
        Assert.Equal(72, c.Nutrition!.WeightKg);
        Assert.True(c.Nutrition.CarbsG > c.Nutrition.ProteinG);
    }

    [Fact]
    public void Renderers_IncludeCoachingBlock()
    {
        var report = new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch,
            Coaching = CoachEngine.Evaluate(Today, Days(70, 50, 7.5, 90), null, null, Plan(SessionType.Easy), null),
            CoachInsight = "Heute locker laufen, alles im grünen Bereich.",
            Days = { new DayMetrics { Date = Today.ToString("yyyy-MM-dd"), RestingHeartRate = 50, Steps = 9000 } },
        };

        var md = MarkdownRenderer.Render(report);
        Assert.Contains("🧠 Coach", md);
        Assert.Contains("locker laufen", md);

        var html = HtmlRenderer.Render(report);
        Assert.Contains("locker laufen", html);
    }
}
