using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SocMacro.Core;

public sealed record OcrLine(string Text, string Norm, Box Box);

/// <summary>Windows built-in OCR (Korean). Boxes are returned in base coordinates.</summary>
public sealed partial class OcrService
{
    private readonly OcrEngine engine;

    public OcrService()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ko"))
            ?? throw new InvalidOperationException("한국어 OCR을 사용할 수 없습니다. Windows 설정 > 시간 및 언어 > 언어에서 한국어를 추가하세요.");
    }

    private const double MaxSkew = 3;
    private const double BandHeight = 150, BandOverlap = 30;

    public List<OcrLine> Read(Mat frame, double sx, double sy, Box? roi = null)
    {
        var r = roi ?? new Box(0, 0, GameWindow.BaseW, GameWindow.BaseH);
        var lines = ReadOnce(frame, sx, sy, r, out double angle);
        if (Math.Abs(angle) <= MaxSkew || r.H <= BandHeight) return lines;

        // The isometric floor's 2:1 diagonals (~26.6°) can fool the engine's deskew into reading the
        // whole screen rotated, losing almost all text. Thin horizontal bands don't trigger it.
        return ReadBanded(frame, sx, sy, r);
    }

    /// <summary>
    /// OCR in overlapping horizontal bands. Slower, but each band is read on its own, which recovers
    /// text the full-frame pass drops (busy backgrounds, skew, stylized titles next to other text).
    /// </summary>
    public List<OcrLine> ReadBanded(Mat frame, double sx, double sy, Box? roi = null)
    {
        var r = roi ?? new Box(0, 0, GameWindow.BaseW, GameWindow.BaseH);
        var merged = new List<OcrLine>();
        for (double y = r.Y; y < r.Y + r.H; y += BandHeight - BandOverlap)
        {
            var band = new Box(r.X, y, r.W, Math.Min(BandHeight, r.Y + r.H - y));
            foreach (var l in ReadOnce(frame, sx, sy, band, out _))
            {
                bool dup = merged.Any(m => m.Norm == l.Norm && Math.Abs(m.Box.CX - l.Box.CX) < 10 && Math.Abs(m.Box.CY - l.Box.CY) < 10);
                if (!dup) merged.Add(l);
            }
            if (band.H < BandHeight) break;
        }
        return merged;
    }

    private List<OcrLine> ReadOnce(Mat frame, double sx, double sy, Box r, out double textAngle)
    {
        int px = Math.Clamp((int)(r.X * sx), 0, frame.Width - 1);
        int py = Math.Clamp((int)(r.Y * sy), 0, frame.Height - 1);
        int pw = Math.Clamp((int)(r.W * sx), 1, frame.Width - px);
        int ph = Math.Clamp((int)(r.H * sy), 1, frame.Height - py);

        using var crop = new Mat(frame, new Rect(px, py, pw, ph));
        // OCR is most accurate at roughly 2x the base resolution
        double up = Math.Max(1.0, 2.0 / sx);
        up = Math.Min(up, (OcrEngine.MaxImageDimension - 1) / (double)Math.Max(pw, ph));
        using var big = new Mat();
        Cv2.Resize(crop, big, new Size(), up, up, InterpolationFlags.Cubic);
        using var bgra = new Mat();
        Cv2.CvtColor(big, bgra, ColorConversionCodes.BGR2BGRA);

        var bytes = new byte[bgra.Width * bgra.Height * 4];
        Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Ignore);
        var result = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();

        // When the engine thinks the text is skewed (e.g. a curved attack arc on screen), word boxes
        // come back in deskewed space, rotated around the image center. Rotate them back.
        double angle = (result.TextAngle ?? 0) * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double icx = bgra.Width / 2.0, icy = bgra.Height / 2.0;

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x1 = line.Words.Min(w => w.BoundingRect.X), y1 = line.Words.Min(w => w.BoundingRect.Y);
            double x2 = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            double y2 = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            double cx = (x1 + x2) / 2 - icx, cy = (y1 + y2) / 2 - icy;
            double rcx = icx + cx * cos - cy * sin, rcy = icy + cx * sin + cy * cos;
            double bw = x2 - x1, bh = y2 - y1;
            var box = new Box((px + (rcx - bw / 2) / up) / sx, (py + (rcy - bh / 2) / up) / sy, bw / up / sx, bh / up / sy);
            lines.Add(new OcrLine(line.Text, Normalize(line.Text), box));
        }
        textAngle = result.TextAngle ?? 0;
        LastTextAngle = textAngle;
        return lines;
    }

    /// <summary>Skew angle reported by the last OCR pass (diagnostics).</summary>
    public double LastTextAngle { get; private set; }

    /// <summary>
    /// Whether OCR text contains a key. Keys of 5+ characters also match with one wrong character,
    /// because small game windows make OCR swap similar glyphs (seen at 800x450: "탐험" read as
    /// "탐혐", "캐릭터배치" as "개릭터배치"). Shorter keys must match exactly to avoid false hits.
    /// </summary>
    public static bool Matches(string norm, string key)
    {
        if (norm.Contains(key)) return true;
        if (key.Length < 5 || norm.Length < key.Length) return false;
        for (int start = 0; start + key.Length <= norm.Length; start++)
        {
            int diff = 0;
            for (int i = 0; i < key.Length && diff <= 1; i++)
                if (norm[start + i] != key[i]) diff++;
            if (diff <= 1) return true;
        }
        return false;
    }

    /// <summary>Strip whitespace so "전투 시작" and "전투시작" compare equal.</summary>
    public static string Normalize(string s) => Whitespace().Replace(s, "");

    /// <summary>Extra cleanup for matching effect descriptions: OCR reads "%" as "0/0" and "칸" as "간".</summary>
    public static string NormalizeLoose(string s)
    {
        s = Normalize(s).Replace("0/0", "%").Replace("o/o", "%");
        return DigitGan().Replace(s, "$1칸");
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"(\d)간")] private static partial Regex DigitGan();
}
