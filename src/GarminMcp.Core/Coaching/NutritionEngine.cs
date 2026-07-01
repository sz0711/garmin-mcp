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
///
/// The calorie target is anchored to the day's Garmin-measured burn rather than an assumed
/// body-composition goal (we have no signal on whether the athlete wants to gain/lose/
/// maintain). On the highest-VOLUME days (long run, race — not short/interval "quality"
/// work, whose energy cost varies too much to apply a single flat factor to) it is uplifted
/// by a modest, deliberately conservative recovery buffer: a same-day burn reading reflects
/// logged activity, not necessarily the elevated all-day energy cost of a multi-hour effort
/// (post-exercise oxygen consumption stays elevated for hours), so targeting exactly the
/// measured burn risks under-fuelling recovery. This is a directional heuristic, not a
/// precise physiological calculation — applied identically regardless of whether body
/// weight is known, since the same measurement-understatement issue applies either way.
/// </summary>
public static class NutritionEngine
{
    private const double ProteinPerKg = 1.8;
    private const double HighVolumeRecoveryBuffer = 1.08;

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

        // Long-duration days only — a short/interval "quality" session's energy cost varies too much
        // (20-minute hill repeats vs. a 90-minute tempo run) to apply one flat factor to the whole
        // category, so it's deliberately excluded here (per-kg carb targets already scale it instead).
        var isHighVolume = session is SessionType.Long or SessionType.Race;

        if (weightKg is double kg && kg > 0)
        {
            var carbs = (int)Math.Round(carbPerKg * kg);
            var protein = (int)Math.Round(ProteinPerKg * kg);
            var measuredCal = garminKcal is > 0 ? garminKcal!.Value : (int)Math.Round(carbs * 4 + protein * 4 + 1.0 * kg * 9);
            var cal = isHighVolume ? (int)Math.Round(measuredCal * HighVolumeRecoveryBuffer) : measuredCal;
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
            var cal = isHighVolume ? (int)Math.Round(gk * HighVolumeRecoveryBuffer) : gk;
            return new NutritionPlan
            {
                DayType = label,
                CalorieTarget = cal,
                CarbsG = (int)Math.Round(cal * carbPct / 4),
                ProteinG = (int)Math.Round(cal * proteinPct / 4),
                FatG = (int)Math.Round(cal * fatPct / 9),
                Guidance = tip,
            };
        }

        return null;
    }
}
