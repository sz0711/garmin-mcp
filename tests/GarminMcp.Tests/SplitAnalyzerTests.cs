using Garmin.Connect.Models;
using GarminMcp.Core.Coaching;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class SplitAnalyzerTests
{
    private static readonly ActivitySummary LongRun = new() { Id = 1, Date = "2026-06-28", Name = "Longrun" };

    // distanceM in meters, paceSecPerKm in seconds/km -> duration derived so the pace comes out exact.
    private static LapDto Lap(int index, double distanceM, double paceSecPerKm, double elevationGain = 0) => new()
    {
        LapIndex = index,
        Distance = distanceM,
        Duration = distanceM / 1000.0 * paceSecPerKm,
        ElevationGain = elevationGain,
    };

    private static GarminActivitySplits Splits(params LapDto[] laps) => new() { ActivityId = 1, LapDtOs = laps };

    [Fact]
    public void Analyze_DetectsPositiveSplit_WhenSecondHalfSlower()
    {
        // 4 x 5km @ 5:00/km, then 4 x 5km @ 5:30/km (second half ~10% slower -> fade)
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 330));

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Positive, result!.Verdict);
        Assert.True(result.PercentDifference > 3);
        Assert.Equal(40.0, result.DistanceKm);
    }

    [Fact]
    public void Analyze_DetectsNegativeSplit_WhenSecondHalfFaster()
    {
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 330));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 300));

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Negative, result!.Verdict);
        Assert.True(result.PercentDifference < -3);
    }

    [Fact]
    public void Analyze_DetectsEvenSplit_WhenWithinThreshold()
    {
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 303)); // ~1% slower, within the 3% noise band

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Even, result!.Verdict);
    }

    [Fact]
    public void Analyze_UsesGradeAdjustment_SoAnUnevenHillDoesNotFalselyReadAsAFade()
    {
        // Same raw pace both halves, but all the elevation gain is in the second half. Raw pace alone
        // would call this "even"; grade adjustment should recognize the second half was actually
        // faster for the effort (uphill), so it must NOT come out as a false Even/Positive read.
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300, elevationGain: 0));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 300, elevationGain: 50)); // 200m total gain in 2nd half

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Negative, result!.Verdict);
    }

    [Fact]
    public void Analyze_ReturnsNull_WhenTooFewLaps()
    {
        var result = SplitAnalyzer.Analyze(Splits(Lap(0, 5000, 300), Lap(1, 5000, 300)), LongRun);

        Assert.Null(result);
    }

    [Fact]
    public void Analyze_ReturnsNull_WhenLapDtOsIsNull()
    {
        var result = SplitAnalyzer.Analyze(new GarminActivitySplits { ActivityId = 1 }, LongRun);

        Assert.Null(result);
    }

    [Fact]
    public void Analyze_ReturnsNull_WhenPaceIsImplausible()
    {
        // Distance/duration far outside any real running pace (guards against a unit mismatch).
        var laps = new List<LapDto>();
        for (var i = 0; i < 8; i++) laps.Add(Lap(i, 5000, 5)); // 5 seconds for 5km — nonsensical

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.Null(result);
    }

    [Fact]
    public void Analyze_Succeeds_AtTheMinimumFourLapBoundary()
    {
        var laps = new[] { Lap(0, 5000, 300), Lap(1, 5000, 300), Lap(2, 5000, 330), Lap(3, 5000, 330) };

        var result = SplitAnalyzer.Analyze(Splits(laps), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Positive, result!.Verdict);
    }

    [Fact]
    public void Analyze_ReturnsNull_WhenOneDominantLapMakesOneHalfEmpty()
    {
        // A single giant first lap (30 of 36 km) pushes cumulative distance past the halfway point
        // on its own, so every lap -- including the first -- ends up in the "second half" bucket.
        var laps = new[] { Lap(0, 30000, 300), Lap(1, 2000, 300), Lap(2, 2000, 300), Lap(3, 2000, 300) };

        var result = SplitAnalyzer.Analyze(Splits(laps), LongRun);

        Assert.Null(result);
    }

    [Fact]
    public void Analyze_IgnoresLapsWithNaNOrNegativeDistanceOrDuration()
    {
        var laps = new List<LapDto>
        {
            Lap(0, 5000, 300), Lap(1, 5000, 300),
            new() { LapIndex = 2, Distance = double.NaN, Duration = 300 },
            new() { LapIndex = 3, Distance = 5000, Duration = double.NaN },
            new() { LapIndex = 4, Distance = -5000, Duration = 300 },
            new() { LapIndex = 5, Distance = 5000, Duration = -300 },
            Lap(6, 5000, 330), Lap(7, 5000, 330),
        };

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        // Only the 4 well-formed laps (0,1,6,7) should count -- same as the plain 4-lap case above.
        Assert.NotNull(result);
        Assert.Equal(20.0, result!.DistanceKm);
        Assert.Equal(SplitVerdict.Positive, result.Verdict);
    }
}
