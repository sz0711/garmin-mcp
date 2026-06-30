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
        var n = NutritionEngine.Compute(SessionType.Quality, weightKg: null, garminKcal: 2500);
        Assert.NotNull(n);
        Assert.Null(n!.WeightKg);
        Assert.Equal(2500, n.CalorieTarget);
        Assert.True(n.CarbsG > n.ProteinG);
        // carbs ~ 58% of 2500 / 4 ≈ 362 g
        Assert.InRange(n.CarbsG, 340, 380);
    }

    [Fact]
    public void Null_WhenNoWeightAndNoCalories()
    {
        Assert.Null(NutritionEngine.Compute(SessionType.Easy, null, null));
    }
}
