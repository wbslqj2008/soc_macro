using System.Text.RegularExpressions;
using SocMacro.Core;

namespace SocMacro.Macro;

/// <summary>
/// 알림판 → 글로리 전장: challenge the chosen difficulty column until the configured count,
/// the game's "도전 횟수" counter, or the 5-per-day limit runs out.
/// </summary>
public sealed partial class GloryBot(MacroContext ctx)
{
    public const int DailyLimit = 5;

    // starts at the left edge: OCR often merges the title with the back button ("@이벤트"), moving its center left
    private static readonly Box TitleArea = new(0, 5, 560, 60);
    private static readonly Box CounterArea = new(880, 15, 190, 45);
    private static readonly Box BoardTitleArea = new(0, 0, 560, 70);
    private static readonly Box LobbyEventButton = new(840, 115, 120, 45);
    private static readonly (double X, double Y) BoardButton = (1195, 495);
    private static readonly (double X, double Y) GloryCard = (250, 330);
    private static readonly double[] FightX = [543, 823, 1103];
    private const double FightY = 500;
    private static readonly string[] DifficultyNames = ["쉬움", "일반", "어려움"];

    private int doneToday;
    private DateTime doneDay;

    public void Run()
    {
        if (doneDay != DateTime.Today)
        {
            doneToday = 0;
            doneDay = DateTime.Today;
        }
        int diff = Math.Clamp(ctx.Settings.GloryDifficulty, 0, 2);
        int target = Math.Min(Math.Clamp(ctx.Settings.GloryCount, 1, DailyLimit), DailyLimit - doneToday);
        if (target <= 0)
        {
            ctx.Log.Info("글로리 전장: 오늘 5회를 모두 진행했습니다.");
            return;
        }

        ctx.ReportStatus("글로리 전장");
        GoToGlory();
        int fought = 0;
        while (fought < target)
        {
            int? left;
            using (var f = ctx.Capture()) left = ReadCounter(f);
            if (left == 0)
            {
                ctx.Log.Info("글로리 전장: 남은 도전 횟수가 없습니다.");
                break;
            }
            ctx.Log.Info($"글로리 전장: {DifficultyNames[diff]} 도전 ({fought + 1}/{target}, 남은 도전 횟수 {left?.ToString() ?? "?"})");
            ctx.Click(FightX[diff], FightY, 3000);
            if (!Fight()) break;
            fought++;
            doneToday++;
        }
        ctx.Log.Info($"글로리 전장 종료 ({fought}회 진행)");
    }

    /// <summary>One challenge, from the deploy screen to back on the 글로리 전장 page.</summary>
    private bool Fight()
    {
        // progress is driven by screen recognition; this is only a safety net for a stuck screen
        var until = DateTime.Now.AddMinutes(30);
        bool started = false;
        while (DateTime.Now < until)
        {
            using var f = ctx.Capture();
            if (IsGloryPage(f))
            {
                if (started) return true;
                ctx.Sleep(1500); // the deploy screen is still loading
                continue;
            }
            // check "실패" before "종료": the defeat screen also has a "전투 종료" button
            if (f.Has("전투패배")) { ctx.Click(472, 488, 2500); continue; }
            if (ctx.Vision.IsDefeatScreen(f)) { ctx.Click(806, 619, 3000); started = true; continue; }
            // "티어 상승" (and other rank popups) sit on top of the result screen; same arrow dismisses them
            if (f.HasAny("전투종료", "전투승리", "티어상승", "티어하락"))
            {
                ctx.Click(1212, 640, 3000);
                started = true;
                continue;
            }
            if (f.Find("캐릭터배치", TitleArea) != null || (f.Has("출전") && f.HasAny("전술", "전투력")))
            {
                ctx.Click(1183, 560, 5000); // 시작
                started = true;
                continue;
            }
            if (ctx.Vision.BattleHud(f) is (true, var auto))
            {
                started = true;
                if (!auto) ctx.Click(143, 36, 2000);
                else ctx.Sleep(5000);
                continue;
            }
            ctx.Sleep(2000);
        }
        ctx.Log.Info("글로리 전장: 30분 동안 화면이 진행되지 않아 중단합니다.");
        return false;
    }

    private void GoToGlory()
    {
        for (int i = 0; i < 15; i++)
        {
            using var f = ctx.Capture();
            if (IsGloryPage(f)) return;
            // this page has no breadcrumb button, so its title sits further left; the lobby also shows
            // an "알림판" label (bottom right), hence the region
            if (f.Find("알림판", BoardTitleArea) != null)
            {
                ctx.Click(GloryCard.X, GloryCard.Y, 3000);
                continue;
            }
            if (f.FindIn("이벤트", LobbyEventButton) != null)
            {
                ctx.Click(BoardButton.X, BoardButton.Y, 3000);
                continue;
            }
            if (f.HasAny("획득정보", "시련완료", "전투종료", "티어상승", "티어하락")) { ctx.Click(1212, 640, 2000); continue; }
            ctx.Click(73, 33, 2500); // back; never pressed on the lobby (it opens the profile there)
        }
        throw new DailySkipException("글로리 전장으로 이동하지 못했습니다");
    }

    private static bool IsGloryPage(Frame f) =>
        f.Find("글로리전", TitleArea) != null && f.HasAny("도전횟수", "도전기록", "새로고침");

    private static int? ReadCounter(Frame f)
    {
        // the label and the number come back as separate lines
        var m = Number().Match(string.Concat(f.ReadRegion(CounterArea).Select(l => l.Norm)));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"도전횟수(\d+)")] private static partial Regex Number();
}
