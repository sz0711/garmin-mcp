using GarminMcp.Core.Coaching;
using Xunit;

namespace GarminMcp.Tests;

public class NutritionEngineTests
{
    [Fact]
    public void WeightBased_ComputesPerKgMacros()
    {
        var n = NutritionEngine.Compute(SessionType.Long, weightKg: 70, garminKcal: 3000);
        Assert.NotNull(n);
        Assert.Equal("Longrun", n!.DayType);
        Assert.Equal(70, n.WeightKg);
        Assert.Equal(560, n.CarbsG);      // 8 g/kg * 70
        Assert.Equal(126, n.ProteinG);    // 1.8 g/kg * 70
        Assert.True(n.FatG > 0);
        // Calories stay consistent with the macro sum (never below the day's burn estimate).
        Assert.True(n.CalorieTarget >= 3000);
        Assert.Equal(n.CarbsG * 4 + n.ProteinG * 4 + n.FatG * 9, n.CalorieTarget);
    }

    [Fact]
    public void Carbs_ScaleWithDayLoad()
    {
        var rest = NutritionEngine.Compute(SessionType.Rest, 70, null)!;
        var moderate = NutritionEngine.Compute(SessionType.Other, 70, null)!;
        var hard = NutritionEngine.Compute(SessionType.Long, 70, null)!;
        Assert.True(rest.CarbsG < moderate.CarbsG);
        Assert.True(moderate.CarbsG < hard.CarbsG);
    }

    [Fact]
    public void CalorieBased_WhenNoWeight_UsesPercentSplit()
    {
        // Long is a high-VOLUME day: target is uplifted ~8% above the measured burn (a same-day
        // Garmin reading can understate the true energy cost of a multi-hour effort), so it is NOT
        // a bare pass-through of garminKcal.
        var n = NutritionEngine.Compute(SessionType.Long, weightKg: null, garminKcal: 2500);
        Assert.NotNull(n);
        Assert.Null(n!.WeightKg);
        Assert.Equal(2700, n.CalorieTarget); // 2500 * 1.08, rounded
        Assert.True(n.CarbsG > n.ProteinG);
        // carbs ~ 60% of 2700 / 4 ≈ 405 g
        Assert.InRange(n.CarbsG, 380, 430);
        // Macros stay internally consistent with the (uplifted) calorie target.
        var macroSum = n.CarbsG * 4 + n.ProteinG * 4 + n.FatG * 9;
        Assert.InRange(macroSum, n.CalorieTarget - 10, n.CalorieTarget + 10);
    }

    [Fact]
    public void CalorieBased_WhenNoWeight_LowDemandDayMatchesMeasuredBurnExactly()
    {
        // Rest/Easy/Strength AND Quality are NOT uplifted: Quality spans everything from short hill
        // repeats to a 90-minute tempo run, too heterogeneous for one flat factor (per-kg carb
        // targets already scale it in the weight-known path instead).
        var rest = NutritionEngine.Compute(SessionType.Rest, weightKg: null, garminKcal: 2200)!;
        var easy = NutritionEngine.Compute(SessionType.Easy, weightKg: null, garminKcal: 2200)!;
        var quality = NutritionEngine.Compute(SessionType.Quality, weightKg: null, garminKcal: 2200)!;
        Assert.Equal(2200, rest.CalorieTarget);
        Assert.Equal(2200, easy.CalorieTarget);
        Assert.Equal(2200, quality.CalorieTarget);
    }

    [Theory]
    [InlineData(SessionType.Long)]
    [InlineData(SessionType.Race)]
    public void CalorieBased_WhenNoWeight_HighVolumeDaysAreUpliftedAboveMeasuredBurn(SessionType session)
    {
        var n = NutritionEngine.Compute(session, weightKg: null, garminKcal: 2000)!;
        Assert.True(n.CalorieTarget > 2000);
    }

    [Fact]
    public void CalorieBased_WithWeight_AlsoGetsHighVolumeUplift()
    {
        // The uplift now applies consistently whether or not body weight is known — a same-day
        // Garmin reading can understate true energy cost either way. Use a garminKcal well above
        // the per-kg macro floor so the uplift (not the floor) is what's actually being tested.
        var withWeight = NutritionEngine.Compute(SessionType.Long, weightKg: 60, garminKcal: 5000)!;
        Assert.True(withWeight.CalorieTarget >= (int)Math.Round(5000 * 1.08));
    }

    [Fact]
    public void Null_WhenNoWeightAndNoCalories()
    {
        Assert.Null(NutritionEngine.Compute(SessionType.Easy, null, null));
    }
}
