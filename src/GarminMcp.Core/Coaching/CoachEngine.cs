using GarminMcp.Core.Metrics;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

public enum Readiness
{
    Green,
    Amber,
    Red,
}

/// <summary>The structured daily coaching verdict (input to the renderers and the LLM coach).</summary>
public sealed class DailyCoaching
{
    public string Date { get; set; } = "";
    public Readiness Readiness { get; set; }
    public int? ReadinessScore { get; set; }
    public string? ReadinessLevel { get; set; }
    public SessionType Recommended { get; set; }
    public string Headline { get; set; } = "";
    public List<string> Rationale { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public List<PlannedWorkout> PlanToday { get; set; } = new();
    public List<PlannedWorkout> Upcoming { get; set; } = new();
    public string? PlanNote { get; set; }
    public PlannedWorkout? NextLongRun { get; set; }
    public PlannedWorkout? NextQuality { get; set; }
    public string? TrainingStatus { get; set; }
    public double? Acwr { get; set; }
    public string? AcwrStatus { get; set; }
    public double? Vo2Max { get; set; }
    public RacePrediction? Race { get; set; }
    public string? RaceDate { get; set; }
    public int? DaysToRace { get; set; }
    public string? Goal { get; set; }
    public string? TaperNote { get; set; }
    public NutritionPlan? Nutrition { get; set; }

    // Enrichments computed by the report builder (where activities are available).
    public double? Ctl { get; set; }   // Fitness
    public double? Atl { get; set; }   // Fatigue
    public double? Tsb { get; set; }   // Form
    public int? PlannedThisWeek { get; set; }
    public int? DoneThisWeek { get; set; }
    public double? PlannedKmThisWeek { get; set; }
    public double? DoneKmThisWeek { get; set; }
    public int? PlannedLastWeek { get; set; }
    public int? DoneLastWeek { get; set; }
    public double? PlannedKmLastWeek { get; set; }
    public double? DoneKmLastWeek { get; set; }
    public double? SleepConsistencyMin { get; set; }

    // What was already trained today (so the coach doesn't prescribe a second session).
    public List<ActivitySummary> CompletedToday { get; set; } = new();
    public bool TrainedToday => CompletedToday.Count > 0;

    // Goal projection (parsed goal vs Garmin marathon prediction).
    public int? GoalSeconds { get; set; }
    public int? GoalGapSeconds { get; set; }   // predicted - goal (negative = ahead of goal)
    public bool? OnTrackForGoal { get; set; }

    // Training pace bands derived from race predictions.
    public PaceZones? Paces { get; set; }
    public string? TodayTargetPace { get; set; }

    // Endurance reality check for the marathon-prediction goal verdict (see Evaluate for the
    // sports-science rationale): a VO2max/tempo-based prediction can look "on track" long before
    // the long run needed for 42.2 km has actually been built.
    public double? LongestRunKm { get; set; }
    public string? EnduranceCaveat { get; set; }
}

/// <summary>
/// Deterministic, evidence-based daily training recommendation. Anchors on Garmin
/// Training Readiness when present, applies recovery-signal guardrails (HRV trend vs
/// baseline, RHR, sleep, Body Battery, ACWR), and reconciles with the planned workout.
/// Asymmetric: a red flag can veto a hard day; good signals only grant permission.
/// </summary>
public static class CoachEngine
{
    public static DailyCoaching Evaluate(
        DateOnly today,
        IReadOnlyList<DayMetrics> days,
        TrainingReadiness? readiness,
        TrainingStatusInfo? status,
        TrainingPlanView plan,
        RacePrediction? race,
        string? goal = null,
        double? weightKg = null,
        IReadOnlyList<ActivitySummary>? activities = null)
    {
        var todayKey = today.ToString("yyyy-MM-dd");
        var todayM = days.FirstOrDefault(d => d.Date == todayKey);
        var prior = days
            .Where(d => string.CompareOrdinal(d.Date, todayKey) < 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .Take(7)
            .ToList();

        var flags = new List<string>();
        var rationale = new List<string>();
        int red = 0, amber = 0;

        // --- HRV (trend vs baseline) ---
        double? hrvBaseline = Avg(prior, d => d.HrvLastNight);
        double? hrvPct = null;
        if (todayM?.HrvLastNight is int hrv && hrvBaseline is double hb && hb > 0)
        {
            hrvPct = (hrv - hb) / hb * 100.0;
            if (hrvPct <= -7) { red++; flags.Add($"HRV {hrv} ms liegt {Math.Abs(hrvPct.Value):0} % unter deiner ~{hb:0}-ms-Baseline"); }
            else if (hrvPct <= -4) { amber++; flags.Add($"HRV {hrv} ms leicht unter deiner ~{hb:0}-ms-Baseline"); }
        }

        // --- Resting HR (vs 7-day average) ---
        double? rhrBaseline = Avg(prior, d => d.RestingHeartRate);
        double? rhrDelta = null;
        if (todayM?.RestingHeartRate is int rhr && rhrBaseline is double rb)
        {
            rhrDelta = rhr - rb;
            if (rhrDelta >= 7) { red++; flags.Add($"Ruhepuls {rhr} bpm liegt {rhrDelta:0} bpm über deinem ~{rb:0}-bpm-Schnitt"); }
            else if (rhrDelta >= 4) { amber++; flags.Add($"Ruhepuls {rhr} bpm leicht erhöht (~+{rhrDelta:0})"); }
        }

        // --- Sleep ---
        if (todayM?.SleepHours is double sleep)
        {
            if (sleep < 6) { red++; flags.Add($"Nur {sleep:0.0} h Schlaf letzte Nacht"); }
            else if (sleep < 7) { amber++; flags.Add($"{sleep:0.0} h Schlaf (etwas wenig)"); }
        }

        // --- Body Battery (peak as a proxy for recharge) ---
        if (todayM?.BodyBatteryHigh is int bb)
        {
            if (bb < 25) { red++; flags.Add($"Body Battery kaum aufgeladen (Peak {bb})"); }
            else if (bb < 50) { amber++; flags.Add($"Body Battery nur teilweise aufgeladen (Peak {bb})"); }
        }

        // --- Acute:Chronic Workload Ratio (caps the day) ---
        if (status?.Acwr is double acwr && acwr > 0)
        {
            if (acwr > 1.5) { red++; flags.Add($"Trainingslast-Spitze (ACWR {acwr:0.0} > 1,5) — Verletzungs-Risikofenster"); }
            else if (acwr > 1.3 || acwr < 0.8) { amber++; flags.Add($"Trainingslast-Verhältnis außerhalb des Ziels (ACWR {acwr:0.0})"); }
        }

        // --- Illness pattern ---
        bool illness = rhrDelta >= 7 && hrvPct <= -7 && (todayM?.SleepHours is double s2 && s2 < 6);
        if (illness) flags.Add("Mögliches Krankheits-/Überlastungsmuster (erhöhter Ruhepuls + niedrige HRV + schlechter Schlaf)");

        // --- Roll-up rating ---
        var rating = (red >= 1 || amber >= 3 || illness) ? Readiness.Red
                   : amber >= 1 ? Readiness.Amber
                   : Readiness.Green;

        // --- Anchor on Garmin Training Readiness (take the more conservative) ---
        if (readiness?.Score is int score)
        {
            var band = score >= 60 ? Readiness.Green : score >= 40 ? Readiness.Amber : Readiness.Red;
            rating = (Readiness)Math.Max((int)rating, (int)band);
            rationale.Add($"Garmin Training Readiness {score}{(readiness.Level is null ? "" : $" ({readiness.Level})")}.");
            // (Readiness is a Garmin product term — kept verbatim.)
        }

        // --- Reconcile with the plan ---
        var plannedToday = plan.Today.FirstOrDefault(p => p.Type != SessionType.Rest) ?? plan.Today.FirstOrDefault();
        var (recommended, planNote) = Reconcile(rating, plannedToday, illness, rhrDelta);

        // --- Taper context ---
        string? taperNote = null;
        if (plan.DaysToRace is int dtr && dtr <= 21)
        {
            taperNote = dtr <= 7
                ? "Wettkampfwoche — Umfang niedrig halten, mit kurzen Steigerungen im Renntempo scharf bleiben und dem Taper vertrauen. Keine ungeplanten Einheiten."
                : "Taper-Phase — der Umfang sinkt, etwas Intensität bleibt. Widerstehe der Versuchung, harte Extra-Einheiten einzubauen.";
        }

        // --- Already trained today? Don't prescribe a second session; pivot to recovery. ---
        var completedToday = (activities ?? Array.Empty<ActivitySummary>())
            .Where(a => a.Date == todayKey)
            .ToList();
        var nutritionSession = recommended;
        if (completedToday.Count > 0)
        {
            var doneKm = completedToday.Sum(a => a.DistanceKm ?? 0);
            var doneMin = completedToday.Sum(a => a.DurationMin ?? 0);
            var summary = string.Join(", ", completedToday.Select(DescribeActivity));

            // Sport must match: a run doesn't satisfy a planned strength session and vice versa.
            bool DoneIs(string key) => completedToday.Any(a => (a.Type ?? "").Contains(key, StringComparison.OrdinalIgnoreCase));
            var sportMatches = plannedToday?.Type switch
            {
                SessionType.Strength => DoneIs("strength") || DoneIs("kraft"),
                SessionType.Easy or SessionType.Long or SessionType.Quality or SessionType.Race => DoneIs("run") || DoneIs("lauf"),
                _ => true, // Rest / Other / no plan: any completed activity counts
            };
            var coversPlan = plannedToday is null
                || plannedToday.Type is SessionType.Rest
                || (sportMatches && (
                       (plannedToday.DistanceKm is double pk && pk > 0 && doneKm >= pk * 0.6)
                    || (plannedToday.DurationMin is double pm && pm > 0 && doneMin >= pm * 0.6)
                    || (plannedToday.DistanceKm is null && plannedToday.DurationMin is null && (doneKm >= 5 || doneMin >= 30))));

            if (coversPlan)
            {
                var doneType = InferDoneType(doneKm, completedToday);
                // Fuel for what was actually done + the (possibly downgraded) recommendation — NOT the
                // planned type, which Reconcile may have just vetoed on a red-recovery day.
                nutritionSession = Hardest(recommended, doneType);
                rationale.Insert(0, $"Heute bereits erledigt: {summary}. Tagesziel erfüllt — jetzt Regeneration, Auffüllen (Carbs + Eiweiß) und Schlaf.");
                planNote = $"✅ Heute schon trainiert ({summary}). Keine weitere harte Einheit nötig — locker auslaufen/mobilisieren ist ok.";
            }
            else
            {
                rationale.Insert(0, $"Heute bereits absolviert: {summary} — die geplante Schlüsseleinheit steht aber noch aus.");
            }
        }

        // --- Rationale ---
        if (flags.Count == 0)
            rationale.Add("Erholungssignale liegen in deinem normalen Bereich.");
        else
            rationale.Add("Ausschlaggebend: " + string.Join("; ", flags));
        if (status?.StatusPhrase is { Length: > 0 } sp)
            rationale.Add($"Trainingsstatus: {Humanize(sp)}.");

        var trainedAndDone = completedToday.Count > 0 && planNote?.StartsWith("✅") == true;
        var headline = trainedAndDone
            ? $"✅ Erledigt — Regeneration ({completedToday.Sum(a => a.DistanceKm ?? 0):0.#} km)"
            : Headline(rating, recommended, plannedToday);

        // --- Pace zones + today's target ---
        var paces = PaceCalculator.FromPredictions(race);
        var todayTarget = trainedAndDone ? null
            : paces is not null ? PaceCalculator.TargetForSession(paces, recommended) : null;

        // --- Goal projection (parsed goal vs Garmin marathon prediction) ---
        int? goalSeconds = GoalParser.ToSeconds(goal);
        int? goalGap = null; bool? onTrack = null;
        if (goalSeconds is int gs && race?.MarathonSeconds is int pms)
        {
            goalGap = pms - gs;
            onTrack = pms <= gs;
        }

        // --- Endurance reality check: Garmin's marathon prediction is VO2max/tempo-based and knows
        // nothing about whether the long run needed for 42.2 km has actually been built. Look across
        // a ~10-week block (not just this week) so a taper/deload week never hides a real long-run
        // base — a single week's longest run would falsely trigger this on every taper week. ---
        double? longestRunKm = null;
        if (activities is not null)
        {
            var lookback = today.AddDays(-70);
            var runs = activities
                .Where(a => a.IsRun && a.DistanceKm is > 0 && DateOnly.TryParse(a.Date, out var ad) && ad >= lookback && ad <= today)
                .Select(a => a.DistanceKm!.Value)
                .ToList();
            if (runs.Count > 0) longestRunKm = runs.Max();
        }
        string? enduranceCaveat = null;
        if (race?.MarathonSeconds is not null && longestRunKm is double lr && lr < 29)
        {
            // Once in taper (<=21 days out) it's too late to safely build a longer run — doing so
            // would blow the taper and add injury/glycogen-depletion risk with no time to recover
            // before race day. Keep the caveat purely informational then; only recommend building the
            // long run further out, when there's still time to safely act on it.
            var inTaper = plan.DaysToRace is int dtr2 && dtr2 <= 21;
            enduranceCaveat = inTaper
                ? $"Die Prognose beruht auf Tempo-/VO₂max-Daten, nicht auf einem bestätigten langen Lauf nahe Marathon-Distanz (bisher längster Lauf: {lr:0.#} km). Dafür ist es jetzt zu spät — keinen längeren Lauf mehr nachholen, sondern dem Taper vertrauen und im Rennen eher konservativ starten."
                : lr < 20
                    ? $"Die Prognose basiert auf Tempo-/VO₂max-Daten, nicht auf einem langen Lauf nahe Marathon-Distanz (bisher längster Lauf: {lr:0.#} km) — für 42,2 km ist die Ausdauerbasis noch nicht bestätigt. Longrun schrittweise Richtung 29–32 km aufbauen."
                    : $"Ausdauerbasis wächst (bisher längster Lauf {lr:0.#} km) — für eine verlässlichere Prognose hilft ein Longrun näher an 29–32 km.";
        }

        return new DailyCoaching
        {
            Date = todayKey,
            Readiness = rating,
            ReadinessScore = readiness?.Score,
            ReadinessLevel = readiness?.Level,
            Recommended = recommended,
            Headline = headline,
            Rationale = rationale,
            Flags = flags,
            PlanToday = plan.Today,
            Upcoming = plan.Upcoming,
            PlanNote = planNote,
            NextLongRun = plan.NextLongRun,
            NextQuality = plan.NextQuality,
            TrainingStatus = status?.StatusPhrase is { Length: > 0 } s ? Humanize(s) : null,
            Acwr = status?.Acwr,
            AcwrStatus = status?.AcwrStatus,
            Vo2Max = status?.Vo2Max,
            Race = race,
            RaceDate = plan.RaceDate,
            DaysToRace = plan.DaysToRace,
            Goal = goal,
            GoalSeconds = goalSeconds,
            GoalGapSeconds = goalGap,
            OnTrackForGoal = onTrack,
            TaperNote = taperNote,
            Nutrition = NutritionEngine.Compute(nutritionSession, weightKg, todayM?.Calories),
            CompletedToday = completedToday,
            Paces = paces,
            TodayTargetPace = todayTarget,
            LongestRunKm = longestRunKm,
            EnduranceCaveat = enduranceCaveat,
        };
    }

    private static string DescribeActivity(ActivitySummary a)
    {
        var name = a.Name ?? a.Type ?? "Aktivität";
        var bits = new List<string>();
        if (a.DistanceKm is double km) bits.Add($"{km:0.#} km");
        if (a.DurationMin is double m) bits.Add($"{m:0} min");
        return bits.Count > 0 ? $"{name} ({string.Join(", ", bits)})" : name;
    }

    private static SessionType InferDoneType(double doneKm, IReadOnlyList<ActivitySummary> done)
    {
        if (doneKm >= 16) return SessionType.Long;
        var avgHr = done.Select(a => a.AverageHr).Where(h => h.HasValue).Select(h => h!.Value).DefaultIfEmpty(0).Max();
        if (avgHr >= 155 || (doneKm >= 8 && avgHr >= 150)) return SessionType.Quality;
        return SessionType.Easy;
    }

    private static SessionType Hardest(params SessionType?[] types)
    {
        static int Rank(SessionType t) => t switch
        {
            SessionType.Rest => 0,
            SessionType.Easy => 1,
            SessionType.Strength => 2,
            SessionType.Quality => 3,
            SessionType.Long => 4,
            SessionType.Race => 5,
            _ => 1,
        };
        return types.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty(SessionType.Easy).MaxBy(Rank);
    }

    private static (SessionType, string?) Reconcile(Readiness rating, PlannedWorkout? planned, bool illness, double? rhrDelta)
    {
        var plannedType = planned?.Type;

        if (rating == Readiness.Red)
        {
            var rest = illness || rhrDelta >= 7;
            return plannedType switch
            {
                SessionType.Quality or SessionType.Long or SessionType.Race =>
                    (rest ? SessionType.Rest : SessionType.Easy,
                     $"Plan sieht {Describe(planned)} vor, aber die Erholung ist rot — tausche auf {(rest ? "RUHE" : "sehr locker")} und verschiebe die Schlüsseleinheit auf einen frischeren Tag."),
                SessionType.Easy =>
                    (rest ? SessionType.Rest : SessionType.Easy, rest ? "Nimm den lockeren Tag heute als kompletten RUHETAG." : "Halte ihn wirklich locker."),
                _ => (rest ? SessionType.Rest : SessionType.Easy, "Erholung ist rot — Ruhe bzw. sehr locker priorisieren."),
            };
        }

        if (rating == Readiness.Amber)
        {
            return plannedType switch
            {
                SessionType.Quality =>
                    (SessionType.Easy, $"Plan sieht {Describe(planned)} vor — Erholung ist gelb, also REDUZIEREN (Intensität/Umfang) oder einen Tag verschieben."),
                SessionType.Long =>
                    (SessionType.Long, $"Mach den langen Lauf ({Describe(planned)}), aber am lockeren Ende des Tempos; lass eingebaute Renntempo-Abschnitte weg."),
                SessionType.Rest => (SessionType.Rest, "Ruhetag wie geplant — lass die Werte zurückkommen."),
                _ => (SessionType.Easy, "Heute aerob und im Plaudertempo bleiben; Qualität verschieben."),
            };
        }

        // Grün
        return plannedType switch
        {
            null => (SessionType.Easy, "Heute keine Einheit geplant — ein lockerer Dauerlauf oder Ruhe passen beide."),
            SessionType.Rest => (SessionType.Rest, "Ruhetag wie geplant — Erholung sieht gut aus, aber Ruhe verdient den nächsten harten Tag."),
            _ => (planned!.Type, $"Grünes Licht — die geplante {Describe(planned)} wie vorgesehen durchziehen."),
        };
    }

    private static string Headline(Readiness rating, SessionType recommended, PlannedWorkout? planned)
    {
        var color = rating switch { Readiness.Green => "🟢", Readiness.Amber => "🟡", _ => "🔴" };
        var rec = recommended switch
        {
            SessionType.Rest => "RUHETAG",
            SessionType.Easy => "LOCKER / ERHOLUNG",
            SessionType.Long => "LANGER LAUF",
            SessionType.Quality => "QUALITÄT (hart)",
            SessionType.Strength => "KRAFT",
            SessionType.Race => "WETTKAMPF",
            _ => "MODERAT / AEROB",
        };
        return $"{color} {rec}";
    }

    private static string Describe(PlannedWorkout? p) =>
        p is null ? "die Einheit" :
        $"{p.Title ?? p.Type.ToString()}{(p.DistanceKm is double km ? $" ({km} km)" : p.DurationMin is double m ? $" ({m:0} min)" : "")}";

    private static string Humanize(string phrase) =>
        phrase.Replace('_', ' ').ToLowerInvariant() switch
        {
            var s when s.StartsWith("productive") => "produktiv",
            var s when s.StartsWith("maintaining") => "erhaltend",
            var s when s.StartsWith("recovery") => "Erholung",
            var s when s.StartsWith("detraining") => "Formverlust",
            var s when s.StartsWith("unproductive") => "unproduktiv",
            var s when s.StartsWith("overreaching") => "Überlastung",
            var s when s.StartsWith("peaking") => "Formaufbau (Peaking)",
            var s => s,
        };

    private static double? Avg(IEnumerable<DayMetrics> days, Func<DayMetrics, int?> selector)
    {
        var vals = days.Select(selector).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        return vals.Count > 0 ? vals.Average() : null;
    }
}
