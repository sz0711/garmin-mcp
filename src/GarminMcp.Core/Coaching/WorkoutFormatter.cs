using System.Text;
using Garmin.Connect.Models;

namespace GarminMcp.Core.Coaching;

/// <summary>Turns a Garmin workout's structure into a short human-readable line for the coach/LLM.</summary>
public static class WorkoutFormatter
{
    public static string Summarize(GarminWorkout w)
    {
        var head = w.WorkoutName ?? "Workout";
        var sport = w.SportType?.SportTypeKey;
        string? est = w.EstimatedDurationInSecs > 0
            ? $"~{Math.Round(w.EstimatedDurationInSecs / 60.0)} min"
            : w.EstimatedDistanceInMeters > 0
                ? $"~{Math.Round(w.EstimatedDistanceInMeters / 1000.0, 1)} km"
                : null;

        var steps = (w.WorkoutSegments ?? Array.Empty<GarminWorkoutSegment>())
            .OrderBy(s => s.SegmentOrder)
            .SelectMany(s => (s.WorkoutSteps ?? Array.Empty<GarminWorkoutStep>()).OrderBy(x => x.StepOrder))
            .Select(FormatStep)
            .Where(x => !string.IsNullOrEmpty(x))
            .Take(14)
            .ToList();

        var sb = new StringBuilder(head);
        if (!string.IsNullOrEmpty(sport)) sb.Append($" [{sport}]");
        if (est is not null) sb.Append($" ({est})");
        if (steps.Count > 0) sb.Append(": ").Append(string.Join(" → ", steps));
        return sb.ToString();
    }

    private static string FormatStep(GarminWorkoutStep s)
    {
        var type = Pretty(s.StepType?.StepTypeKey ?? s.Type);
        var ec = EndCondition(s);
        var tg = Target(s);
        var r = type;
        if (ec is not null) r += " " + ec;
        if (tg is not null) r += " @ " + tg;
        return r;
    }

    private static string Pretty(string? key) => (key ?? "step").ToLowerInvariant() switch
    {
        "warmup" => "WU",
        "cooldown" => "CD",
        "interval" => "Interval",
        "recovery" => "Recovery",
        "rest" => "Rest",
        "run" => "Run",
        "main" => "Main",
        var k when k.Contains("repeat") => "Repeat",
        var k => k,
    };

    private static string? EndCondition(GarminWorkoutStep s)
    {
        var key = s.EndCondition?.ConditionTypeKey;
        var v = s.EndConditionValue ?? 0;
        if (key is null) return null;
        if (key.Contains("time")) return FormatDuration(v);
        if (key.Contains("distance")) return v >= 1000 ? $"{Math.Round(v / 1000.0, 2)} km" : $"{Math.Round(v)} m";
        if (key.Contains("lap")) return "until lap";
        return null;
    }

    private static string? Target(GarminWorkoutStep s)
    {
        var key = s.TargetType?.WorkoutTargetTypeKey;
        if (string.IsNullOrEmpty(key) || key.Contains("no.target")) return null;

        if (key.Contains("pace") || key.Contains("speed"))
        {
            var p1 = Pace(s.TargetValueOne);
            var p2 = Pace(s.TargetValueTwo);
            if (p1 is null && p2 is null) return null;
            if (p1 is not null && p2 is not null)
                return PaceSeconds(p1) <= PaceSeconds(p2) ? $"{p1}–{p2}/km" : $"{p2}–{p1}/km";
            return $"{p1 ?? p2}/km";
        }
        if (key.Contains("heart") || key.Contains("hr"))
        {
            if (s.ZoneNumber is long z && z > 0) return $"HR Z{z}";
            if (s.TargetValueOne is > 0 && s.TargetValueTwo is > 0)
                return $"{Math.Round(s.TargetValueOne.Value)}–{Math.Round(s.TargetValueTwo.Value)} bpm";
            return "HR zone";
        }
        if (s.ZoneNumber is long zn && zn > 0) return $"Z{zn}";
        return null;
    }

    private static string? Pace(double? speedMetersPerSecond)
    {
        if (speedMetersPerSecond is not double v || v <= 0) return null;
        var ts = TimeSpan.FromSeconds(1000.0 / v);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static int PaceSeconds(string pace)
    {
        var parts = pace.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s)
            ? m * 60 + s : int.MaxValue;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00} min";
    }
}
