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
        svc.GetDailySummaryAsync("2026-06-30", Arg.Any<CancellationToken>())
            .Returns(new GarminStats
            {
                RestingHeartRate = 48,
                TotalSteps = 11240,
                AverageStressLevel = 31,
                BodyBatteryHighestValue = 96,
                BodyBatteryLowestValue = 28,
                TotalKilocalories = 2500,
                ModerateIntensityMinutes = 30,
                VigorousIntensityMinutes = 10,
            });
        svc.GetSleepAsync("2026-06-30", Arg.Any<CancellationToken>())
            .Returns(new GarminSleepData { DailySleepDto = new DailySleepDto { SleepTimeSeconds = 27720 } });
        svc.GetActivitiesByDateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GarminActivity
                {
                    ActivityId = 1, ActivityName = "Morning Run",
                    ActivityType = new ActivityType { TypeKey = "running" },
                    StartTimeLocal = new DateTime(2026, 6, 30, 7, 0, 0),
                    Distance = 5000, Duration = 1800, Calories = 300, AverageHr = 150,
                },
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

        var a = Assert.Single(report.Activities);
        Assert.Equal(5.0, a.DistanceKm);
        Assert.Equal(30.0, a.DurationMin);
        Assert.Equal("running", a.Type);
        Assert.Equal(150, a.AverageHr);
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
