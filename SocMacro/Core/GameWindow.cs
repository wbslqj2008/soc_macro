using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SocMacro.Core;

/// <summary>
/// The game's main window. All macro coordinates are in base units (1274x717 client area)
/// and are scaled to the live client size, so any 16:9 resolution works.
/// </summary>
public sealed class GameWindow
{
    public const int BaseW = 1274;
    public const int BaseH = 717;
    public const string ProcessName = "SoC";

    public IntPtr Handle { get; private set; }

    public bool IsAlive => Handle != IntPtr.Zero && Native.IsWindow(Handle);

    public bool Find()
    {
        var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        Handle = proc?.MainWindowHandle ?? IntPtr.Zero;
        return Handle != IntPtr.Zero;
    }

    /// <summary>Client area in screen pixels.</summary>
    public (int X, int Y, int W, int H) ClientRect()
    {
        Native.GetClientRect(Handle, out var r);
        var p = new Native.POINT();
        Native.ClientToScreen(Handle, ref p);
        return (p.X, p.Y, r.Right, r.Bottom);
    }

    public (double Sx, double Sy) Scale()
    {
        var (_, _, w, h) = ClientRect();
        return (w / (double)BaseW, h / (double)BaseH);
    }

    public (int X, int Y) ToScreen(double bx, double by)
    {
        var (x, y, w, h) = ClientRect();
        return (x + (int)Math.Round(bx * w / BaseW), y + (int)Math.Round(by * h / BaseH));
    }

    public uint Dpi => Handle == IntPtr.Zero ? 96 : Native.GetDpiForWindow(Handle);

    /// <summary>Bring the game to the front without sending ALT (the game treats ALT as "back").</summary>
    public void EnsureForeground()
    {
        if (Native.IsIconic(Handle)) Native.ShowWindow(Handle, 9 /* SW_RESTORE */);
        var fg = Native.GetForegroundWindow();
        if (fg == Handle) return;

        uint self = Native.GetCurrentThreadId();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        if (fgThread != 0 && fgThread != self) Native.AttachThreadInput(self, fgThread, true);
        Native.BringWindowToTop(Handle);
        Native.SetForegroundWindow(Handle);
        if (fgThread != 0 && fgThread != self) Native.AttachThreadInput(self, fgThread, false);
        Thread.Sleep(250);
    }

    /// <summary>Resize so the client area is exactly w x h (windowed mode only).</summary>
    public void ResizeClient(int w, int h)
    {
        Native.GetWindowRect(Handle, out var wr);
        var r = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        uint style = (uint)Native.GetWindowLong(Handle, -16);
        uint exStyle = (uint)Native.GetWindowLong(Handle, -20);
        Native.AdjustWindowRectEx(ref r, style, false, exStyle);
        // SWP_NOZORDER | SWP_NOACTIVATE
        Native.SetWindowPos(Handle, IntPtr.Zero, wr.Left, wr.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0004 | 0x0010);
    }

    /// <summary>
    /// Capture the client area as BGR. PrintWindow works even when the game is covered by other windows;
    /// if it returns a blank image we fall back to copying from the screen.
    /// </summary>
    public Mat Capture()
    {
        var (sx, sy, w, h) = ClientRect();
        if (w <= 0 || h <= 0) throw new InvalidOperationException("게임 창이 최소화되어 있거나 크기가 0입니다.");

        var mat = CaptureWith(w, h, (memDc) => Native.PrintWindow(Handle, memDc, 1 /* CLIENTONLY */ | 2 /* RENDERFULLCONTENT */));
        if (IsBlank(mat))
        {
            mat.Dispose();
            mat = CaptureWith(w, h, (memDc) =>
            {
                var screenDc = Native.GetDC(IntPtr.Zero);
                try { return Native.BitBlt(memDc, 0, 0, w, h, screenDc, sx, sy, 0x00CC0020 | 0x40000000 /* SRCCOPY|CAPTUREBLT */); }
                finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
            });
        }
        return mat;
    }

    private static Mat CaptureWith(int w, int h, Func<IntPtr, bool> draw)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = Native.SelectObject(memDc, bmp);
        try
        {
            draw(memDc);
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            var bits = new byte[w * h * 4];
            Native.SelectObject(memDc, old);
            Native.GetDIBits(memDc, bmp, 0, (uint)h, bits, ref bi, 0);
            using var bgra = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(bits, 0, bgra.Data, bits.Length);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool IsBlank(Mat m)
    {
        Cv2.MinMaxLoc(m.Reshape(1), out double min, out double max);
        return max - min < 8;
    }
}
