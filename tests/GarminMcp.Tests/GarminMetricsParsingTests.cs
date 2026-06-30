using System.Text.Json;
using GarminMcp.Core.Metrics;
using Xunit;

namespace GarminMcp.Tests;

/// <summary>
/// Exercises the raw Garmin "metrics" JSON parsing (the riskiest, previously HTTP-coupled code).
/// JSON shapes mirror the real endpoint payloads the parsers target.
/// </summary>
public class GarminMetricsParsingTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseReadiness_PicksAfterWakeupResetEntry()
    {
        var json = """
        [
          { "inputContext": "HISTORICAL", "score": 41, "level": "LOW" },
          { "inputContext": "AFTER_WAKEUP_RESET", "score": 72, "level": "HIGH",
            "feedbackShort": "Bereit", "sleepScore": 80, "hrvFactorPercent": 60,
            "recoveryTime": 1200, "acuteLoad": 350 }
        ]
        """;
        var r = GarminMetricsClient.ParseReadiness(Root(json));

        Assert.NotNull(r);
        Assert.Equal(72, r!.Score);
        Assert.Equal("HIGH", r.Level);
        Assert.Equal("Bereit", r.Feedback);
        Assert.Equal(80, r.SleepScore);
        Assert.Equal(60, r.HrvFactorPercent);
        Assert.Equal(20, r.RecoveryTimeHours); // 1200 min / 60
        Assert.Equal(350, r.AcuteLoad);
    }

    [Fact]
    public void ParseReadiness_FallsBackToFirstWhenNoWakeupContext()
    {
        var r = GarminMetricsClient.ParseReadiness(Root("""[ { "score": 55, "level": "MODERATE" } ]"""));
        Assert.Equal(55, r!.Score);
        Assert.Equal("MODERATE", r.Level);
    }

    [Fact]
    public void ParseReadiness_EmptyArrayIsNull()
        => Assert.Null(GarminMetricsClient.ParseReadiness(Root("[]")));

    [Fact]
    public void ParseStatus_ExtractsPhraseVo2maxAndAcwr()
    {
        var json = """
        {
          "mostRecentTrainingStatus": {
            "latestTrainingStatusData": {
              "3939376432": {
                "trainingStatusFeedbackPhrase": "PRODUCTIVE_1",
                "weeklyTrainingLoad": 420,
                "acuteTrainingLoadDTO": {
                  "dailyTrainingLoadAcute": 350.0,
                  "dailyTrainingLoadChronic": 300.0,
                  "dailyAcuteChronicWorkloadRatio": 1.17,
                  "acwrStatus": "OPTIMAL"
                }
              }
            }
          },
          "mostRecentVO2Max": { "generic": { "vo2MaxValue": 54.0 } },
          "mostRecentTrainingLoadBalance": {
            "metricsTrainingLoadBalanceDTOMap": {
              "3939376432": {
                "monthlyLoadAerobicLow": 100,
                "monthlyLoadAerobicHigh": 200,
                "monthlyLoadAnaerobic": 50,
                "trainingBalanceFeedbackPhrase": "BALANCED"
              }
            }
          }
        }
        """;
        var s = GarminMetricsClient.ParseStatus(Root(json));

        Assert.NotNull(s);
        Assert.Equal("PRODUCTIVE_1", s!.StatusPhrase);
        Assert.Equal(420, s.WeeklyLoad);
        Assert.Equal(350.0, s.AcuteLoad);
        Assert.Equal(300.0, s.ChronicLoad);
        Assert.Equal(1.17, s.Acwr);
        Assert.Equal("OPTIMAL", s.AcwrStatus);
        Assert.Equal(54.0, s.Vo2Max);
        Assert.Equal(100, s.LoadAerobicLow);
        Assert.Equal(200, s.LoadAerobicHigh);
        Assert.Equal(50, s.LoadAnaerobic);
        Assert.Equal("BALANCED", s.LoadBalanceFeedback);
    }

    [Fact]
    public void ParseStatus_UsesPreciseVo2maxFallback()
    {
        var s = GarminMetricsClient.ParseStatus(Root("""{ "mostRecentVO2Max": { "generic": { "vo2MaxPreciseValue": 53.7 } } }"""));
        Assert.Equal(53.7, s!.Vo2Max);
    }

    [Fact]
    public void ParseRace_ReadsTimesFromArray()
    {
        var r = GarminMetricsClient.ParseRace(Root("""
        [ { "time5K": 1290, "time10K": 2700, "timeHalfMarathon": 6000, "timeMarathon": 13500 } ]
        """));

        Assert.NotNull(r);
        Assert.Equal(1290, r!.FiveKSeconds);
        Assert.Equal(2700, r.TenKSeconds);
        Assert.Equal(6000, r.HalfMarathonSeconds);
        Assert.Equal(13500, r.MarathonSeconds);
    }

    [Fact]
    public void ParseRace_ReadsSingleObject()
    {
        var r = GarminMetricsClient.ParseRace(Root("""{ "timeMarathon": 12600 }"""));
        Assert.Equal(12600, r!.MarathonSeconds);
        Assert.Null(r.FiveKSeconds);
    }

    [Fact]
    public void ParseRace_EmptyArrayIsNull()
        => Assert.Null(GarminMetricsClient.ParseRace(Root("[]")));
}
