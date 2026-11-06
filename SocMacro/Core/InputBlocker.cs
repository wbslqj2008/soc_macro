using System.Runtime.InteropServices;

namespace SocMacro.Core;

/// <summary>
/// Swallows physical mouse/keyboard input while the macro drives the game.
/// Injected input (ours) passes through, and so do the hotkeys so the user can always stop.
/// Must be enabled/disabled on a thread with a message loop (the WPF UI thread).
/// </summary>
public sealed class InputBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x01, LLKHF_INJECTED = 0x10;

    private readonly Native.HookProc mouseProc;
    private readonly Native.HookProc keyProc;
    private readonly HashSet<uint> passKeys;
    private IntPtr mouseHook, keyHook;

    public InputBlocker(params uint[] passThroughKeys)
    {
        passKeys = [.. passThroughKeys];
        mouseProc = OnMouse;
        keyProc = OnKey;
    }

    public bool Enabled => mouseHook != IntPtr.Zero;

    public void Enable()
    {
        if (Enabled) return;
        var mod = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, mod, 0);
    }

    public void Disable()
    {
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        mouseHook = keyHook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0) return 1;
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0 && !passKeys.Contains(info.vkCode)) return 1;
        }
        return Native.CallNextHookEx(keyHook, code, wParam, lParam);
    }

    public void Dispose() => Disable();
}
