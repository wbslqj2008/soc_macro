using System.Text.RegularExpressions;
using SocMacro.Core;

namespace SocMacro.Macro;

/// <summary>
/// Daily contents in 출항 → 엇갈린 세계 → 일일 이벤트. Each checked content is swept (or, for
/// 기억 회복, fought) once per run. Button positions are base coordinates on the 1274x717 client.
/// </summary>
public sealed partial class DailyBot(MacroContext ctx)
{
    // starts at the left edge: OCR often merges the title with the back button ("@이벤트"), moving its center left
    private static readonly Box TitleArea = new(0, 5, 560, 60);
    private static readonly Box LobbyEventButton = new(840, 115, 120, 45);
    private static readonly Box CardLabelBand = new(0, 500, GameWindow.BaseW, 120);
    private static readonly Box CardTopBand = new(0, 130, GameWindow.BaseW, 90);
    private static readonly Box PanelSweep = new(830, 600, 190, 55);
    private static readonly Box PanelSweepCost = new(900, 605, 100, 45);
    // 기억 회복 has a single, centered 출격 button; elsewhere it sits right of 소탕
    private static readonly Box PanelSortie = new(830, 600, 420, 55);
    private static readonly Box SweepCount = new(580, 300, 140, 46);
    private static readonly Box StageNumberRow = new(0, 425, GameWindow.BaseW, 45);
    private static readonly Box PageCounter = new(820, 15, 260, 45);

    private static readonly (double X, double Y) Back = (73, 33);
    private static readonly (double X, double Y) DailyTab = (750, 57);
    private static readonly (double X, double Y) SailButton = (1060, 655);
    private static readonly (double X, double Y) SweepButton = (920, 627);
    private static readonly (double X, double Y) CountRight = (728, 323);
    private static readonly (double X, double Y) UseCanButton = (713, 397);
    private static readonly (double X, double Y) ContinuousButton = (713, 483);
    private static readonly (double X, double Y) ContinuousConfirm = (985, 650);
    private static readonly (double X, double Y) ResultArrow = (1212, 640);

    private readonly HashSet<string> completed = [];
    private DateTime completedDay;

    public void Run()
    {
        var day = DateTime.Now.DayOfWeek;
        if (completedDay != DateTime.Today)
        {
            completed.Clear();
            completedDay = DateTime.Today;
        }
        // contents already done this day (e.g. before a game crash) aren't swept again
        var todo = DailyContent.All
            .Where(c => ctx.Settings.DailyContents.GetValueOrDefault(c.Name) && !completed.Contains(c.Name)).ToList();
        bool glory = ctx.Settings.GloryEnabled;
        if (todo.Count == 0 && !glory)
        {
            ctx.Log.Info("체크된 일일퀘스트가 없습니다.");
            return;
        }
        if (todo.Count == 0)
        {
            RunGlory();
            GoToLobby();
            return;
        }
        ctx.Log.Info($"일일퀘스트 시작: {string.Join(", ", todo.Select(c => c.Name))} ({DayName(day)}요일)");

        int done = 0;
        foreach (var content in todo)
        {
            ctx.ReportStatus($"일일퀘스트: {content.Name}");
            try
            {
                if (RunContent(content, day)) done++;
                completed.Add(content.Name);
            }
            catch (DailySkipException ex)
            {
                ctx.Log.Info($"{content.Name}: {ex.Message} → 다음 컨텐츠로 넘어갑니다.");
            }
        }
        ctx.Log.Info($"일일퀘스트 종료 ({done}/{todo.Count}개 완료)");
        if (glory)
        {
            GoToLobby();
            RunGlory();
        }
        GoToLobby();
    }

    private readonly GloryBot gloryBot = new(ctx);

    private void RunGlory()
    {
        try
        {
            gloryBot.Run();
        }
        catch (DailySkipException ex)
        {
            ctx.Log.Info($"글로리 전장: {ex.Message}");
        }
    }

    private bool RunContent(DailyContent c, DayOfWeek day)
    {
        int sub = 0;
        if (c.HasSub)
        {
            sub = ctx.Settings.DailyPick(c, day);
            if (sub == 0)
            {
                ctx.Log.Info($"{c.Name}: {DayName(day)}요일은 건너뛰도록 설정되어 있습니다.");
                return false;
            }
        }

        GoToList();
        var card = FindCard(c);
        if (RemainingOnCard(card) is 0)
        {
            ctx.Log.Info($"{c.Name}: 오늘 남은 횟수가 없습니다.");
            return false;
        }
        ctx.Click(card.Box, 3000);

        // the content page shows a clean "남은 횟수 0/1" top right (the card's own text is over artwork)
        using (var f = ctx.Capture())
        {
            var m = f.ReadRegion(PageCounter).Select(l => RemainingPage().Match(l.Norm)).FirstOrDefault(x => x.Success);
            if (m != null && m.Groups[1].Value == "0")
            {
                ctx.Log.Info($"{c.Name}: 오늘 남은 횟수가 없습니다.");
                return false;
            }
        }

        if (c.HasSub) OpenSub(c, sub);

        return c.Kind == DailyKind.Battle ? Fight(c) : Sweep(c);
    }

    // ───────────────────────── navigation ─────────────────────────

    /// <summary>Get to 엇갈린 세계 with the 일일 이벤트 tab, from anywhere.</summary>
    private void GoToList()
    {
        for (int i = 0; i < 20; i++)
        {
            using var f = ctx.Capture();
            if (f.Find("엇갈린세계", TitleArea) != null)
            {
                if (f.Has("일일이벤트")) ctx.Click(DailyTab.X, DailyTab.Y, 1500);
                return;
            }
            if (DismissOverlay(f)) continue;
            // "출항" is drawn in a decorative font OCR can't read; the lobby is recognized by its 이벤트 button
            if (IsLobby(f))
            {
                ctx.Click(SailButton.X, SailButton.Y, 3000);
                continue;
            }
            // 출항 selection: OCR of its stylized card titles is unreliable (often nothing at all),
            // so the 엇갈린 세계 card is found by template
            if (ctx.Vision.MatchAll(f, "sail_crossed_world", 0.8, max: 1) is [var (card, _)])
            {
                ctx.Click(card.CX, card.CY - 150, 3000);
                continue;
            }
            ctx.Click(Back.X, Back.Y, 2500);
        }
        throw new MacroStopException("엇갈린 세계 화면으로 이동하지 못했습니다. logs/shots 폴더의 스크린샷을 확인해 주세요.");
    }

    private void GoToLobby()
    {
        for (int i = 0; i < 12; i++)
        {
            using var f = ctx.Capture();
            if (IsLobby(f)) return;
            if (DismissOverlay(f)) continue;
            ctx.Click(Back.X, Back.Y, 2500);
        }
    }

    private static bool IsLobby(Frame f) => f.FindIn("이벤트", LobbyEventButton) != null;

    /// <summary>Close result screens and popups that may be left over. Returns true if it clicked.</summary>
    private bool DismissOverlay(Frame f)
    {
        if (f.Has("획득정보") || f.Has("시련완료")) { ctx.Click(ResultArrow.X, ResultArrow.Y, 2000); return true; }
        if (f.Has("전투승리")) { ctx.Click(ResultArrow.X, ResultArrow.Y, 3000); return true; }
        if (f.HasAny("게임공지", "이벤트공지")) { ctx.Click(1105, 97, 1500); return true; }
        if (f.Find("게임시작", new Box(400, 500, 480, 120)) is { } start) { ctx.Click(start.Box, 10000); return true; }
        return false;
    }

    /// <summary>Find the content's card on the carousel, scrolling toward it if it's off-screen.</summary>
    private OcrLine FindCard(DailyContent c)
    {
        for (int i = 0; i < 6; i++)
        {
            using var f = ctx.Capture();
            var labels = f.ReadRegion(CardLabelBand);
            var hit = labels.FirstOrDefault(l => l.Norm.Contains(c.Norm));
            // a card cut off at the screen edge can't be clicked reliably
            if (hit != null && hit.Box.CX > 90 && hit.Box.CX < GameWindow.BaseW - 90)
            {
                lastCardTop = f.ReadRegion(CardTopBand);
                return hit;
            }

            var visible = labels
                .Select(l => DailyContent.All.FirstOrDefault(d => l.Norm.Contains(d.Norm)))
                .Where(d => d != null).Select(d => d!.Order).ToList();
            bool toLeft = hit != null ? hit.Box.CX <= 90 : visible.Count > 0 && visible.Min() > c.Order;
            // dragging right reveals cards on the left
            if (toLeft) ctx.Input.Drag(300, 640, 900, 640, holdMs: 100);
            else ctx.Input.Drag(900, 640, 300, 640, holdMs: 100);
            ctx.Sleep(1500);
        }
        throw new DailySkipException("목록에서 카드를 찾지 못했습니다");
    }

    private List<OcrLine> lastCardTop = [];
    private Box sortie;

    /// <summary>"남은 횟수 N" above the card, if the card shows one.</summary>
    private int? RemainingOnCard(OcrLine card)
    {
        foreach (var l in lastCardTop)
        {
            if (Math.Abs(l.Box.CX - card.Box.CX) > 110) continue;
            var m = Remaining().Match(l.Norm);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    private void OpenSub(DailyContent c, int sub)
    {
        string label = c.SubLabels[sub - 1];
        string display = c.SubNames[sub - 1];
        for (int i = 0; i < 3; i++)
        {
            using var f = ctx.Capture();
            // Exact match only: "타로의자취1" is one character away from "타로의자취2", and picking the
            // wrong card would sweep the wrong stage. Sub labels sit low on their cards (keeps the title out).
            var hit = f.Lines.FirstOrDefault(l => l.Norm.Contains(label) && l.Box.CY > 300);
            (double X, double Y)? at = hit != null ? (hit.Box.CX, hit.Box.CY) : null;

            // Last try without the label: the cards have a fixed layout, so if this is clearly the
            // content's own page (its title), use the label's known position instead of giving up.
            if (at == null && i == 2 && f.Find(c.Norm, TitleArea) != null)
            {
                at = SubLabelPosition(c.SubLabels.Length, sub);
                ctx.Log.Info($"{c.Name}: {display} 글자를 읽지 못해 위치로 선택합니다.");
            }
            if (at is not { } p)
            {
                ctx.Sleep(1500);
                continue;
            }

            // Locked cards show "화요일/금요일 오픈" just above their label. It wraps and is often misread
            // ("화요일/금요일오", "화요월주호말/토"), and full-frame OCR sometimes merges it into the card
            // art, so that strip is read on its own. Open cards have no text there.
            var lockText = f.ReadRegion(new Box(p.X - 130, p.Y - 120, 260, 60))
                .FirstOrDefault(l => l.Norm.Contains('요') || l.Norm.Contains("오픈"));
            if (lockText != null)
            {
                ctx.Alert($"{c.Name} {display}이(가) 오늘({DayName(DateTime.Now.DayOfWeek)}요일) 잠겨 있어 건너뛰었습니다. 요일 설정을 확인해 주세요.");
                throw new DailySkipException($"{display} 잠김");
            }
            ctx.Log.Info($"{c.Name}: {display} 선택");
            ctx.Click(p.X, p.Y - 150, 3000); // the card art, above the label
            return;
        }
        throw new DailySkipException($"{display} 카드를 찾지 못했습니다");
    }

    /// <summary>Label center of the n-th sub card (base coords), measured on the 5-card and 3-card pages.</summary>
    private static (double X, double Y) SubLabelPosition(int cards, int sub) => cards switch
    {
        5 => (new[] { 160.0, 399, 637, 876, 1115 }[sub - 1], 491),
        _ => (new[] { 335.0, 635, 935 }[sub - 1], 544),
    };

    // ───────────────────────── sweep ─────────────────────────

    private bool Sweep(DailyContent c)
    {
        int target = c.CountConfigurable ? ctx.Settings.DailyCount(c.Name) : 1;

        // the stage map opens with the last cleared stage selected and its panel shown
        int cost, stamina;
        using (var f = WaitFor(fr => fr.Find("소탕", PanelSweep) != null, 6))
        {
            if (f == null) throw new DailySkipException("소탕 버튼을 찾지 못했습니다 (클리어한 스테이지가 없을 수 있음)");
            cost = ReadNumber(f, PanelSweepCost) ?? 0;
            stamina = StaminaManager.Read(f) ?? -1;
            if (stamina >= 0) ctx.ReportStamina(stamina);
        }

        if (cost > 0 && stamina >= 0)
        {
            int affordable = stamina / cost;
            if (affordable == 0)
            {
                ctx.Alert($"{c.Name}: 행동력이 부족합니다 (필요 {cost}, 현재 {stamina}).");
                throw new DailySkipException("행동력 부족");
            }
            if (affordable < target)
            {
                ctx.Alert($"{c.Name}: 행동력이 부족해 {target}회 중 {affordable}회만 소탕합니다 (회당 {cost}, 현재 {stamina}).");
                target = affordable;
            }
        }

        ctx.Click(SweepButton.X, SweepButton.Y, 2000);
        using (var f = WaitFor(IsSweepPopup, 5))
        {
            if (f == null) throw new DailySkipException("소탕 창이 열리지 않았습니다");
            if (f.HasAny("소진", "부족")) throw new DailySkipException("오늘 소탕할 수 있는 횟수가 없습니다");
        }

        int count = SetSweepCount(target);
        if (ctx.Settings.UseOctopusCan && TryCan(count, c)) return true;
        return Continuous(count, c);
    }

    /// <summary>Press "▶" on the "n/max" selector until n reaches the target. Returns the selected n.</summary>
    private int SetSweepCount(int target)
    {
        for (int i = 0; i < 12; i++)
        {
            using var f = ctx.Capture();
            var m = f.ReadRegion(SweepCount).Select(l => Fraction().Match(l.Norm)).FirstOrDefault(x => x.Success);
            if (m == null) return 1;
            int n = int.Parse(m.Groups[1].Value), max = int.Parse(m.Groups[2].Value);
            if (n >= target || n >= max) return n;
            ctx.Click(CountRight.X, CountRight.Y, 600);
        }
        return target;
    }

    /// <summary>
    /// The sweep popup. Full-frame OCR tends to miss its "계속 소탕" button, so it is recognized by
    /// the header text instead.
    /// </summary>
    private static bool IsSweepPopup(Frame f) => f.HasAny("소탕횟수선택", "즉시완료");

    private bool TryCan(int count, DailyContent c)
    {
        ctx.Click(UseCanButton.X, UseCanButton.Y, 2500);
        using var f = WaitFor(fr => fr.Has("획득정보") || IsSweepPopup(fr), 5);
        if (f != null && f.Has("획득정보"))
        {
            ctx.Log.Info($"{c.Name}: 문어 통조림으로 {count}회 소탕 완료");
            ctx.Click(ResultArrow.X, ResultArrow.Y, 2000);
            return true;
        }
        ctx.Log.Info($"{c.Name}: 문어 통조림이 부족해 계속 소탕으로 진행합니다.");
        return false;
    }

    private bool Continuous(int count, DailyContent c)
    {
        using (var f = ctx.Capture())
        {
            // the sweep popup may have closed after a failed can attempt
            if (!IsSweepPopup(f))
            {
                ctx.Click(SweepButton.X, SweepButton.Y, 2000);
                SetSweepCount(count);
            }
        }
        ctx.Click(ContinuousButton.X, ContinuousButton.Y, 2500);

        // character selection fills itself ("자동"); just confirm
        using (var f = WaitFor(fr => fr.Has("캐릭터선택") || fr.Has("시련완료") || fr.Has("획득정보"), 6))
        {
            if (f == null) throw new DailySkipException("계속 소탕 화면이 열리지 않았습니다");
            if (f.Has("캐릭터선택")) ctx.Click(ContinuousConfirm.X, ContinuousConfirm.Y, 3000);
        }
        using (var f = WaitFor(fr => fr.Has("시련완료") || fr.Has("획득정보") || fr.Has("소탕보상"), 10))
        {
            if (f == null) throw new DailySkipException("계속 소탕 결과를 확인하지 못했습니다");
        }
        ctx.Log.Info($"{c.Name}: 계속 소탕 {count}회 완료");
        ctx.Click(ResultArrow.X, ResultArrow.Y, 2000);
        return true;
    }

    // ───────────────────────── battle (기억 회복) ─────────────────────────

    private bool Fight(DailyContent c)
    {
        if (!OpenLastStagePanel(c)) throw new DailySkipException("스테이지 패널을 열지 못했습니다");

        int losses = 0;
        bool reshuffle = false;
        var until = DateTime.Now.AddMinutes(25);
        ctx.Click(sortie, 4000);

        while (DateTime.Now < until)
        {
            using var f = ctx.Capture();
            if (f.Has("전투패배")) { ctx.Click(472, 488, 2500); continue; }
            if (ctx.Vision.IsDefeatScreen(f))
            {
                losses++;
                ctx.Click(806, 619, 3000); // 전투 종료
                if (losses >= Math.Max(1, ctx.Settings.MaxLossesPerNode))
                    throw new DailySkipException($"{losses}번 패배");
                ctx.Log.Info($"{c.Name}: 패배 ({losses}회) → 배치를 바꿔 재도전합니다.");
                reshuffle = true;
                continue;
            }
            if (f.Has("전투승리"))
            {
                ctx.Log.Info($"{c.Name}: 전투 승리");
                ctx.Click(ResultArrow.X, ResultArrow.Y, 3000);
                DrainResults();
                return true;
            }
            if (f.Find("캐릭터배치", TitleArea) != null || (f.Has("출전") && f.HasAny("전술", "전투력")))
            {
                if (reshuffle) Formation.Reshuffle(ctx, f);
                reshuffle = false;
                ctx.Click(1183, 560, 5000); // 시작
                continue;
            }
            if (ctx.Vision.BattleHud(f) is (true, var auto))
            {
                if (!auto) ctx.Click(143, 36, 2000);
                else ctx.Sleep(4000);
                continue;
            }
            // back on the stage map after a defeat: sortie again
            if (f.FindIn("출격", PanelSortie) is { } again)
            {
                ctx.Click(again.Box, 4000);
                continue;
            }
            ctx.Sleep(2000);
        }
        throw new DailySkipException("전투가 25분 안에 끝나지 않았습니다");
    }

    /// <summary>
    /// The panel normally opens on the last stage. If not, scroll the stage row to its end and
    /// open the highest-numbered stage that offers 출격.
    /// </summary>
    private bool OpenLastStagePanel(DailyContent c)
    {
        using (var f = WaitFor(fr => fr.FindIn("출격", PanelSortie) != null, 4))
        {
            if (f != null)
            {
                if (f.Has("소진")) throw new DailySkipException("오늘의 도전 횟수가 소진되었습니다");
                sortie = f.FindIn("출격", PanelSortie)!.Box;
                return true;
            }
        }

        // reveal the end of the stage row
        for (int i = 0; i < 4; i++)
        {
            ctx.Input.Drag(1000, 600, 250, 600, holdMs: 100);
            ctx.Sleep(800);
        }

        using var shot = ctx.Capture();
        var stages = shot.ReadRegion(StageNumberRow)
            .SelectMany(l => Digits().Matches(l.Norm).Select(m => (N: int.Parse(m.Value), l.Box)))
            .OrderByDescending(s => s.N).ToList();
        foreach (var (n, box) in stages)
        {
            ctx.Click(box.CX, box.CY - 60, 2000); // the node sits above its number
            using var f = ctx.Capture();
            if (f.Has("소진")) throw new DailySkipException("오늘의 도전 횟수가 소진되었습니다");
            if (f.FindIn("출격", PanelSortie) is { } btn)
            {
                ctx.Log.Info($"{c.Name}: 스테이지 {n}");
                sortie = btn.Box;
                return true;
            }
            ctx.Click(Back.X, Back.Y, 1500); // close whatever opened (e.g. a locked stage)
        }
        return false;
    }

    private void DrainResults()
    {
        for (int i = 0; i < 5; i++)
        {
            using var f = ctx.Capture();
            if (!DismissOverlay(f)) return;
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private Frame? WaitFor(Func<Frame, bool> condition, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            var f = ctx.Capture();
            if (condition(f)) return f;
            f.Dispose();
            ctx.Sleep(1000);
        }
        return null;
    }

    private static int? ReadNumber(Frame f, Box region)
    {
        foreach (var l in f.ReadRegion(region))
        {
            var m = Digits().Match(l.Norm);
            if (m.Success) return int.Parse(m.Value);
        }
        return null;
    }

    private static string DayName(DayOfWeek d) => "일월화수목금토"[(int)d].ToString();

    // card text is over artwork and reads like "7남은졧수0", so only "남은" is anchored
    [GeneratedRegex(@"남은\D{0,3}?(\d+)")] private static partial Regex Remaining();
    [GeneratedRegex(@"남은횟수(\d+)/(\d+)")] private static partial Regex RemainingPage();
    [GeneratedRegex(@"(\d+)/(\d+)")] private static partial Regex Fraction();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();
}

/// <summary>Skips the current content; the run continues with the next one.</summary>
public sealed class DailySkipException(string reason) : Exception(reason);
