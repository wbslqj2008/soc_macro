using SocMacro.Core;
using SocMacro.Settings;

namespace SocMacro.Macro;

/// <summary>Everything a macro routine needs, plus cancellable helpers.</summary>
public sealed class MacroContext(GameWindow game, OcrService ocr, Vision vision, AppSettings settings, Log log, CancellationToken token)
{
    public GameWindow Game { get; } = game;
    public InputDriver Input { get; } = new(game);
    public OcrService Ocr { get; } = ocr;
    public Vision Vision { get; } = vision;
    public AppSettings Settings { get; } = settings;
    public Log Log { get; } = log;
    public CancellationToken Token { get; } = token;

    /// <summary>Raised when the macro wants physical input blocked (true) or released (false).</summary>
    public event Action<bool>? BlockRequested;

    /// <summary>Short description of what the macro is doing now, for the dashboard.</summary>
    public event Action<string>? StatusChanged;

    public event Action<int>? StaminaRead;

    public void RequestBlock(bool block) => BlockRequested?.Invoke(block);

    public void ReportStatus(string status) => StatusChanged?.Invoke(status);

    public void ReportStamina(int value) => StaminaRead?.Invoke(value);

    /// <summary>Something the user should act on (e.g. not enough stamina); shown prominently in the UI.</summary>
    public event Action<string>? AlertRaised;

    public void Alert(string message)
    {
        Log.Info($"[알림] {message}");
        AlertRaised?.Invoke(message);
    }

    public Frame Capture()
    {
        Token.ThrowIfCancellationRequested();
        if (!Game.IsAlive) throw new GameClosedException();
        var (sx, sy) = Game.Scale();
        return new Frame(Game.Capture(), sx, sy, Ocr);
    }

    public void Click(double bx, double by, int afterMs = 900)
    {
        Token.ThrowIfCancellationRequested();
        Input.Click(bx, by);
        Sleep(afterMs);
    }

    public void Click(Box box, int afterMs = 900) => Click(box.CX, box.CY, afterMs);

    public void Sleep(int ms)
    {
        if (Token.WaitHandle.WaitOne(ms)) Token.ThrowIfCancellationRequested();
    }
}

public sealed class GameClosedException() : Exception("게임 창이 사라졌습니다.");

public sealed class MacroStopException(string reason) : Exception(reason);
