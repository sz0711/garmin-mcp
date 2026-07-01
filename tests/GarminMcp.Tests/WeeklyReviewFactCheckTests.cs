using GarminMcp.Core.Coaching;
using GarminMcp.Core.Reporting;
using Xunit;

namespace GarminMcp.Tests;

public class WeeklyReviewFactCheckTests
{
    private static readonly WeeklyStats Week = new()
    {
        Km = 14.5, Hours = 2.0, Sessions = 4, LongestKm = 7.7, ElevationM = 120, IntensityMinutes = 30,
    };

    [Fact]
    public void FindUnverifiedNumbers_ReturnsEmpty_WhenAllNumbersMatchKnownFacts()
    {
        var text = "Die letzte Woche warst du 14,5 km in 4 Einheiten unterwegs, dein längster Lauf war 7,7 km.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_FlagsInventedNumber()
    {
        // 42 km is not close to any known fact (Km=14.5, LongestKm=7.7, etc.) — a plausible
        // hallucination (e.g. the LLM inventing a "marathon-length week").
        var text = "Du bist diese Woche 42 km gelaufen, das ist eine tolle Leistung!";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Contains("42", unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_IgnoresPercentages()
    {
        // A derived ratio the LLM computed itself, not a claim that should match a raw source value.
        var text = "Du hast damit 60% deines Wochenziels erreicht.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_IgnoresSmallAmbiguousNumbers()
    {
        var text = "Konzentriere dich auf 1 Sache diese Woche.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_MatchesCoachingFacts_NotJustWeeklyStats()
    {
        var coaching = new DailyCoaching { Ctl = 45, DaysToRace = 21 };
        var text = "Deine Fitness (CTL) liegt bei 45, noch 21 Tage bis zum Rennen.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, coaching);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_AllowsSmallRoundingTolerance()
    {
        // 14.5 rounded/paraphrased as "etwa 15 km" should not be flagged (within 15% relative tolerance).
        var text = "Du bist etwa 15 km gelaufen.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_MatchesNegativeTsb_NotStrippedOfItsSign()
    {
        // A negative TSB (fatigued, common during a training build) must not be parsed as its
        // positive absolute value and then always mismatch.
        var coaching = new DailyCoaching { Tsb = -8 };
        var text = "Dein Form/TSB liegt aktuell bei -8, du bist also etwas ermüdet.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, coaching);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_IgnoresDatesInGermanAndIsoFormat()
    {
        var text = "Dein nächster langer Lauf ist am 15.07. geplant, das Rennen findet am 2026-08-30 statt.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_MatchesPlannedLastWeekFacts()
    {
        // These are the exact numbers BuildWeeklyPrompt's "Planerfüllung letzte Woche" sentence
        // states -- the primary use case this fact-checker exists to verify.
        var coaching = new DailyCoaching { PlannedLastWeek = 5, DoneLastWeek = 3, PlannedKmLastWeek = 40, DoneKmLastWeek = 28 };
        var text = "Letzte Woche hast du 3 von 5 geplanten Einheiten geschafft, 28 von 40 km.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, coaching);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_MatchesRecentDayRecoveryMetrics()
    {
        var days = new List<DayMetrics> { new() { Date = "2026-06-30", RestingHeartRate = 47, HrvLastNight = 56, SleepHours = 7.7 } };
        var text = "Dein Ruhepuls lag zuletzt bei 47 bpm und deine HRV bei 56 ms, bei 7,7 h Schlaf.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null, days);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_DoesNotUseElevationAsAKnownFact()
    {
        // ElevationM (120) is never sent to the LLM (BuildWeeklyPrompt doesn't mention it), so it
        // must not sit in the known-facts list -- otherwise an unrelated hallucinated number that
        // happens to be near 120 would be silently "verified" even though the model never saw it.
        var text = "Du bist unglaubliche 121 km gelaufen!"; // close to ElevationM=120, NOT to Km=14.5

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Contains("121", unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_MatchesPacingDistance()
    {
        var pacing = new PacingAnalysis { ActivityDate = "2026-06-28", DistanceKm = 18.3, Verdict = SplitVerdict.Positive, PercentDifference = 6.2 };
        var text = "Dein Longrun über 18,3 km war ein Positive Split mit 6,2% Unterschied zwischen den Hälften.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null, null, pacing);

        Assert.Empty(unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_FlagsInventedPacingDistance_WhenNoPacingGiven()
    {
        // Without a pacing analysis passed in, a distance the prompt never sent must still be flagged.
        var text = "Dein Longrun über 18,3 km war stark.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null);

        Assert.Contains("18,3", unverified);
    }

    [Fact]
    public void FindUnverifiedNumbers_DoesNotFlagTheSplitVerdictLabel()
    {
        // LlmCoach.BuildWeeklyPrompt's verdict labels deliberately say "zweite Hälfte", not "2.
        // Hälfte" -- a bare ordinal digit there would survive the date/percent filters as a spurious
        // plain number whenever the LLM echoes the exact phrasing it was given (which it's asked to).
        var pacing = new PacingAnalysis { ActivityDate = "2026-06-28", DistanceKm = 18.3, Verdict = SplitVerdict.Positive, PercentDifference = 6.2 };
        var text = "Dein Longrun über 18,3 km war ein Positive Split/Fade (zweite Hälfte langsamer als die erste), mit 6,2% Unterschied.";

        var unverified = WeeklyReviewFactCheck.FindUnverifiedNumbers(text, Week, null, null, pacing);

        Assert.Empty(unverified);
    }
}
