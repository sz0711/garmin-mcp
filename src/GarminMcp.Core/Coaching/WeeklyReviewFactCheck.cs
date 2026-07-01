using System.Globalization;
using System.Text.RegularExpressions;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

/// <summary>
/// Lightweight, best-effort numeric fact-check for the LLM-generated weekly review. The review
/// runs unattended every week with no human in the loop, so this extracts numbers mentioned in
/// the free-text review and flags any that don't reasonably match a known fact fed to the LLM in
/// its own prompt (see LlmCoach.BuildWeeklyPrompt — the known-facts list here is intentionally
/// kept in sync with exactly what that prompt actually sends, so a legitimate restatement of a
/// given fact never gets flagged).
///
/// This is a DIAGNOSTIC safety net, not a hard gate — a flagged review still gets published. A
/// naive numeric checker has real false-positive risk (legitimate rounding, derived percentages,
/// or paraphrasing can all produce numbers that don't literally match a source value), so blocking
/// publication on any mismatch would risk suppressing a perfectly good review over a benign
/// rephrasing. The goal is a stderr trail so a real hallucination (e.g. an invented distance or
/// session count) is at least visible after the fact, not silently trusted.
/// </summary>
public static class WeeklyReviewFactCheck
{
    // Optional leading sign (ASCII '-' and the Unicode minus some LLMs/fonts emit) so a legitimately
    // negative fact (TSB is commonly negative during a training build) isn't silently parsed as
    // positive and then always mismatched.
    private static readonly Regex NumberPattern = new(@"(-|−)?\s*\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    // Strip date-like substrings before number extraction: LlmCoach.BuildWeeklyPrompt feeds the LLM
    // several ISO dates (Stichtag, NextLongRun/NextQuality/RaceDate) which it's free to restate as
    // "am 15.07." or the raw ISO string — without this, the day/month/year fragments get extracted
    // as spurious numbers that will essentially never match a training-metric known fact, producing
    // a near-guaranteed false "unverified number" almost every week a date is mentioned.
    private static readonly Regex DatePattern = new(@"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}\.\d{1,2}\.(?:\d{2,4})?", RegexOptions.Compiled);

    /// <summary>Numbers (as written, e.g. "14,5" or "-8") extracted from <paramref name="reviewText"/>
    /// that don't reasonably match any known fact. Empty when everything checks out.</summary>
    public static List<string> FindUnverifiedNumbers(
        string reviewText, WeeklyStats week, DailyCoaching? coaching, IReadOnlyList<DayMetrics>? recentDays = null,
        PacingAnalysis? pacing = null)
    {
        if (string.IsNullOrWhiteSpace(reviewText)) return new List<string>();

        // Only facts BuildWeeklyPrompt actually sends the LLM — a fact the model never saw (e.g.
        // ElevationM, which the prompt doesn't mention) must not sit in this list: it wouldn't just
        // add noise, it could silently "verify" an unrelated hallucinated number that happens to
        // land within tolerance of it.
        var known = new List<double> { week.Km, week.Hours, week.Sessions, week.LongestKm, week.IntensityMinutes };
        // BuildWeeklyPrompt states the pacing analysis's distance ("Letzter Longrun (..., X km)").
        // PercentDifference/AerobicDecouplingPercent don't need to be listed here: they're always
        // rendered with a trailing '%' in the prompt, which the percentage skip-rule below already
        // treats as a derived ratio, not a fact to verify against.
        if (pacing is not null) known.Add(pacing.DistanceKm);
        if (coaching is not null)
        {
            // The weekly-recap sentence in BuildWeeklyPrompt states LAST week's plan adherence
            // (Planerfüllung letzte Woche: DoneLastWeek/PlannedLastWeek, DoneKmLastWeek/PlannedKmLastWeek)
            // — the primary numbers this fact-checker exists to verify. PlannedThisWeek/DoneThisWeek
            // are kept too since BuildWeeklyPrompt also mentions the week ahead's planned session count.
            if (coaching.PlannedLastWeek is int plw) known.Add(plw);
            if (coaching.DoneLastWeek is int dlw) known.Add(dlw);
            if (coaching.PlannedKmLastWeek is double pklw) known.Add(pklw);
            if (coaching.DoneKmLastWeek is double dklw) known.Add(dklw);
            if (coaching.PlannedThisWeek is int pt) known.Add(pt);
            if (coaching.DoneThisWeek is int dt) known.Add(dt);
            if (coaching.PlannedKmThisWeek is double pk) known.Add(pk);
            if (coaching.DoneKmThisWeek is double dk) known.Add(dk);
            if (coaching.Ctl is double ctl) known.Add(Math.Round(ctl));
            if (coaching.Atl is double atl) known.Add(Math.Round(atl));
            if (coaching.Tsb is double tsb) known.Add(Math.Round(tsb));
            if (coaching.DaysToRace is int d) known.Add(d);
        }
        // BuildWeeklyPrompt also renders each of the last 7 days' resting HR/HRV/sleep hours under an
        // "Erholung (letzte Tage)" block, and explicitly asks the LLM to comment on "Erholungstrend" —
        // without these, any legitimate recovery number the review cites can never match.
        if (recentDays is not null)
        {
            foreach (var d in recentDays)
            {
                if (d.RestingHeartRate is int rhr) known.Add(rhr);
                if (d.HrvLastNight is int hrv) known.Add(hrv);
                if (d.SleepHours is double sh) known.Add(sh);
            }
        }

        var textWithoutDates = DatePattern.Replace(reviewText, " ");

        var unverified = new List<string>();
        foreach (Match m in NumberPattern.Matches(textWithoutDates))
        {
            // Percentages are usually a derived ratio the LLM computed itself (e.g. "60% des
            // Ziels") rather than a fact copied from the prompt — not a source-value mismatch.
            var after = textWithoutDates[(m.Index + m.Length)..].TrimStart();
            if (after.StartsWith('%')) continue;

            var raw = m.Value.Replace(" ", "").Replace(",", ".").Replace("−", "-");
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            // Small numbers (1, "eine Sache", weekday-like counts) are too ambiguous to usefully
            // fact-check and would just create noise.
            if (Math.Abs(value) < 2) continue;

            var matchesKnownFact = known.Any(k => Math.Abs(k - value) <= Math.Max(0.5, Math.Abs(k) * 0.15));
            if (!matchesKnownFact) unverified.Add(m.Value.Trim());
        }
        return unverified;
    }
}
