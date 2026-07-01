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

    // Shared test scenario: HalfMarathonSeconds=7380 → threshold ≈ 7380/21.0975 ≈ 350 s/km (5:50/km),
    // matching the real-world case this fix addresses (see CoachEngine's EnduranceCaveat): the
    // formula fallback would anchor "Locker" at threshold+65 ≈ 415 s/km (6:55/km) — markedly slower
    // than the ~375 s/km (6:15/km) easy pace the athlete actually, comfortably runs.
    private static readonly RacePrediction PaceTestRace = new() { HalfMarathonSeconds = 7380, MarathonSeconds = 16560 };

    [Fact]
    public void PaceCalculator_EasyZone_PrefersObservedPaceOverFormula_WhenEnoughHistory()
    {
        var activities = new List<ActivitySummary>();
        for (var i = 0; i < 5; i++)
            activities.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 8, DurationMin = 50, AverageHr = 138 }); // 50*60/8 = 375 s/km

        var zones = PaceCalculator.FromPredictions(PaceTestRace, activities, Today);

        var easy = zones!.ByKey("easy")!;
        Assert.Equal(360, easy.LowSecPerKm);  // observed 375 - 15
        Assert.Equal(395, easy.HighSecPerKm); // observed 375 + 20
        // Must not regress to the old, markedly slower marathon/threshold-anchored range (~400-435).
        Assert.True(easy.HighSecPerKm < 400);
    }

    [Fact]
    public void PaceCalculator_EasyZone_FallsBackToFormula_WithTooFewEasyRuns()
    {
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 50, AverageHr = 138 },
            new() { Id = 2, Date = Today.AddDays(-3).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 8, DurationMin = 50, AverageHr = 138 },
        }; // only 2 qualifying runs — below the trust threshold of 3

        var zones = PaceCalculator.FromPredictions(PaceTestRace, activities, Today);

        // Falls back to threshold+65 (threshold ≈ 350 s/km → easy center ≈ 415 s/km), NOT the
        // faster 375 s/km the (too sparse) run history suggests.
        var easy = zones!.ByKey("easy")!;
        Assert.Equal(400, easy.LowSecPerKm);  // 415 - 15
        Assert.Equal(435, easy.HighSecPerKm); // 415 + 20
    }

    [Fact]
    public void PaceCalculator_EasyZone_ExcludesStaleAndHardEffortRuns()
    {
        var activities = new List<ActivitySummary>();
        // 5 recent runs at Quality effort (high HR) — must not count as "easy" evidence.
        for (var i = 0; i < 5; i++)
            activities.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 8, DurationMin = 40, AverageHr = 165 }); // hard effort, fast pace
        // 5 genuinely-easy runs, but 100 days old — outside the 90-day recency window.
        for (var i = 0; i < 5; i++)
            activities.Add(new ActivitySummary { Id = 10 + i, Date = Today.AddDays(-100 - i).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 8, DurationMin = 50, AverageHr = 138 });

        var zones = PaceCalculator.FromPredictions(PaceTestRace, activities, Today);

        // With no qualifying (recent + easy-effort) runs, the formula fallback applies (easy center
        // ≈ 415 s/km) — the fast, hard-effort pace (40*60/8=300 s/km) must NOT have been mistaken
        // for the athlete's easy pace.
        var easy = zones!.ByKey("easy")!;
        Assert.Equal(400, easy.LowSecPerKm);
    }

    [Fact]
    public void PaceCalculator_EasyZone_RejectsImplausibleObservedPace_TooCloseToThreshold()
    {
        // A wrist-HR sensor can under-read on a genuinely hard effort (dropout/cadence-lock
        // artifacts) — if that happened across several runs, the "observed" pace could land close
        // to or faster than threshold (350 s/km here), which is physiologically nonsensical for an
        // easy pace. The plausibility guard must reject it and fall back to the formula instead.
        var activities = new List<ActivitySummary>();
        for (var i = 0; i < 5; i++)
            activities.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 8, DurationMin = 47, AverageHr = 149 }); // 47*60/8 = 352.5 s/km — barely slower than threshold

        var zones = PaceCalculator.FromPredictions(PaceTestRace, activities, Today);

        var easy = zones!.ByKey("easy")!;
        Assert.Equal(400, easy.LowSecPerKm); // formula fallback (415-15), not the implausible ~353
    }

    [Fact]
    public void PaceCalculator_EasyZone_ObservedPace_UsesTrueMedian_ForEvenSampleSize()
    {
        // 4 qualifying runs (even count): true median averages the two middle paces, rather than
        // picking only the upper-middle one (a plain Count/2 index would silently do that).
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 138 },        // 360 s/km
            new() { Id = 2, Date = Today.AddDays(-3).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 61, AverageHr = 138 }, // 366 s/km
            new() { Id = 3, Date = Today.AddDays(-6).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 63, AverageHr = 138 }, // 378 s/km
            new() { Id = 4, Date = Today.AddDays(-9).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 64, AverageHr = 138 }, // 384 s/km
        };
        // Sorted: [360, 366, 378, 384] → true median = (366+378)/2 = 372, NOT paces[2]=378.

        var zones = PaceCalculator.FromPredictions(PaceTestRace, activities, Today);

        var easy = zones!.ByKey("easy")!;
        Assert.Equal(357, easy.LowSecPerKm); // 372 - 15
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
    public void Coach_RunDoesNotCoverPlannedStrengthSession()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30", RestingHeartRate = 50, HrvLastNight = 66, SleepHours = 7.6, BodyBatteryHigh = 82 } };
        var plan = new TrainingPlanView { Today = { new PlannedWorkout { Date = "2026-06-30", Type = SessionType.Strength, Title = "Strength Builder 1" } } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-30", Type = "running", Name = "Base", DistanceKm = 9, DurationMin = 55, AverageHr = 140 },
        };

        var c = CoachEngine.Evaluate(Today, days, new TrainingReadiness { Score = 70 }, null, plan, null, activities: activities);

        Assert.True(c.TrainedToday);
        Assert.DoesNotContain("Erledigt", c.Headline); // a run doesn't tick off the planned strength session
        Assert.Contains(c.Rationale, r => r.Contains("steht aber noch aus"));
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
    public void Coach_FlagsStrongEnduranceCaveat_WhenLongestRunFarBelowMarathonDistance()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 12 }, // nowhere near 42.2 km
        };

        var c = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.Equal(12, c.LongestRunKm);
        Assert.NotNull(c.EnduranceCaveat);
        Assert.Contains("Ausdauerbasis noch nicht bestätigt", c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_FlagsMildEnduranceCaveat_WhenLongestRunModerate()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 24 },
        };

        var c = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.Equal(24, c.LongestRunKm);
        Assert.NotNull(c.EnduranceCaveat);
        Assert.Contains("Ausdauerbasis wächst", c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_NoEnduranceCaveat_WhenLongRunAlreadyBuilt()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 30 },
        };

        var c = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.Equal(30, c.LongestRunKm);
        Assert.Null(c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_EnduranceCaveat_LooksAcrossTenWeeks_NotJustTaperWeek()
    {
        // A 32 km long run 9 weeks ago must still count even though this week (taper) only had a
        // short shakeout run — a single-week lookback would falsely flag a well-prepared athlete.
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = Today.AddDays(-63).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 32 },
            new() { Id = 2, Date = "2026-06-29", Type = "running", DistanceKm = 5 }, // this week's taper shakeout
        };

        var c = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(),
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.Equal(32, c.LongestRunKm);
        Assert.Null(c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_EnduranceCaveat_DoesNotRecommendBuildingLongRun_DuringTaper()
    {
        // Recommending a new near-marathon-distance long run during taper is sports-science-wrong
        // (blown taper, no time to recover before race day) — the caveat must switch to a purely
        // informational, taper-respecting framing instead of the "build toward 29-32 km" instruction.
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 12 }, // would normally trigger the strong caveat
        };
        var plan = new TrainingPlanView { DaysToRace = 10 }; // inside the 21-day taper window

        var c = CoachEngine.Evaluate(Today, days, null, null, plan,
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.NotNull(c.EnduranceCaveat);
        Assert.DoesNotContain("aufbauen", c.EnduranceCaveat);
        Assert.Contains("Taper", c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_EnduranceCaveat_RecommendsBuildingLongRun_WellBeforeTaper()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 12 },
        };
        var plan = new TrainingPlanView { DaysToRace = 60 }; // well outside taper — still time to act

        var c = CoachEngine.Evaluate(Today, days, null, null, plan,
            new RacePrediction { MarathonSeconds = 13500 }, activities: activities);

        Assert.NotNull(c.EnduranceCaveat);
        Assert.Contains("aufbauen", c.EnduranceCaveat);
    }

    [Fact]
    public void Coach_NoEnduranceCaveat_WhenNoRacePrediction()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30" } };
        var activities = new List<ActivitySummary>
        {
            new() { Id = 1, Date = "2026-06-25", Type = "running", DistanceKm = 10 },
        };

        var c = CoachEngine.Evaluate(Today, days, null, null, new TrainingPlanView(), race: null, activities: activities);

        Assert.Null(c.EnduranceCaveat); // nothing to caveat without a prediction to begin with
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
    public void Merge_KeepsWeeklyInsightUntilRefreshed()
    {
        var existing = new GarminReport { WeeklyInsight = "alte Woche", WeeklyInsightWeekStart = "2026-06-22" };

        // Run with no fresh weekly insight → keep the existing one (and its week stamp) in between.
        var merged = GarminReport.Merge(existing, new GarminReport { GeneratedAtUtc = DateTimeOffset.UnixEpoch });
        Assert.Equal("alte Woche", merged.WeeklyInsight);
        Assert.Equal("2026-06-22", merged.WeeklyInsightWeekStart);

        // Run that produces a fresh one for the new week → it wins, stamp updates.
        var merged2 = GarminReport.Merge(existing, new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch, WeeklyInsight = "neue Woche", WeeklyInsightWeekStart = "2026-06-29",
        });
        Assert.Equal("neue Woche", merged2.WeeklyInsight);
        Assert.Equal("2026-06-29", merged2.WeeklyInsightWeekStart);
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

    [Fact]
    public void AlertEngine_FlagsLowSpO2()
    {
        var days = new List<DayMetrics>();
        for (var i = 0; i < 3; i++)
            days.Add(new DayMetrics { Date = Today.AddDays(-i).ToString("yyyy-MM-dd"), SpO2Avg = 88 });

        var alerts = AlertEngine.Evaluate(days, null, Today);

        Assert.Contains(alerts, a => a.Title.Contains("Sauerstoffsättigung") && a.Level == AlertLevel.Red);
    }

    [Fact]
    public void AlertEngine_NoSpO2AlertWhenNormal()
    {
        var days = new List<DayMetrics>();
        for (var i = 0; i < 3; i++)
            days.Add(new DayMetrics { Date = Today.AddDays(-i).ToString("yyyy-MM-dd"), SpO2Avg = 97 });

        var alerts = AlertEngine.Evaluate(days, null, Today);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Sauerstoffsättigung"));
    }

    [Fact]
    public void AlertEngine_FlagsRunningEconomyDecline()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        // 3 most recent runs at a noticeably lower cadence than the 6 runs before them, all at the
        // same pace (8 km / 48 min = 6:00/km) so the pace-similarity gate doesn't suppress the alert.
        for (var i = 0; i < 3; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 168, DistanceKm = 8, DurationMin = 48 });
        for (var i = 3; i < 9; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 178, DistanceKm = 8, DurationMin = 48 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.Contains(alerts, a => a.Title.Contains("Laufökonomie"));
    }

    [Fact]
    public void AlertEngine_NoRunningEconomyAlertWhenCadenceStable()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 9; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 176, DistanceKm = 8, DurationMin = 48 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Laufökonomie"));
    }

    [Fact]
    public void AlertEngine_NoRunningEconomyAlertWhenPaceDiffers()
    {
        // Same cadence drop as the "decline" test, but the recent runs are a much faster tempo
        // block — the pace-similarity gate should suppress the alert rather than confusing a
        // training-mix change with fatigue-driven form breakdown.
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 3; i++) // tempo: 8 km / 36 min = 4:30/km
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 168, DistanceKm = 8, DurationMin = 36 });
        for (var i = 3; i < 9; i++) // easy: 8 km / 48 min = 6:00/km
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 178, DistanceKm = 8, DurationMin = 48 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Laufökonomie"));
    }

    [Fact]
    public void AlertEngine_NoRunningEconomyAlertWhenRunsAreStale()
    {
        // Same cadence drop, but the most recent qualifying run is 20 days old — too stale to
        // treat as a current signal.
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 3; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-20 - i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 168, DistanceKm = 8, DurationMin = 48 });
        for (var i = 3; i < 9; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-20 - i * 2).ToString("yyyy-MM-dd"), Type = "running", CadenceSpm = 178, DistanceKm = 8, DurationMin = 48 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Laufökonomie"));
    }

    private static readonly PaceZones TestPaces = new()
    {
        Zones = { new PaceZone("Locker (GA1)", "easy", 400, 435) }, // 6:40/km - 7:15/km
    };

    [Fact]
    public void AlertEngine_FlagsEasyRunsTooFast()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 4; i++)
            // 10km in 60 min = 360 s/km (6:00/km) — faster than the easy zone's 400 s/km floor.
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, paces: TestPaces);

        Assert.Contains(alerts, a => a.Title.Contains("Lockere Läufe"));
    }

    [Fact]
    public void AlertEngine_NoEasyPaceAlertWhenPacesAreAppropriate()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 4; i++)
            // 10km in 68 min = 408 s/km — inside the 400-435 easy band.
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 68, AverageHr = 140 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, paces: TestPaces);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Lockere Läufe"));
    }

    [Fact]
    public void AlertEngine_NoEasyPaceAlertWithTooFewQualifyingRuns()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 3; i++) // only 3 — below the 4-run evidence floor
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, paces: TestPaces);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Lockere Läufe"));
    }

    [Fact]
    public void AlertEngine_FlagsTaperNotReducingLoad()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        // Same load both weeks (4x 60-min runs @ HR140 in each 7-day window) — taper isn't reducing anything.
        int[] offsets = { 0, 2, 4, 6, 8, 10, 12, 13 };
        for (var i = 0; i < offsets.Length; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-offsets[i]).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 });

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, daysToRace: 14);

        Assert.Contains(alerts, a => a.Title.Contains("Taper"));
    }

    [Fact]
    public void AlertEngine_NoTaperAlertWhenLoadIsActuallyDeclining()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>
        {
            // Last week: substantial load.
            new() { Id = 1, Date = Today.AddDays(-8).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 2, Date = Today.AddDays(-10).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 3, Date = Today.AddDays(-12).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 4, Date = Today.AddDays(-13).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            // This week: much less — a real taper cut.
            new() { Id = 5, Date = Today.ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 5, DurationMin = 30, AverageHr = 140 },
        };

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, daysToRace: 14);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Taper"));
    }

    [Fact]
    public void AlertEngine_NoTaperAlertOutsideTaperWindow()
    {
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        int[] offsets = { 0, 2, 4, 6, 8, 10, 12, 13 };
        for (var i = 0; i < offsets.Length; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-offsets[i]).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 });

        // Same flat-load pattern as the "flags" test, but 40 days out — not taper season yet.
        var alerts = AlertEngine.Evaluate(days, null, Today, acts, daysToRace: 40);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Taper"));
    }

    [Fact]
    public void AlertEngine_NoTaperAlertWhenAcwrAlreadyReportsReducedLoad()
    {
        // Same flat-load, would-otherwise-fire data as AlertEngine_FlagsTaperNotReducingLoad, but the
        // existing ACWR check already says "load is falling, that's fine in taper" (Acwr < 0.8) — firing
        // the taper alert too would read as directly contradictory advice in the same report.
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>();
        int[] offsets = { 0, 2, 4, 6, 8, 10, 12, 13 };
        for (var i = 0; i < offsets.Length; i++)
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-offsets[i]).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 });
        var status = new TrainingStatusInfo { Acwr = 0.65 };

        var alerts = AlertEngine.Evaluate(days, status, Today, acts, daysToRace: 14);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Taper reduziert"));
    }

    [Fact]
    public void AlertEngine_TaperBaseline_UsesTrueUsPreTaperPeak_NotJustTheImmediatelyPrecedingWeek()
    {
        // A deliberate cutback week sits right before the taper starts (common in 3:1-style periodized
        // plans). The immediately-preceding week (back 7-13) is therefore already light — comparing
        // only against it would make an unremarkable taper week look like "no reduction happened" even
        // though the true pre-taper peak (back 14-20) was cut hard already.
        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var acts = new List<ActivitySummary>
        {
            // True pre-taper peak (back 14-20): substantial load.
            new() { Id = 1, Date = Today.AddDays(-14).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 2, Date = Today.AddDays(-16).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 3, Date = Today.AddDays(-18).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            new() { Id = 4, Date = Today.AddDays(-20).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 10, DurationMin = 60, AverageHr = 140 },
            // Deliberate cutback week (back 7-13): already light.
            new() { Id = 5, Date = Today.AddDays(-8).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 6.67, DurationMin = 40, AverageHr = 140 },
            new() { Id = 6, Date = Today.AddDays(-11).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 6.67, DurationMin = 40, AverageHr = 140 },
            // This week: similar to the cutback week, meaningfully below the true peak.
            new() { Id = 7, Date = Today.AddDays(-1).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 6.67, DurationMin = 40, AverageHr = 140 },
            new() { Id = 8, Date = Today.AddDays(-4).ToString("yyyy-MM-dd"), Type = "running", DistanceKm = 6.67, DurationMin = 40, AverageHr = 140 },
        };

        var alerts = AlertEngine.Evaluate(days, null, Today, acts, daysToRace: 14);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("Taper"));
    }

    [Fact]
    public void AlertEngine_FlagsEasyRunsTooFast_EvenWhenChronicDriftHasShiftedTheZoneItself()
    {
        // The easy zone's own low bound is partly derived from the athlete's own recent easy-run
        // paces (PaceCalculator's observed-pace preference) — so a long-standing habit of running
        // "easy" days too fast can drag the zone's low bound fast enough to stop flagging itself.
        // HalfMarathonSeconds=6300 -> threshold ~299 s/km -> formula-anchored easy floor ~349 s/km.
        var race = new RacePrediction { HalfMarathonSeconds = 6300 };
        var acts = new List<ActivitySummary>();
        for (var i = 0; i < 5; i++)
            // 15km in 80 min = 320 s/km: passes the observed-pace plausibility guard (320 >= 299+10)
            // and is FASTER than the formula floor (349), but not far enough below the zone's own
            // (self-shifted) low bound of 305 for the OLD, undamped comparison to have caught it.
            acts.Add(new ActivitySummary { Id = i + 1, Date = Today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
                Type = "running", DistanceKm = 15, DurationMin = 80, AverageHr = 140 });

        var paces = PaceCalculator.FromPredictions(race, acts, Today);
        Assert.Equal(305, paces!.ByKey("easy")!.LowSecPerKm);   // the (drifted) zone low bound
        Assert.Equal(349, paces.FormulaEasyLowSecPerKm);        // the stable, drift-independent floor

        var days = new List<DayMetrics> { new() { Date = Today.ToString("yyyy-MM-dd") } };
        var alerts = AlertEngine.Evaluate(days, null, Today, acts, paces: paces);

        Assert.Contains(alerts, a => a.Title.Contains("Lockere Läufe"));
    }
}
