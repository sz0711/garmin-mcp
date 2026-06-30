namespace GarminMcp.Core.Coaching;

/// <summary>Daily fuelling target: calories + carb/protein/fat split, scaled to the day's load.</summary>
public sealed class NutritionPlan
{
    public string DayType { get; set; } = "";
    public double? WeightKg { get; set; }
    public int CalorieTarget { get; set; }
    public int CarbsG { get; set; }
    public int ProteinG { get; set; }
    public int FatG { get; set; }
    public double? CarbsPerKg { get; set; }
    public double? ProteinPerKg { get; set; }
    public string Guidance { get; set; } = "";
}

/// <summary>
/// Computes a daily macro target for an endurance/marathon athlete. Carbohydrate needs
/// scale with the day's training (rest ~3.5 g/kg → long/quality 7–8 g/kg); protein is
/// held at ~1.8 g/kg; fat fills the rest. Prefers per-kg targets when body weight is
/// known, otherwise falls back to a calorie-percentage split of the day's energy burn.
/// Sources: ACSM/IOC sports-nutrition carbohydrate periodisation guidelines.
/// </summary>
public static class NutritionEngine
{
    private const double ProteinPerKg = 1.8;

    public static NutritionPlan? Compute(SessionType session, double? weightKg, int? garminKcal)
    {
        var (carbPerKg, label, carbPct, proteinPct, fatPct, tip) = session switch
        {
            SessionType.Rest => (3.5, "Ruhetag", 0.45, 0.25, 0.30,
                "Weniger Kohlenhydrate, Eiweiß hoch halten für die Regeneration; viel Gemüse, nicht überessen."),
            SessionType.Easy => (5.0, "Lockerer Tag", 0.50, 0.20, 0.30,
                "Moderate Kohlenhydrate, gute Eiweißzufuhr, ausreichend trinken."),
            SessionType.Long => (8.0, "Longrun", 0.60, 0.18, 0.22,
                "Kohlenhydrate hoch: vorher aufladen, währenddessen Carbs/Gels, danach Carbs + Eiweiß (3:1) zur Erholung."),
            SessionType.Quality => (7.0, "Harte Einheit", 0.58, 0.20, 0.22,
                "Kohlenhydrate vor und nach der Einheit, Eiweiß zur Regeneration, gut hydrieren."),
            SessionType.Strength => (5.0, "Krafttag", 0.45, 0.27, 0.28,
                "Eiweiß betont (Muskelerhalt), moderate Carbs um die Einheit."),
            SessionType.Race => (9.0, "Wettkampf", 0.62, 0.18, 0.20,
                "Maximal Kohlenhydrate, leicht verdaulich; bewährte Renn-Verpflegung."),
            _ => (6.0, "Moderater Tag", 0.55, 0.20, 0.25,
                "Ausgewogene Kohlenhydrate, solide Eiweißzufuhr."),
        };

        if (weightKg is double kg && kg > 0)
        {
            var carbs = (int)Math.Round(carbPerKg * kg);
            var protein = (int)Math.Round(ProteinPerKg * kg);
            var cal = garminKcal is > 0 ? garminKcal!.Value : (int)Math.Round(carbs * 4 + protein * 4 + 1.0 * kg * 9);
            var fat = (int)Math.Round(Math.Max(0.8 * kg, (cal - carbs * 4 - protein * 4) / 9.0));
            cal = Math.Max(cal, carbs * 4 + protein * 4 + fat * 9);
            return new NutritionPlan
            {
                DayType = label,
                WeightKg = kg,
                CalorieTarget = cal,
                CarbsG = carbs,
                ProteinG = protein,
                FatG = fat,
                CarbsPerKg = carbPerKg,
                ProteinPerKg = ProteinPerKg,
                Guidance = tip,
            };
        }

        if (garminKcal is int gk && gk > 0)
        {
            return new NutritionPlan
            {
                DayType = label,
                CalorieTarget = gk,
                CarbsG = (int)Math.Round(gk * carbPct / 4),
                ProteinG = (int)Math.Round(gk * proteinPct / 4),
                FatG = (int)Math.Round(gk * fatPct / 9),
                Guidance = tip,
            };
        }

        return null;
    }
}
