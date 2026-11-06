namespace SocMacro.Core;

/// <summary>Mouse/keyboard injection in base coordinates.</summary>
public sealed class InputDriver(GameWindow game)
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    public void Click(double bx, double by)
    {
        game.EnsureForeground();
        var (x, y) = game.ToScreen(bx, by);
        Native.SetCursorPos(x, y);
        Thread.Sleep(80);
        Native.mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        Native.mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Press-and-hold drag. The game only picks a unit up after a long press (~0.8s);
    /// shorter holds are ignored.
    /// </summary>
    public void Drag(double bx1, double by1, double bx2, double by2, int holdMs = 800, int steps = 30)
    {
        game.EnsureForeground();
        var (sx, sy) = game.ToScreen(bx1, by1);
        var (ex, ey) = game.ToScreen(bx2, by2);
        Native.SetCursorPos(sx, sy);
        Thread.Sleep(150);
        Native.mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(holdMs);
        for (int i = 1; i <= steps; i++)
        {
            Native.SetCursorPos(sx + (ex - sx) * i / steps, sy + (ey - sy) * i / steps);
            Thread.Sleep(30);
        }
        Thread.Sleep(300);
        Native.mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }
}
