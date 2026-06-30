using System.Text;

namespace GarminMcp.Core.Reporting;

/// <summary>Tiny Unicode block sparkline (renders everywhere, incl. the GitHub app).</summary>
public static class Sparkline
{
    private const string Blocks = "▁▂▃▄▅▆▇█";

    public static string Render(IEnumerable<double?> values)
    {
        var list = values.ToList();
        var present = list.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0)
            return "";

        double min = present.Min(), max = present.Max();
        var sb = new StringBuilder(list.Count);
        foreach (var v in list)
        {
            if (!v.HasValue)
            {
                sb.Append(' ');
                continue;
            }
            var idx = max > min
                ? (int)Math.Round((v.Value - min) / (max - min) * (Blocks.Length - 1))
                : Blocks.Length / 2;
            sb.Append(Blocks[Math.Clamp(idx, 0, Blocks.Length - 1)]);
        }
        return sb.ToString();
    }
}
