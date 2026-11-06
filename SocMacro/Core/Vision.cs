using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SocMacro.Core;

/// <summary>Template matching and pixel rules that OCR can't cover.</summary>
public sealed class Vision : IDisposable
{
    private readonly Dictionary<string, Mat> templates = [];

    private const string ResourcePrefix = "SocMacro.Assets.templates.";

    /// <summary>Templates are embedded in the assembly, so the single exe works without an Assets folder.</summary>
    public Vision()
    {
        var asm = typeof(Vision).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".png")))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            templates[res[ResourcePrefix.Length..^4]] = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
        }
        if (templates.Count == 0) throw new InvalidOperationException("내장된 화면 인식 이미지를 찾을 수 없습니다.");
    }

    /// <summary>All matches of a template (captured at base resolution) above a threshold, best first.</summary>
    public List<(Box Box, double Score)> MatchAll(Frame f, string name, double threshold, int max = 8)
    {
        var tpl = templates[name];
        var img = f.BaseImage;
        using var res = new Mat();
        Cv2.MatchTemplate(img, tpl, res, TemplateMatchModes.CCoeffNormed);
        var hits = new List<(Box, double)>();
        while (hits.Count < max)
        {
            Cv2.MinMaxLoc(res, out _, out double score, out _, out Point loc);
            if (score < threshold) break;
            hits.Add((new Box(loc.X, loc.Y, tpl.Width, tpl.Height), score));
            Cv2.Rectangle(res, new Rect(loc.X - tpl.Width / 2, loc.Y - tpl.Height / 2, tpl.Width, tpl.Height), Scalar.All(-1), -1);
        }
        return hits;
    }

    /// <summary>
    /// The clickable (active) event node on the exploration map. Locked nodes have the same
    /// clover shape but are grayscale, so a saturation check separates them.
    /// </summary>
    public Box? FindActiveEventNode(Frame f)
    {
        foreach (var (box, _) in MatchAll(f, "node_event_active", 0.8))
        {
            if (MeanSaturation(f.BaseImage, (int)box.CX, (int)box.CY, 12) > 40) return box;
        }
        return null;
    }

    private static readonly Box HudArea = new(60, 0, 300, 80);
    private static readonly Box DefeatButtons = new(300, 560, 700, 120);

    /// <summary>
    /// The "전투 실패" result screen. Its stylized title is often misread (e.g. "구두 실패"), so the
    /// "다시 시도" button is matched by template as well. Scores: this screen ≥0.94, others ≤0.32.
    /// </summary>
    public bool IsDefeatScreen(Frame f) =>
        f.HasAny("전투실패", "투실패") || BestScore(f, "defeat_retry", DefeatButtons) > 0.7;

    /// <summary>
    /// Battle HUD state from the top-left badges. Template matching is used because OCR misses
    /// this small text on busy maps. Scores: badge present ≈1.0, absent ≤0.35.
    /// </summary>
    public (bool InBattle, bool Auto) BattleHud(Frame f)
    {
        if (BestScore(f, "hud_speed", HudArea) < 0.8) return (false, false);
        return (true, BestScore(f, "hud_auto", HudArea) > BestScore(f, "hud_manual", HudArea));
    }

    private double BestScore(Frame f, string name, Box roi)
    {
        using var crop = new Mat(f.BaseImage, new Rect((int)roi.X, (int)roi.Y, (int)roi.W, (int)roi.H));
        using var res = new Mat();
        Cv2.MatchTemplate(crop, templates[name], res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double max);
        return max;
    }

    /// <summary>
    /// Deployed allies on the "캐릭터 배치" screen, found by their blue HP bars.
    /// Returns each ally's tile center (base coords). Deployment tiles differ per stage,
    /// so we locate units instead of tiles.
    /// </summary>
    public static List<(double X, double Y)> FindAllies(Frame f)
    {
        // Work on the captured pixels, not the 1274x717 copy: on small windows (800x450) the bars are
        // ~2px thick and upscaling blurs them away (found 2 of 6 allies). Sizes are scaled instead.
        var img = f.Image;
        double sx = f.Sx, sy = f.Sy;
        int w = img.Width, h = img.Height;
        var data = new byte[w * h * 3];
        using (var cont = img.IsContinuous() ? null : img.Clone())
            Marshal.Copy((cont ?? img).Data, data, 0, data.Length);

        bool Blue(int x, int y)
        {
            int i = (y * w + x) * 3;
            int b = data[i], g = data[i + 1], r = data[i + 2];
            return b >= 195 && b - r >= 60 && g >= 95;
        }

        // the bar is 3-6px thick at base size; above it and a few pixels below it is not blue
        int above = Math.Max(1, (int)Math.Round(2 * sy));
        int thick = Math.Max(1, (int)Math.Round(2 * sy));
        int below = Math.Max(thick + 2, (int)Math.Round(7 * sy));
        int minRun = Math.Max(8, (int)Math.Round(26 * sx));
        int rosterTop = (int)(540 * sy); // the character roster strip at the bottom has its own bars

        var bars = new List<(int X, int Y, int W)>();
        for (int y = above; y < Math.Min(h - below, rosterTop); y++)
        {
            int runStart = -1;
            for (int x = 0; x <= w; x++)
            {
                bool hit = false;
                if (x < w && !Blue(x, y - above) && !Blue(x, y + below))
                {
                    hit = true;
                    for (int k = 0; k <= thick && hit; k++) hit = Blue(x, y + k);
                }
                if (hit && runStart < 0) runStart = x;
                if (!hit && runStart >= 0)
                {
                    if (x - runStart >= minRun) bars.Add((runStart, y, x - runStart));
                    runStart = -1;
                }
            }
        }

        var allies = new List<(double, double)>();
        var kept = new List<(double X, double Y)>();
        foreach (var (px, py, _) in bars.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            double x = px / sx, y = py / sy; // back to base coordinates
            if (kept.Any(k => Math.Abs(k.X - x) < 10 && Math.Abs(k.Y - y) < 6)) continue;
            kept.Add((x, y));
            // tile center relative to the bar's left end, measured on several stages
            allies.Add((x + 13, y + 118));
        }
        return allies;
    }
    private static double MeanSaturation(Mat img, int cx, int cy, int r)
    {
        var rect = new Rect(cx - r, cy - r, 2 * r, 2 * r).Intersect(new Rect(0, 0, img.Width, img.Height));
        using var patch = new Mat(img, rect);
        using var hsv = new Mat();
        Cv2.CvtColor(patch, hsv, ColorConversionCodes.BGR2HSV);
        return Cv2.Mean(hsv).Val1;
    }

    public void Dispose()
    {
        foreach (var t in templates.Values) t.Dispose();
    }
}
