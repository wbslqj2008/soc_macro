using OpenCvSharp;

namespace SocMacro.Core;

/// <summary>Writes to the UI and to logs/yyyy-MM-dd.log; can save screenshots for debugging.</summary>
public sealed class Log
{
    private readonly string dir;
    private readonly object gate = new();

    public event Action<string>? Line;

    public Log(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(Path.Combine(dir, "shots"));
    }

    public void Info(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (gate)
        {
            File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
        Line?.Invoke(line);
    }

    public string SaveShot(Mat image, string tag)
    {
        var path = Path.Combine(dir, "shots", $"{DateTime.Now:yyyyMMdd_HHmmss}_{tag}.png");
        image.SaveImage(path);
        return path;
    }
}
