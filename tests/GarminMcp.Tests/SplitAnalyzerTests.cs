using Garmin.Connect.Models;
using GarminMcp.Core.Coaching;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class SplitAnalyzerTests
{
    private static readonly ActivitySummary LongRun = new() { Id = 1, Date = "2026-06-28", Name = "Longrun" };

    // distanceM in meters, paceSecPerKm in seconds/km -> duration derived so the pace comes out exact.
    private static LapDto Lap(int index, double distanceM, double paceSecPerKm, double elevationGain = 0, double avgHr = 0) => new()
    {
        LapIndex = index,
        Distance = distanceM,
        Duration = distanceM / 1000.0 * paceSecPerKm,
        ElevationGain = elevationGain,
        AverageHr = avgHr,
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

    [Fact]
    public void Analyze_DetectsAerobicDecoupling_WhenHrRisesAtSamePace()
    {
        // Same grade-adjusted pace both halves, but HR climbs from 140 to 155 -- efficiency (speed
        // per heartbeat) worsened, which pace comparison alone would miss entirely (Even verdict).
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300, avgHr: 140));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 300, avgHr: 155));

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Equal(SplitVerdict.Even, result!.Verdict); // pace alone looks fine
        Assert.NotNull(result.AerobicDecouplingPercent);
        Assert.True(result.AerobicDecouplingPercent > 5); // but decoupling flags the real story
    }

    [Fact]
    public void Analyze_LeavesDecouplingNull_WhenMostLapsInAHalfLackHeartRate()
    {
        // 10 laps of 5km each -> 5 laps per half. First half: only 1 of 5 laps (20% of that half's
        // duration) has a real HR reading, the rest default to 0 (Garmin's "no sensor data" marker).
        // A naive duration-weighted average over ALL laps (including the zero ones) would still land
        // above a bare floor and look like a plausible-but-wrong low HR -- must come out null instead.
        var laps = new List<LapDto> { Lap(0, 5000, 300, avgHr: 150) };
        for (var i = 1; i < 5; i++) laps.Add(Lap(i, 5000, 300)); // avgHr defaults to 0
        for (var i = 5; i < 10; i++) laps.Add(Lap(i, 5000, 300, avgHr: 150)); // second half: full HR coverage

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Null(result!.AerobicDecouplingPercent);
    }

    [Fact]
    public void Analyze_ComputesDecoupling_WhenCoverageIsAtTheEightyPercentBoundary()
    {
        // Same shape as above, but 4 of 5 laps per half have real HR (80% coverage, the pass/fail
        // boundary) -- should compute normally, ignoring only the single sensor-dropout lap.
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300, avgHr: 140));
        laps.Add(Lap(4, 5000, 300)); // dropout lap, avgHr defaults to 0
        for (var i = 5; i < 9; i++) laps.Add(Lap(i, 5000, 300, avgHr: 155));
        laps.Add(Lap(9, 5000, 300)); // dropout lap, avgHr defaults to 0

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.NotNull(result!.AerobicDecouplingPercent);
        Assert.True(result.AerobicDecouplingPercent > 5);
    }

    [Fact]
    public void Analyze_LeavesDecouplingNull_WhenNoHeartRateData()
    {
        // AverageHr defaults to 0 (Garmin's "no sensor data" convention for this DTO, not null).
        var laps = new List<LapDto>();
        for (var i = 0; i < 4; i++) laps.Add(Lap(i, 5000, 300));
        for (var i = 4; i < 8; i++) laps.Add(Lap(i, 5000, 330));

        var result = SplitAnalyzer.Analyze(Splits(laps.ToArray()), LongRun);

        Assert.NotNull(result);
        Assert.Null(result!.AerobicDecouplingPercent);
    }
}
