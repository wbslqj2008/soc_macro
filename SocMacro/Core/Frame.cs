using System.Text.RegularExpressions;
using OpenCvSharp;

namespace SocMacro.Core;

/// <summary>One captured screen. OCR of the full frame is computed lazily and cached.</summary>
public sealed class Frame(Mat image, double sx, double sy, OcrService ocr) : IDisposable
{
    private List<OcrLine>? lines;
    private Mat? baseImage;

    public Mat Image { get; } = image;
    public double Sx { get; } = sx;
    public double Sy { get; } = sy;

    /// <summary>The frame resized to the 1274x717 base, for pixel rules tuned at that size.</summary>
    public Mat BaseImage
    {
        get
        {
            if (baseImage != null) return baseImage;
            if (Image.Width == GameWindow.BaseW && Image.Height == GameWindow.BaseH) return baseImage = Image;
            baseImage = new Mat();
            Cv2.Resize(Image, baseImage, new Size(GameWindow.BaseW, GameWindow.BaseH), 0, 0, InterpolationFlags.Area);
            return baseImage;
        }
    }

    public IReadOnlyList<OcrLine> Lines => lines ??= ocr.Read(Image, Sx, Sy);

    /// <summary>
    /// Re-read the whole frame in bands and use that from now on (second chance for an unrecognized
    /// screen). The full-frame result is kept too, so nothing it found is lost.
    /// </summary>
    public void RereadBanded()
    {
        var banded = ocr.ReadBanded(Image, Sx, Sy);
        var first = Lines;
        lines = [.. first, .. banded.Where(b => !first.Any(l => l.Norm == b.Norm))];
    }

    public string AllText => string.Join(" / ", Lines.Select(l => l.Text));

    public bool Has(string key) => Lines.Any(l => OcrService.Matches(l.Norm, key));

    public bool HasAny(params string[] keys) => keys.Any(Has);

    public OcrLine? Find(string key, Box? within = null) =>
        Lines.FirstOrDefault(l => OcrService.Matches(l.Norm, key) && (within is null || within.Value.ContainsCenterOf(l.Box)));

    public (OcrLine Line, Match Match)? Match(Regex re, Box? within = null)
    {
        foreach (var l in Lines)
        {
            if (within is not null && !within.Value.ContainsCenterOf(l.Box)) continue;
            var m = re.Match(l.Norm);
            if (m.Success) return (l, m);
        }
        return null;
    }

    /// <summary>
    /// Whether a sub-region contains the text. Uses the full-frame OCR first and falls back to a
    /// dedicated pass over the region, since full-frame OCR often misses small buttons on busy screens.
    /// </summary>
    public OcrLine? FindIn(string key, Box region) =>
        Find(key, region) ?? ReadRegion(region).FirstOrDefault(l => OcrService.Matches(l.Norm, key));

    /// <summary>Separate, higher-detail OCR of a sub-region.</summary>
    public List<OcrLine> ReadRegion(Box roi) => ocr.Read(Image, Sx, Sy, roi);

    /// <summary>Average BGR of a 3x3 patch around a base-coordinate point.</summary>
    public Vec3b Pixel(int bx, int by)
    {
        var img = BaseImage;
        int b = 0, g = 0, r = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var p = img.At<Vec3b>(Math.Clamp(by + dy, 0, img.Height - 1), Math.Clamp(bx + dx, 0, img.Width - 1));
                b += p.Item0; g += p.Item1; r += p.Item2;
            }
        return new Vec3b((byte)(b / 9), (byte)(g / 9), (byte)(r / 9));
    }

    public void Dispose()
    {
        if (baseImage != null && !ReferenceEquals(baseImage, Image)) baseImage.Dispose();
        Image.Dispose();
    }
}
