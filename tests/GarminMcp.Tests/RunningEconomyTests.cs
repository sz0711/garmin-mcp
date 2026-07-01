using GarminMcp.Core.Coaching;
using Xunit;

namespace GarminMcp.Tests;

public class RunningEconomyTests
{
    [Fact]
    public void GradeAdjustedPace_EqualsRawPace_WhenNoElevationData()
    {
        // 10 km in 60 min = 360 s/km raw. No elevation gain -> grade adjustment must be a no-op.
        var gap = RunningEconomy.GradeAdjustedPaceSecPerKm(10, 60, null);

        Assert.Equal(360, gap, 3);
    }

    [Fact]
    public void GradeAdjustedPace_IsFasterThanRawPace_ForAHillyRun()
    {
        // 10 km in 60 min with 300 m of climbing: 300m * 0.01 = 3 extra "flat-equivalent" km,
        // so grade-adjusted distance = 13 km -> GAP = 60*60/13 ≈ 277 s/km, well under the 360 s/km
        // raw pace -- correctly reflecting that the same time over a hilly 10 km is a faster
        // underlying effort than a flat 10 km in the same time.
        var raw = RunningEconomy.GradeAdjustedPaceSecPerKm(10, 60, 0);
        var gap = RunningEconomy.GradeAdjustedPaceSecPerKm(10, 60, 300);

        Assert.True(gap < raw);
        Assert.Equal(276.9, gap, 1);
    }

    [Fact]
    public void EfficiencyFactor_ReturnsNull_WithoutHeartRate()
    {
        Assert.Null(RunningEconomy.EfficiencyFactor(10, 60, null, null));
    }

    [Fact]
    public void EfficiencyFactor_HigherForFasterPace_AtSameHeartRate()
    {
        // Same HR, faster pace -> more speed per heartbeat -> higher EF.
        var slowEf = RunningEconomy.EfficiencyFactor(10, 70, 140, null);
        var fastEf = RunningEconomy.EfficiencyFactor(10, 50, 140, null);

        Assert.True(fastEf > slowEf);
    }
}
