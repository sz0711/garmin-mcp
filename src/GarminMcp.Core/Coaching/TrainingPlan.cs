using Garmin.Connect.Models;

namespace GarminMcp.Core.Coaching;

public enum SessionType
{
    Rest,
    Easy,
    Long,
    Quality,
    Strength,
    Race,
    Other,
}

/// <summary>A single scheduled session from the (marathon) training plan.</summary>
public sealed class PlannedWorkout
{
    public string Date { get; set; } = "";       // yyyy-MM-dd
    public string? Title { get; set; }
    public string? SportType { get; set; }
    public SessionType Type { get; set; }
    public double? DurationMin { get; set; }
    public double? DistanceKm { get; set; }
    public long? WorkoutId { get; set; }
    public bool IsRace { get; set; }

    /// <summary>Human-readable workout structure (resolved on demand for key sessions).</summary>
    public string? Detail { get; set; }
}

/// <summary>What the plan calls for around today, plus the goal race.</summary>
public sealed class TrainingPlanView
{
    public bool HasPlan { get; set; }
    public List<PlannedWorkout> Today { get; set; } = new();
    public List<PlannedWorkout> Upcoming { get; set; } = new();
    public List<PlannedWorkout> AllPlanned { get; set; } = new();
    public PlannedWorkout? NextLongRun { get; set; }
    public PlannedWorkout? NextQuality { get; set; }
    public string? RaceDate { get; set; }
    public string? RaceTitle { get; set; }
    public int? DaysToRace { get; set; }
}

/// <summary>
/// Reads scheduled training-plan workouts from the Garmin calendar (3-week window) and
/// classifies them, so the coach can reconcile recovery with what the plan intends.
/// </summary>
public static class TrainingPlanReader
{
    public static async Task<TrainingPlanView> BuildAsync(
        IGarminService service, DateOnly today, CancellationToken cancellationToken = default)
    {
        var items = new Dictionary<long, GarminCalendarItem>();
        foreach (var offset in new[] { 0, 7, 14 })
        {
            try
            {
                var week = await service.GetCalendarWeekAsync(today.AddDays(offset).ToString("yyyy-MM-dd"), cancellationToken);
                foreach (var item in week.CalendarItems ?? Array.Empty<GarminCalendarItem>())
                    items[item.Id] = item;
            }
            catch
            {
                // calendar week unavailable — skip
            }
        }

        var view = new TrainingPlanView { HasPlan = items.Values.Any(i => i.TrainingPlanId is not null) };

        var planned = items.Values
            .Where(IsPlannedWorkout)
            .Select(Map)
            .OrderBy(p => p.Date, StringComparer.Ordinal)
            .ToList();

        view.AllPlanned = planned;

        var todayKey = today.ToString("yyyy-MM-dd");
        view.Today = planned.Where(p => p.Date == todayKey).ToList();
        view.Upcoming = planned.Where(p => string.CompareOrdinal(p.Date, todayKey) > 0).Take(10).ToList();

        var fromToday = planned.Where(p => string.CompareOrdinal(p.Date, todayKey) >= 0).ToList();
        view.NextLongRun = fromToday.FirstOrDefault(p => p.Type == SessionType.Long);
        view.NextQuality = fromToday.FirstOrDefault(p => p.Type == SessionType.Quality);

        var race = items.Values
            .Where(i => i.IsRace)
            .OrderBy(i => i.Date.ToString("yyyy-MM-dd"), StringComparer.Ordinal)
            .FirstOrDefault(i => i.Date >= today)
            ?? items.Values.FirstOrDefault(i => i.IsRace);
        if (race is not null)
        {
            view.RaceDate = race.Date.ToString("yyyy-MM-dd");
            view.RaceTitle = race.Title;
            view.DaysToRace = race.Date >= today ? race.Date.DayNumber - today.DayNumber : null;
        }

        // Resolve the detailed structure for the key sessions only (a few extra calls).
        foreach (var pw in new[] { view.Today.FirstOrDefault(), view.NextLongRun, view.NextQuality }
                     .Where(p => p?.WorkoutId is not null)
                     .Distinct())
        {
            try
            {
                var workout = await service.GetWorkoutAsync(pw!.WorkoutId!.Value, cancellationToken);
                pw.Detail = WorkoutFormatter.Summarize(workout);
            }
            catch
            {
                // workout detail is optional
            }
        }

        return view;
    }

    private static bool IsPlannedWorkout(GarminCalendarItem i) =>
        i.WorkoutId is not null
        || i.TrainingPlanId is not null
        || string.Equals(i.ItemType, "workout", StringComparison.OrdinalIgnoreCase);

    private static PlannedWorkout Map(GarminCalendarItem i) => new()
    {
        Date = i.Date.ToString("yyyy-MM-dd"),
        Title = i.Title,
        SportType = i.SportTypeKey,
        Type = Classify(i.Title, i.SportTypeKey, i.IsRace),
        DurationMin = i.Duration is > 0 ? Math.Round(i.Duration.Value / 60.0, 0) : null,
        DistanceKm = i.Distance is > 0 ? Math.Round(i.Distance.Value / 1000.0, 1) : null,
        WorkoutId = i.WorkoutId,
        IsRace = i.IsRace,
    };

    private static SessionType Classify(string? title, string? sport, bool isRace)
    {
        if (isRace) return SessionType.Race;
        var t = (title ?? string.Empty).ToLowerInvariant();
        var s = (sport ?? string.Empty).ToLowerInvariant();

        if (t.Contains("rest") || t.Contains("ruhe") || t.Contains("off")) return SessionType.Rest;
        if (s.Contains("strength") || s.Contains("kraft") || t.Contains("strength") || t.Contains("kraft")) return SessionType.Strength;
        if (t.Contains("long") || t.Contains("lang")) return SessionType.Long;
        if (t.Contains("tempo") || t.Contains("threshold") || t.Contains("schwelle") || t.Contains("interval")
            || t.Contains("intervall") || t.Contains("speed") || t.Contains("track") || t.Contains("repeat")
            || t.Contains("vo2") || t.Contains("fartlek") || t.Contains("race pace") || t.Contains("wettkampf")
            || t.Contains("sprint") || t.Contains("hill"))
            return SessionType.Quality;
        if (t.Contains("easy") || t.Contains("recovery") || t.Contains("locker") || t.Contains("regener") || t.Contains("base"))
            return SessionType.Easy;
        return SessionType.Other;
    }
}
