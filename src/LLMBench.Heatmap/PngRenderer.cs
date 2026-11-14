namespace LLMBench.Heatmap;

/// <summary>
/// Pixel layout: every item is a 4x4 square with 1 pixel border on all sides (6 px pitch), one row per run,
/// a small extra gap between tiers, 4 px margin at top and bottom. Below the runs a separate row shows the
/// solve rate of each question over all shown runs (white = all solve, dark = none) - questions that do not
/// separate the models are the white and the dark cells there.
/// </summary>
public static class PngRenderer
{
    public const int Square = 4, Border = 1, Pitch = Square + 2 * Border, MarginY = 4;
    public const uint Background = 0x141414;

    public static int TierGap { get; set; } = 4;
    public static int ConsensusGap { get; set; } = 6;

    /// <summary>x position of every column (left edge of its 6 px cell).</summary>
    public static int[] ColumnX(HeatmapData d, out int width)
    {
        var xs = new int[d.Columns.Count];
        int x = 0;
        for (int c = 0; c < d.Columns.Count; c++)
        {
            if (c > 0 && d.Columns[c].Tier != d.Columns[c - 1].Tier) x += TierGap;
            xs[c] = x;
            x += Pitch;
        }
        width = x;
        return xs;
    }

    public static RgbImage Render(HeatmapData d)
    {
        var xs = ColumnX(d, out int width);
        int height = MarginY + d.Rows.Count * Pitch + ConsensusGap + Pitch + MarginY;
        var img = new RgbImage(width, height);
        img.Fill(Background);

        for (int r = 0; r < d.Rows.Count; r++)
        {
            int y = MarginY + r * Pitch;
            for (int c = 0; c < d.Columns.Count; c++)
                img.FillRect(xs[c] + Border, y + Border, Square, Square, HeatmapData.Color(d.Kind(d.Cells[r][c])));
        }

        int cy = MarginY + d.Rows.Count * Pitch + ConsensusGap;
        for (int c = 0; c < d.Columns.Count; c++)
            img.FillRect(xs[c] + Border, cy + Border, Square, Square, Gray(d.Columns[c].SolveRate));
        return img;
    }

    /// <summary>Solve rate 0 .. 1 as grey from dark (0x3C) to white.</summary>
    public static uint Gray(double rate)
    {
        uint v = (uint)Math.Round(0x3C + (0xFF - 0x3C) * Math.Clamp(rate, 0, 1));
        return (v << 16) | (v << 8) | v;
    }
}
