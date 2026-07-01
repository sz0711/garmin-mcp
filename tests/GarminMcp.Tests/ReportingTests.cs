using System.Text.Json;
using GarminMcp.Core;
using GarminMcp.Core.Reporting;
using Garmin.Connect.Models;
using NSubstitute;
using Xunit;

namespace GarminMcp.Tests;

public class ReportingTests
{
    [Fact]
    public void Merge_KeepsHistory_FreshWins_SortedDesc()
    {
        var existing = new GarminReport
        {
            Days =
            {
                new DayMetrics { Date = "2026-06-28", Steps = 100 },
                new DayMetrics { Date = "2026-06-29", Steps = 200 },
            },
            Activities = { new ActivitySummary { Id = 1, Date = "2026-06-28" } },
        };
        var fresh = new GarminReport
        {
            GeneratedAtUtc = DateTimeOffset.UnixEpoch,
            Days =
            {
                new DayMetrics { Date = "2026-06-29", Steps = 999 },
                new DayMetrics { Date = "2026-06-30", Steps = 300 },
            },
            Activities = { new ActivitySummary { Id = 2, Date = "2026-06-30" } },
        };

        var merged = GarminReport.Merge(existing, fresh);

        Assert.Equal(3, merged.Days.Count);
        Assert.Equal("2026-06-30", merged.Days[0].Date); // sorted descending
        Assert.Equal(999, merged.Days.Single(d => d.Date == "2026-06-29").Steps); // fresh overrides
        Assert.Equal(2, merged.Activities.Count);
    }

    [Fact]
    public async Task Builder_MapsModelsToMetrics()
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        svc.GetHrvAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminReportHrvStatus
            {
                HrvSummaries = new[] { new GarminHrvSummary { CalendarDate = today, LastNightAvg = 72, Status = "BALANCED" } },
            });
        // GarminStats types SpO2 as `object` (System.Text.Json deserializes it into a boxed
        // JsonElement on a real HTTP response) — round-trip real JSON so the test exercises the
        // exact same unboxing path production code will see, not a hand-built CLR value.
        var statsJson = """
        {
          "restingHeartRate": 48, "totalSteps": 11240, "averageStressLevel": 31,
          "bodyBatteryHighestValue": 96, "bodyBatteryLowestValue": 28, "totalKilocalories": 2500,
          "moderateIntensityMinutes": 30, "vigorousIntensityMinutes": 10,
          "averageSpo2": 96, "lowestSpo2": 91
        }
        """;
        svc.GetDailySummaryAsync("2026-06-30", Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<GarminStats>(statsJson)!);
        svc.GetSleepAsync("2026-06-30", Arg.Any<CancellationToken>())
            .Returns(new GarminSleepData { DailySleepDto = new DailySleepDto { SleepTimeSeconds = 27720, AverageRespirationValue = 14.2 } });
        svc.GetActivitiesByDateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GarminActivity
                {
                    ActivityId = 1, ActivityName = "Morning Run",
                    ActivityType = new ActivityType { TypeKey = "running" },
                    StartTimeLocal = new DateTime(2026, 6, 30, 7, 0, 0),
                    Distance = 5000, Duration = 1800, Calories = 300, AverageHr = 150,
                    AverageRunningCadenceInStepsPerMinute = 176.4, AvgGroundContactTime = 248, AvgVerticalOscillation = 8.9,
                    AvgStrideLength = 118, AerobicTrainingEffect = 3.2, AnaerobicTrainingEffect = 0.8,
                    TrainingEffectLabel = JsonSerializer.Deserialize<JsonElement>("\"TEMPO_TRAINING_EFFECT_LABEL\""),
                },
                new GarminActivity
                {
                    ActivityId = 2, ActivityName = "Dog Walk",
                    ActivityType = new ActivityType { TypeKey = "walking" },
                    StartTimeLocal = new DateTime(2026, 6, 30, 18, 0, 0),
                    Distance = 2000, Duration = 1200,
                    AverageRunningCadenceInStepsPerMinute = 999, // implausible for a walk / sanity-clamp target
                },
            });
        svc.GetBodyCompositionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminBodyComposition
            {
                DateWeightList = new[] { new GarminDateWeight
                {
                    CalendarDate = new DateTime(2026, 6, 30), BodyFat = 14.8,
                    MuscleMass = 32500, // grams, as Garmin's raw API typically reports it
                    VisceralFat = 7,
                } },
            });

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);

        var d = Assert.Single(report.Days);
        Assert.Equal("2026-06-30", d.Date);
        Assert.Equal(48, d.RestingHeartRate);
        Assert.Equal(72, d.HrvLastNight);
        Assert.Equal("BALANCED", d.HrvStatus);
        Assert.Equal(7.7, d.SleepHours);
        Assert.Equal(11240, d.Steps);
        Assert.Equal(96, d.BodyBatteryHigh);
        Assert.Equal(50, d.IntensityMinutes); // 30 moderate + 2×10 vigorous (Garmin/WHO doubling)
        Assert.Equal(96, d.SpO2Avg);
        Assert.Equal(91, d.SpO2Low);
        Assert.Equal(14.2, d.SleepRespirationRate);
        Assert.Equal(14.8, d.BodyFatPercent);
        Assert.Equal(32.5, d.MuscleMassKg); // 32500 g -> kg
        Assert.Equal(7, d.VisceralFatRating);

        Assert.Equal(2, report.Activities.Count);
        var run = report.Activities.Single(a => a.Id == 1);
        Assert.Equal(5.0, run.DistanceKm);
        Assert.Equal(30.0, run.DurationMin);
        Assert.Equal("running", run.Type);
        Assert.Equal(150, run.AverageHr);
        Assert.Equal(176.4, run.CadenceSpm);
        Assert.Equal(248, run.GroundContactTimeMs);
        Assert.Equal(8.9, run.VerticalOscillationCm);
        Assert.Equal(118, run.StrideLengthCm);
        Assert.Equal(3.2, run.AerobicEffect);
        Assert.Equal(0.8, run.AnaerobicEffect);
        // Stored RAW (like ActivitySummary.Type) — MarkdownRenderer.EffectLabelDe translates it for display.
        Assert.Equal("TEMPO_TRAINING_EFFECT_LABEL", run.EffectLabel);

        var walk = report.Activities.Single(a => a.Id == 2);
        Assert.Null(walk.CadenceSpm); // running dynamics never populated for non-run activity types
    }

    [Theory]
    [InlineData(30)]    // some accounts report muscle mass already in kg
    [InlineData(30_000)] // others report it in grams (the ambiguity WeightKg already guards against)
    public async Task Builder_MuscleMass_HandlesBothGramsAndKgUnits(long rawMuscleMass)
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        StubMinimalBodyComposition(svc, today, muscleMass: rawMuscleMass, visceralFat: 5);

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);

        Assert.Equal(30.0, Assert.Single(report.Days).MuscleMassKg);
    }

    [Theory]
    [InlineData(5)]     // implausibly low for a human (even in kg)
    [InlineData(500_000)] // implausibly high even as grams
    public async Task Builder_MuscleMass_RejectsImplausibleValues(long rawMuscleMass)
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        StubMinimalBodyComposition(svc, today, muscleMass: rawMuscleMass, visceralFat: 5);

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);

        Assert.Null(Assert.Single(report.Days).MuscleMassKg);
    }

    [Fact]
    public async Task Builder_VisceralFat_RejectsImplausibleValue()
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        StubMinimalBodyComposition(svc, today, muscleMass: 30_000, visceralFat: 75); // above the ~1-59 scale

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);

        Assert.Null(Assert.Single(report.Days).VisceralFatRating);
    }

    private static void StubMinimalBodyComposition(IGarminService svc, DateOnly today, long muscleMass, double visceralFat)
    {
        svc.GetBodyCompositionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminBodyComposition
            {
                DateWeightList = new[] { new GarminDateWeight
                {
                    CalendarDate = today.ToDateTime(TimeOnly.MinValue),
                    MuscleMass = muscleMass, VisceralFat = visceralFat,
                } },
            });
    }

    [Fact]
    public async Task Builder_FallsBackToPerDayFetch_WhenBulkActivitiesRangeFails()
    {
        // Simulates the real risk this round's review surfaced: GarminActivity types several running-
        // dynamics/training-effect fields as non-nullable double, so ONE activity with a JSON null in
        // one of them fails System.Text.Json deserialization for the WHOLE range (arrays deserialize
        // atomically). The builder must fall back to day-by-day fetches so only the bad day is lost.
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();

        svc.GetActivitiesByDateAsync("2026-06-28", "2026-06-30", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GarminActivity[]>(new JsonException("simulated: null in a non-nullable field")));

        svc.GetActivitiesByDateAsync("2026-06-28", "2026-06-28", null, Arg.Any<CancellationToken>())
            .Returns(new[] { new GarminActivity { ActivityId = 1, StartTimeLocal = new DateTime(2026, 6, 28), Distance = 5000, Duration = 1800 } });
        svc.GetActivitiesByDateAsync("2026-06-29", "2026-06-29", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GarminActivity[]>(new JsonException("this specific day is still unparseable")));
        svc.GetActivitiesByDateAsync("2026-06-30", "2026-06-30", null, Arg.Any<CancellationToken>())
            .Returns(new[] { new GarminActivity { ActivityId = 2, StartTimeLocal = new DateTime(2026, 6, 30), Distance = 8000, Duration = 2400 } });

        var report = await ReportBuilder.BuildAsync(svc, 3, today, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, report.Activities.Count); // the 06-29 activity is lost, but 06-28 and 06-30 survive
        Assert.Contains(report.Activities, a => a.Id == 1);
        Assert.Contains(report.Activities, a => a.Id == 2);
        Assert.DoesNotContain(report.Activities, a => a.Date == "2026-06-29");
    }

    [Fact]
    public async Task Builder_ClampsImplausibleRunningDynamics()
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        svc.GetActivitiesByDateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GarminActivity
                {
                    ActivityId = 1, ActivityType = new ActivityType { TypeKey = "running" },
                    StartTimeLocal = new DateTime(2026, 6, 30),
                    AverageRunningCadenceInStepsPerMinute = 999,   // way outside 120-220 spm
                    AvgGroundContactTime = 5000,                   // way outside 150-400 ms
                    AvgVerticalOscillation = -1,                   // outside 3-20 cm
                    AerobicTrainingEffect = 9.9,                   // outside Garmin's 0-5 scale
                },
            });

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);

        var a = Assert.Single(report.Activities);
        Assert.Null(a.CadenceSpm);
        Assert.Null(a.GroundContactTimeMs);
        Assert.Null(a.VerticalOscillationCm);
        Assert.Null(a.AerobicEffect);
    }

    [Fact]
    public async Task Builder_TreatsZeroAsNoData()
    {
        var today = new DateOnly(2026, 6, 30);
        var svc = Substitute.For<IGarminService>();
        svc.GetHrvAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminReportHrvStatus { HrvSummaries = Array.Empty<GarminHrvSummary>() });
        svc.GetDailySummaryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminStats { RestingHeartRate = 0, TotalSteps = 0 });
        svc.GetSleepAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GarminSleepData { DailySleepDto = new DailySleepDto { SleepTimeSeconds = 0 } });
        svc.GetActivitiesByDateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GarminActivity>());

        var report = await ReportBuilder.BuildAsync(svc, 1, today, DateTimeOffset.UnixEpoch);
        var d = Assert.Single(report.Days);
        Assert.Null(d.RestingHeartRate);
        Assert.Null(d.Steps);
        Assert.Null(d.SleepHours);
        Assert.False(d.HasAnyData);
    }

    [Fact]
    public void Markdown_RendersSectionsAndValues()
    {
        var md = MarkdownRenderer.Render(SampleReport());
        Assert.Contains("# 🏃 Garmin Dashboard", md);
        Assert.Contains("Ruhepuls: 48 bpm", md);
        Assert.Contains("BALANCED", md);
        Assert.Contains("| 2026-06-30 |", md);
        Assert.Contains("Morning Run", md);
    }

    [Fact]
    public void Markdown_HandlesEmptyAndNulls()
    {
        var empty = new GarminReport { GeneratedAtUtc = DateTimeOffset.UnixEpoch, Days = { new DayMetrics { Date = "2026-06-30" } } };
        var md = MarkdownRenderer.Render(empty);
        Assert.Contains("–", md);
        Assert.Contains("Garmin Dashboard", md);
    }

    private static GarminReport SampleReport() => new()
    {
        GeneratedAtUtc = DateTimeOffset.UnixEpoch,
        Days =
        {
            new DayMetrics
            {
                Date = "2026-06-30", RestingHeartRate = 48, HrvLastNight = 72, HrvStatus = "BALANCED",
                SleepHours = 7.7, Steps = 11240, StressAvg = 31, BodyBatteryHigh = 96, BodyBatteryLow = 28,
                Calories = 2500, IntensityMinutes = 45,
            },
            new DayMetrics { Date = "2026-06-29", RestingHeartRate = 50, HrvLastNight = 68, SleepHours = 7.1, Steps = 9000 },
        },
        Activities =
        {
            new ActivitySummary { Id = 1, Date = "2026-06-30", Name = "Morning Run", Type = "running", DistanceKm = 5.0, DurationMin = 30, Calories = 300, AverageHr = 150 },
        },
    };
}
