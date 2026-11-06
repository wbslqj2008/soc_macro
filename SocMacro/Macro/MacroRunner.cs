using System.Diagnostics;
using SocMacro.Settings;

namespace SocMacro.Macro;

public enum RunMode { Journey, Daily }

/// <summary>Top-level flow: make sure the game is running and sized, then run the chosen mode.</summary>
/// <param name="allowDaily">false for basic license keys: 탐험의 여정 only.</param>
public sealed class MacroRunner(MacroContext ctx, bool allowDaily)
{
    private const string SteamUri = "steam://rungameid/2526380";
    private const int MaxRelaunches = 3;

    public event Action<int>? RoundCompleted;


    public void Run(RunMode mode)
    {
        var journey = new JourneyBot(ctx);
        journey.RoundCompleted += r => RoundCompleted?.Invoke(r);
        var daily = new DailyBot(ctx);


        for (int relaunch = 0; ; relaunch++)
        {
            EnsureGame();
            ApplyResolution();
            ctx.RequestBlock(true);
            try
            {
                if (mode == RunMode.Daily)
                {
                    if (!allowDaily) throw new MacroStopException("일일퀘스트는 프로 키에서만 사용할 수 있습니다.");
                    // after a relaunch this just re-checks; finished contents show 0 remaining
                    daily.Run();
                    return;
                }

                if (!ctx.Settings.JourneyEnabled)
                {
                    ctx.Log.Info("탐험의 여정이 꺼져 있습니다. 탐험의 여정 페이지에서 켜 주세요.");
                    return;
                }
                while (true)
                {
                    journey.Run();
                    if (!journey.DailyDue) return;
                    if (!allowDaily)
                    {
                        // basic key: midnight doesn't switch to dailies, just keep looping the event
                        journey.DailyDue = false;
                        continue;
                    }
                    // midnight passed: finish the dailies, then keep looping the event
                    journey.DailyDue = false;
                    daily.Run();
                }
            }
            catch (GameClosedException)
            {
                ctx.RequestBlock(false);
                if (!ctx.Settings.AutoLaunch || relaunch >= MaxRelaunches) throw;
                ctx.Log.Info($"게임이 종료되었습니다. 다시 실행합니다. ({relaunch + 1}/{MaxRelaunches})");
                ctx.Sleep(5000);
            }
        }
    }

    private void EnsureGame()
    {
        if (ctx.Game.Find()) return;
        if (!ctx.Settings.AutoLaunch) throw new MacroStopException("게임이 실행 중이 아닙니다. 게임을 켜거나 '게임 자동 실행'을 켜 주세요.");

        Launch();
        var until = DateTime.Now.AddMinutes(3);
        while (!ctx.Game.Find())
        {
            if (DateTime.Now > until) throw new MacroStopException("3분 안에 게임 창이 나타나지 않았습니다.");
            ctx.Sleep(2000);
        }
        ctx.Log.Info("게임 창을 찾았습니다. 로딩을 기다립니다.");
        ctx.Sleep(15000);
    }

    private void Launch()
    {
        if (ctx.Settings.Launch == LaunchMode.Steam)
        {
            ctx.Log.Info("Steam으로 게임을 실행합니다.");
            Process.Start(new ProcessStartInfo(SteamUri) { UseShellExecute = true });
            return;
        }

        var path = ctx.Settings.OfficialPath;
        if (!File.Exists(path)) throw new MacroStopException($"공식 런처 실행 파일을 찾을 수 없습니다: {path}");
        ctx.Log.Info("공식 런처로 게임을 실행합니다.");
        // XD.exe fails with "XD.exe를 찾을 수 없습니다" unless started from its own folder
        Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true });
    }

    private void ApplyResolution()
    {
        var s = ctx.Settings;
        if (s.Display == DisplayMode.Windowed && TryParseSize(s.Resolution, out int w, out int h))
        {
            var (_, _, cw, ch) = ctx.Game.ClientRect();
            if (cw != w || ch != h)
            {
                ctx.Game.ResizeClient(w, h);
                ctx.Sleep(1500);
            }
        }

        var (_, _, fw, fh) = ctx.Game.ClientRect();
        double ratio = fw / (double)fh;
        ctx.Log.Info($"게임 화면 {fw}x{fh}, 배율 {ctx.Game.Dpi * 100 / 96}%");
        if (Math.Abs(ratio - 16.0 / 9) > 0.03)
            ctx.Log.Info("주의: 16:9가 아닌 화면 비율은 버튼 위치가 어긋날 수 있습니다.");
        // below ~1200 px wide the game's text gets too small for reliable OCR (seen at 800x450)
        if (fw < 1200)
            ctx.Alert($"게임 화면이 작습니다 ({fw}x{fh}). 글자 인식이 불안정할 수 있으니 1280x720 이상을 권장합니다. " +
                      "설정 > 게임 해상도에서 1280x720 이상을 고르면 시작할 때 창 크기를 맞춰 줍니다.");
    }

    public static bool TryParseSize(string text, out int w, out int h)
    {
        w = h = 0;
        var parts = text.Split('x', 'X', '×');
        return parts.Length == 2 && int.TryParse(parts[0].Trim(), out w) && int.TryParse(parts[1].Trim(), out h);
    }
}
