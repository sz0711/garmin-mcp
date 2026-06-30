using System.Globalization;
using System.Text;

namespace GarminMcp.Core.Reporting;

/// <summary>
/// Self-contained inline-SVG charts (no external JS/CSS) for the HTML dashboard:
/// line charts with gridlines/axes/optional baseline, and bar charts. Colours for axes
/// and text use the page's CSS variables so they adapt to light/dark mode.
/// </summary>
public static class SvgCharts
{
    private const int W = 520, H = 170, PadL = 40, PadR = 12, PadT = 10, PadB = 22;

    public static string Line(IReadOnlyList<string> labels, IReadOnlyList<double?> values, string color, double? baseline = null)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return Empty();

        double min = present.Min(), max = present.Max();
        if (baseline is double b) { min = Math.Min(min, b); max = Math.Max(max, b); }
        (min, max) = Pad(min, max);

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" preserveAspectRatio=\"none\">");
        Grid(sb, min, max);

        double X(int i) => labels.Count <= 1 ? PadL + (W - PadL - PadR) / 2.0 : PadL + (double)i / (labels.Count - 1) * (W - PadL - PadR);
        double Y(double v) => max > min ? PadT + (1 - (v - min) / (max - min)) * (H - PadT - PadB) : (H - PadB + PadT) / 2.0;

        if (baseline is double bl)
            sb.Append($"<line x1=\"{PadL}\" y1=\"{F(Y(bl))}\" x2=\"{W - PadR}\" y2=\"{F(Y(bl))}\" stroke=\"{color}\" stroke-width=\"1\" stroke-dasharray=\"4 3\" opacity=\"0.5\"/>");

        var pts = new List<(double x, double y)>();
        for (var i = 0; i < values.Count; i++)
            if (values[i] is double v) pts.Add((X(i), Y(v)));

        if (pts.Count > 1)
        {
            var poly = string.Join(" ", pts.Select(p => $"{F(p.x)},{F(p.y)}"));
            var area = $"M {F(pts[0].x)},{F(H - PadB)} L " + string.Join(" ", pts.Select(p => $"{F(p.x)},{F(p.y)}")) + $" L {F(pts[^1].x)},{F(H - PadB)} Z";
            sb.Append($"<path d=\"{area}\" fill=\"{color}\" opacity=\"0.12\"/>");
            sb.Append($"<polyline points=\"{poly}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"/>");
        }
        if (pts.Count > 0)
        {
            var last = pts[^1];
            sb.Append($"<circle cx=\"{F(last.x)}\" cy=\"{F(last.y)}\" r=\"2.6\" fill=\"{color}\"/>");
        }

        XLabels(sb, labels, X);
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string Bars(IReadOnlyList<string> labels, IReadOnlyList<double?> values, string color)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return Empty();

        double max = Math.Max(present.Max(), 0.0001);
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" preserveAspectRatio=\"none\">");
        Grid(sb, 0, max);

        var n = Math.Max(1, labels.Count);
        var slot = (double)(W - PadL - PadR) / n;
        var bw = Math.Max(1.0, slot * 0.66);
        double Y(double v) => PadT + (1 - v / max) * (H - PadT - PadB);

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not double v) continue;
            var x = PadL + i * slot + (slot - bw) / 2.0;
            var y = Y(v);
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(H - PadB - y)}\" rx=\"1.5\" fill=\"{color}\" opacity=\"0.85\"/>");
        }

        double X(int i) => PadL + i * slot + slot / 2.0;
        XLabels(sb, labels, X);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Grid(StringBuilder sb, double min, double max)
    {
        for (var k = 0; k <= 2; k++)
        {
            var y = PadT + k / 2.0 * (H - PadT - PadB);
            var val = max - k / 2.0 * (max - min);
            sb.Append($"<line x1=\"{PadL}\" y1=\"{F(y)}\" x2=\"{W - PadR}\" y2=\"{F(y)}\" stroke=\"var(--line)\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{PadL - 4}\" y=\"{F(y + 3)}\" text-anchor=\"end\" font-size=\"9\" fill=\"var(--muted)\">{Round(val)}</text>");
        }
    }

    private static void XLabels(StringBuilder sb, IReadOnlyList<string> labels, Func<int, double> x)
    {
        if (labels.Count == 0) return;
        var idxs = labels.Count == 1 ? new[] { 0 } : new[] { 0, labels.Count / 2, labels.Count - 1 };
        foreach (var i in idxs.Distinct())
            sb.Append($"<text x=\"{F(x(i))}\" y=\"{H - 6}\" text-anchor=\"middle\" font-size=\"9\" fill=\"var(--muted)\">{Short(labels[i])}</text>");
    }

    private static (double, double) Pad(double min, double max)
    {
        if (max <= min) return (min - 1, max + 1);
        var p = (max - min) * 0.1;
        return (min - p, max + p);
    }

    private static string Short(string isoDate) =>
        isoDate.Length >= 10 ? isoDate.Substring(5) : isoDate; // MM-DD

    private static string Round(double v) =>
        (Math.Abs(v) >= 100 ? Math.Round(v) : Math.Round(v, 1)).ToString(CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Empty() =>
        $"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\"><text x=\"{W / 2}\" y=\"{H / 2}\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--muted)\">keine Daten</text></svg>";
}
