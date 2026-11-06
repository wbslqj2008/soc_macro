using System.Text.RegularExpressions;
using SocMacro.Core;

namespace SocMacro.Macro;

/// <summary>
/// Loops the "탐험의 여정" event. Each tick captures the screen, recognizes which screen it is
/// (mostly by OCR) and performs one action, so it can start or resume from any point.
/// Button positions are in base coordinates, measured on the 1274x717 client.
/// </summary>
public sealed partial class JourneyBot(MacroContext ctx)
{
    // starts at the left edge: OCR often merges the title with the back button ("@이벤트"), moving its center left
    private static readonly Box TitleArea = new(0, 5, 560, 60);
    private static readonly Box MapProgressArea = new(40, 100, 240, 50);
    private static readonly Box MainProgressArea = new(90, 600, 260, 75);
    private static readonly Box EventTabColumn = new(0, 80, 200, 600);
    private static readonly Box EventBannerTitle = new(800, 180, 440, 120);
    private static readonly Box LobbyEventButton = new(840, 115, 120, 45);
    private static readonly Box StageList = new(0, 90, 420, 440);
    private static readonly Box BattleStartArea = new(820, 560, 420, 110);
    private static readonly Box AdvancedRewardArea = new(680, 14, 150, 44);

    private static readonly double[] LabelRows = [282, 372, 462];
    private static readonly double[] BlessingCardX = [195, 516, 837];
    private static readonly double[] BlessingSelectX = [316, 637, 957];
    private static readonly double[] GenreCardX = [350, 637, 920];

    private readonly StaminaManager stamina = new(ctx);
    private readonly Dictionary<int, int> losses = [];

    private int? currentBattle;
    private bool abandonPending;
    private bool reshuffleOnDeploy;
    private bool autoLogged;
    private bool victoryHandled;
    private bool inBattle;
    private bool finished;
    private bool clickedJourneyTab;
    private DateTime battleStarted;
    private DateTime lastHudSeen;
    private DateTime lastDay = DateTime.Today;

    public int Rounds { get; private set; }

    public event Action<int>? RoundCompleted;

    public void Run()
    {
        DateTime? unknownSince = null;
        int recoveries = 0;
        string lastState = "";
        DateTime stateSince = DateTime.Now;

        finished = false;
        while (!finished)
        {
            string? state;
            using (var f = ctx.Capture())
            {
                state = Step(f);
                if (state == null)
                {
                    // second chance: banded OCR picks up text the full-frame pass dropped
                    f.RereadBanded();
                    state = Step(f);
                }
                if (state == null) HandleUnknown(f, ref unknownSince, ref recoveries);
            }

            if (state == null) continue;
            unknownSince = null;
            recoveries = 0;

            if (state != lastState)
            {
                lastState = state;
                stateSince = DateTime.Now;
                ctx.ReportStatus(Describe(state));
            }
            else
            {
                // the same screen for too long means an action isn't working
                var limit = state == "battle" ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(5);
                if (DateTime.Now - stateSince > limit)
                {
                    using var f = ctx.Capture();
                    var shot = ctx.Log.SaveShot(f.Image, "stuck");
                    throw new MacroStopException($"'{state}' 화면에서 {limit.TotalMinutes:0}분 넘게 진행이 없어 중단합니다. 스크린샷: {shot}");
                }
            }
        }
    }

    private string Describe(string state)
    {
        string battle = currentBattle is int n ? $" {n}/7" : "";
        return state switch
        {
            "battle" or "battle_panel" or "deploy" => $"전투{battle} 진행 중",
            "victory" => $"전투{battle} 승리",
            "defeat" or "defeat_popup" => $"전투{battle} 패배 처리",
            "map" => "탐색 맵 이동",
            "blessing" => "축복 선택",
            "stage" or "genre" => "탐험 준비",
            "journey_main" => "탐험의 여정 메인",
            "settle" or "loot" or "complete" or "summary" or "level_up" or "returning" => "결산·보상 확인",
            "abandon_confirm" => "진행도 포기",
            "stamina_popup" => "행동력 보충",
            _ => "이벤트로 이동 중",
        };
    }

    private void HandleUnknown(Frame f, ref DateTime? since, ref int recoveries)
    {
        // During a battle, cut-ins and skill animations hide the HUD for a few seconds; don't press
        // "back" then. But once the HUD has been gone for a while the battle is over and this is a
        // result screen we failed to read (seen: "전투 실패" read as "구두 실패", stuck for 20 minutes).
        if (inBattle && DateTime.Now - lastHudSeen < TimeSpan.FromMinutes(2))
        {
            ctx.Sleep(3000);
            return;
        }
        since ??= DateTime.Now;
        var idle = DateTime.Now - since.Value;
        if (idle > TimeSpan.FromSeconds(25 * (recoveries + 1)) && recoveries < 2)
        {
            recoveries++;
            var shot = ctx.Log.SaveShot(f.Image, "unknown");
            ctx.Log.Info($"알 수 없는 화면입니다. 뒤로가기를 눌러 봅니다. (인식된 글자: {Trim(f.AllText, 80)}) {shot}");
            ctx.Click(73, 33, 2500);
            return;
        }
        if (idle > TimeSpan.FromMinutes(3))
            throw new MacroStopException("알 수 없는 화면에서 3분 넘게 벗어나지 못해 중단합니다. logs/shots 폴더의 스크린샷을 확인해 주세요.");
        ctx.Sleep(1500);
    }

    /// <summary>Recognize the screen and perform one action. Returns null if the screen is unknown.</summary>
    private string? Step(Frame f)
    {
        // popups and full-screen overlays first, they sit on top of other screens
        if (f.Has("전투패배"))
        {
            ctx.Click(472, 488, 2500); // 취소 → 전투 실패 화면
            return "defeat_popup";
        }
        if (ctx.Vision.IsDefeatScreen(f))
        {
            OnDefeat();
            ctx.Click(806, 619, 3000); // 전투 종료
            return "defeat";
        }
        if (f.Has("진행도를포기"))
        {
            ctx.Log.Info("진행도를 포기하고 처음부터 다시 시작합니다.");
            ctx.Click(800, 488, 3000);
            abandonPending = false;
            losses.Clear();
            currentBattle = null;
            return "abandon_confirm";
        }
        if (f.Has("존재의힘보충"))
        {
            ctx.Click(540, 598, 1000); // stray refill popup: 취소
            return "stamina_popup";
        }
        if (f.HasAny("게임공지", "이벤트공지"))
        {
            ctx.Click(1105, 97, 1500);
            return "notice";
        }
        if (f.Find("게임시작", new Box(400, 500, 480, 120)) is { } start)
        {
            ctx.Log.Info("타이틀 화면 → 게임 시작");
            ctx.Click(start.Box, 10000);
            return "title";
        }
        if (f.Has("축복을선택"))
        {
            PickBlessing(f);
            return "blessing";
        }
        if (f.Has("난이도클리어"))
        {
            Settle(f);
            return "settle";
        }
        if (f.Has("획득정보")) { ctx.Click(1212, 640, 2000); return "loot"; }
        if (f.Has("모험완료")) { ctx.Click(637, 600, 2000); return "complete"; }
        if (f.Has("신비한세계의견문")) { ctx.Click(637, 680, 2000); return "summary"; }
        if (f.Has("모험가클래스")) { ctx.Click(637, 600, 2000); return "level_up"; }
        if (f.Has("도전이종료")) { ctx.Click(637, 600, 4000); return "returning"; }
        if (f.Has("전투승리"))
        {
            // the result screen can survive the first click while it animates; count it once
            if (!victoryHandled) OnVictory();
            victoryHandled = true;
            ctx.Click(1212, 640, 3000);
            return "victory";
        }
        if (f.Has("탐험장르선택"))
        {
            int g = (int)ctx.Settings.Genre;
            ctx.Click(GenreCardX[g], 330, 800);
            ctx.Click(815, 632, 3000);
            return "genre";
        }
        if (f.Find("난이도선택", TitleArea) != null)
        {
            ChooseStage(f);
            return "stage";
        }
        // the title is sometimes misread (e.g. "개릭터豊치"), so the roster/buttons also identify the screen
        if (f.Find("캐릭터배치", TitleArea) != null || (f.Has("출전") && f.HasAny("전술", "전투력")))
        {
            Deploy(f);
            return "deploy";
        }
        if (f.Find("전투시작", BattleStartArea) is { } fight)
        {
            ctx.Click(fight.Box, 4000);
            return "battle_panel";
        }
        if (ctx.Vision.BattleHud(f) is (true, var auto))
        {
            if (!inBattle)
            {
                // started (or restarted) in the middle of a battle
                inBattle = true;
                battleStarted = DateTime.Now;
            }
            lastHudSeen = DateTime.Now;
            EnsureAuto(auto);
            return "battle";
        }
        if (IsJourneyMain(f))
        {
            JourneyMain(f);
            return "journey_main";
        }
        if (f.Find("탐색진행도", MapProgressArea) != null)
        {
            Map(f);
            return "map";
        }
        if (f.Find("이벤트", TitleArea) != null)
        {
            EventPage(f);
            return "event_page";
        }
        if (f.FindIn("이벤트", LobbyEventButton) != null)
        {
            ctx.Log.Info("로비 → 이벤트");
            ctx.Click(897, 110, 3000);
            return "lobby";
        }
        return null;
    }

    // The title is the first thing OCR gets wrong on small windows, so the page's own side panel
    // (탐험 보급소 together with 탐험 코인 / 정산 기록 / 탐험 레벨) also identifies it.
    private static bool IsJourneyMain(Frame f) =>
        f.Has("보급소") && (f.Find("탐험의여정", TitleArea) != null || f.HasAny("탐험코인", "정산기록", "탐험레벨"));

    private void EventPage(Frame f)
    {
        if (f.Find("탐험의여정", EventBannerTitle) != null || clickedJourneyTab)
        {
            clickedJourneyTab = false;
            ctx.Click(1013, 594, 3000); // 이동
            return;
        }
        // "여정!" with the exclamation mark only appears on this tab ("여정의 여파" has none)
        if ((f.Find("탐험의여정", EventTabColumn) ?? f.Find("여정!", EventTabColumn)) is { } tab)
        {
            ctx.Click(tab.Box, 1500);
            clickedJourneyTab = true;
            return;
        }
        ctx.Log.Info("이벤트 목록에서 '탐험의 여정!' 탭을 찾지 못했습니다.");
        ctx.Sleep(2000);
    }

    private void JourneyMain(Frame f)
    {
        bool inProgress = f.Find("탐색진행도", MainProgressArea) != null;
        inBattle = false;

        if (abandonPending)
        {
            if (inProgress)
            {
                ctx.Click(65, 615, 2000); // 포기
                return;
            }
            abandonPending = false;
        }

        if (!inProgress)
        {
            // checked here, after the result screens, so the game is left on the event main screen
            if (ctx.Settings.MaxRounds > 0 && Rounds >= ctx.Settings.MaxRounds)
            {
                ctx.Log.Info($"설정한 반복 횟수({ctx.Settings.MaxRounds}회)를 모두 마쳤습니다.");
                finished = true;
                return;
            }
            CheckDayRollover();
            if (DailyDue)
            {
                finished = true; // the runner does the dailies, then calls Run again
                return;
            }
            int? now = StaminaManager.Read(f);
            if (now is int read) ctx.ReportStamina(read);
            if (now is int v && v < StaminaManager.RunCost)
            {
                if (!EnsureStamina(v)) return;
                // refilled or waited: log the current value, not the one from before
                using var after = ctx.Capture();
                now = StaminaManager.Read(after) ?? now;
            }
            losses.Clear();
            currentBattle = null;
            ctx.Log.Info($"{Rounds + 1}번째 탐험 시작 (행동력 {now?.ToString() ?? "?"})");
        }
        ctx.Click(840, 625, 3000); // 탐색 시작 / 탐색 복귀
    }

    private void ChooseStage(Frame f)
    {
        int n = Math.Clamp(ctx.Settings.Stage, 1, 5);
        string key = n == 1 ? "난이도1" : $"스테이지{n}";
        // exact matching only: "스테이지5" is one character away from "스테이지2"
        var line = f.Lines.FirstOrDefault(l => l.Norm == key && StageList.ContainsCenterOf(l.Box))
                   ?? f.Lines.FirstOrDefault(l => l.Norm.Contains(key) && StageList.ContainsCenterOf(l.Box));
        if (line != null) ctx.Click(line.Box.CX, line.Box.CY, 1000);
        else ctx.Click(200, 132 + 87 * (n - 1), 1000);
        ctx.Click(1087, 637, 3000); // 입장
    }

    private void Map(Frame f)
    {
        inBattle = false;
        if (abandonPending)
        {
            ctx.Click(73, 33, 2500); // back to the event main, where 포기 is
            return;
        }

        if (ctx.Vision.FindActiveEventNode(f) is { } node)
        {
            ctx.Click(node, 2000);
            return;
        }

        var progress = f.Match(Progress(), MapProgressArea);
        if (progress == null) { ctx.Sleep(1000); return; }
        int done = int.Parse(progress.Value.Match.Groups[1].Value);
        int total = int.Parse(progress.Value.Match.Groups[2].Value);
        int next = done + 1;
        if (next > total)
        {
            ctx.Sleep(2000); // settlement appears on its own
            return;
        }

        // Full-frame OCR can merge a label with neighbouring node icons (seen at the map's end),
        // so also read the three rows where battle labels sit.
        var labelLines = f.Lines.Concat(LabelRows.SelectMany(y => f.ReadRegion(new Box(0, y - 22, GameWindow.BaseW, 44))));
        var visible = new List<(int Number, OcrLine Line)>();
        foreach (var l in labelLines)
        {
            var m = BattleLabel().Match(l.Norm);
            if (m.Success) visible.Add((int.Parse(m.Groups[1].Value), l));
        }

        var target = visible.FirstOrDefault(v => v.Number == next).Line;
        if (target != null)
        {
            // too close to the left edge: the event node before it may be off-screen
            if (target.Box.X < 260)
            {
                ScrollMap(toEarlier: true, distance: 400);
                return;
            }
            currentBattle = next;
            // the node icon sits just left of its "VoD x-y" label
            ctx.Click(target.Box.X - 40, target.Box.CY, 2000);
            return;
        }

        // the map keeps its scroll position after backing out of a battle, so the next node can be off-screen
        if (visible.Count > 0)
        {
            bool earlier = visible.Min(v => v.Number) > next;
            ctx.Log.Info($"다음 전투 노드({next}/{total})가 화면 밖에 있어 맵을 {(earlier ? "왼쪽" : "오른쪽")}으로 이동합니다.");
            ScrollMap(toEarlier: earlier, distance: 600);
            return;
        }
        ctx.Log.Info($"다음 전투 노드({next}/{total})를 찾지 못했습니다.");
        ScrollMap(toEarlier: true, distance: 600);
    }

    /// <summary>Pan the exploration map. toEarlier reveals nodes to the left.</summary>
    private void ScrollMap(bool toEarlier, int distance)
    {
        const double y = 560; // empty band below the path
        double from = toEarlier ? 300 : 300 + distance, to = toEarlier ? 300 + distance : 300;
        ctx.Input.Drag(from, y, to, y, holdMs: 100);
        ctx.Sleep(1000);
    }

    private void PickBlessing(Frame f)
    {
        static List<string> Norm(IEnumerable<string> ks) =>
            ks.Select(OcrService.NormalizeLoose).Where(k => k.Length > 0).ToList();
        var keywords = Norm(ctx.Settings.BlessingKeywords);
        var excludes = Norm(ctx.Settings.BlessingExcludeKeywords);
        var texts = BlessingCardX
            .Select(x => string.Concat(f.ReadRegion(new Box(x, 260, 240, 250)).Select(l => OcrService.NormalizeLoose(l.Text))))
            .ToList();
        bool Has(int card, string k) => texts[card].Contains(k, StringComparison.OrdinalIgnoreCase);
        var allowed = Enumerable.Range(0, texts.Count).Where(i => !excludes.Any(k => Has(i, k))).ToList();

        int best = -1, bestRank = int.MaxValue;
        foreach (int i in allowed)
            for (int k = 0; k < keywords.Count && k < bestRank; k++)
                if (Has(i, keywords[k]))
                {
                    best = i;
                    bestRank = k;
                }

        string reason;
        if (best >= 0) reason = $"키워드 '{keywords[bestRank]}'";
        else
        {
            int fallback = Math.Clamp(ctx.Settings.BlessingFallback, 1, 3) - 1;
            // prefer the fallback slot, but not if it was excluded
            best = allowed.Contains(fallback) || allowed.Count == 0 ? fallback : allowed[0];
            reason = allowed.Count == 0 ? "모든 카드가 제외 키워드에 걸림, 기본 위치" : "일치하는 키워드 없음";
        }

        ctx.Log.Info($"축복 {best + 1}번 선택 ({reason}): {Trim(texts[best].Replace("[아군적용]", ""), 40)}");
        ctx.Click(BlessingSelectX[best], 548, 2000);
    }

    private void Deploy(Frame f)
    {
        if (reshuffleOnDeploy)
        {
            reshuffleOnDeploy = false;
            Formation.Reshuffle(ctx, f);
        }
        battleStarted = DateTime.Now;
        lastHudSeen = DateTime.Now; // the HUD takes a moment to appear after 시작
        inBattle = true;
        autoLogged = false;
        victoryHandled = false;
        ctx.Click(1183, 560, 5000); // 시작
    }

    private void EnsureAuto(bool auto)
    {
        if (!auto)
        {
            ctx.Click(143, 36, 2000);
            if (!autoLogged) ctx.Log.Info($"전투 {currentBattle?.ToString() ?? "?"}: 자동 전투 켜기");
            autoLogged = true;
            return;
        }
        ctx.Sleep(4000);
    }

    private void OnVictory()
    {
        inBattle = false;
        var took = DateTime.Now - battleStarted;
        ctx.Log.Info($"전투 {currentBattle?.ToString() ?? "?"} 승리 ({took.TotalSeconds:0}초)");
        if (currentBattle is int n) losses.Remove(n);
    }

    private void OnDefeat()
    {
        inBattle = false;
        int n = currentBattle ?? 0;
        losses[n] = losses.GetValueOrDefault(n) + 1;
        int max = Math.Max(1, ctx.Settings.MaxLossesPerNode);
        if (losses[n] >= max)
        {
            ctx.Log.Info($"전투 {n} 패배 ({losses[n]}/{max}) → 이번 탐험을 포기하고 다시 시작합니다.");
            abandonPending = true;
        }
        else
        {
            ctx.Log.Info($"전투 {n} 패배 ({losses[n]}/{max}) → 배치를 바꿔 재도전합니다.");
            reshuffleOnDeploy = true;
        }
    }

    private void Settle(Frame f)
    {
        // read only the top bar counter: the reward card text also says "고급 보상 1회 추가로 소모"
        int advanced = 0;
        foreach (var l in f.ReadRegion(AdvancedRewardArea))
        {
            var m = AdvancedCount().Match(l.Norm);
            if (m.Success) { advanced = int.Parse(m.Groups[1].Value); break; }
        }

        bool useAdvanced = ctx.Settings.UseAdvancedReward && advanced > 0;
        ctx.Click(useAdvanced ? 889 : 383, 529, 2500);
        Rounds++;
        ctx.Log.Info($"{Rounds}번째 탐험 완료 → {(useAdvanced ? $"고급 보상 교환 (보상권 {advanced}장)" : "클리어 포인트 교환")}");
        RoundCompleted?.Invoke(Rounds);
    }

    /// <summary>Refill if allowed, otherwise wait for natural regeneration. Returns false if it gave up this tick.</summary>
    private bool EnsureStamina(int current)
    {
        for (int i = 0; i < 2 && current < StaminaManager.RunCost; i++)
        {
            if (!stamina.TryRefill()) break;
            using var f = ctx.Capture();
            current = StaminaManager.Read(f) ?? current;
        }
        if (current >= StaminaManager.RunCost) return true;

        ctx.Log.Info($"행동력 부족 ({current}/{StaminaManager.RunCost}). 자연 회복을 기다립니다.");
        ctx.ReportStatus("행동력 회복 대기");
        if (ctx.Settings.UnblockWhileWaiting) ctx.RequestBlock(false);
        try
        {
            while (true)
            {
                ctx.Sleep(5 * 60 * 1000);
                using var f = ctx.Capture(); // background capture, doesn't steal focus
                int? v = StaminaManager.Read(f);
                if (v is int value)
                {
                    ctx.ReportStamina(value);
                    ctx.Log.Info($"행동력 {value}/{StaminaManager.RunCost}");
                    if (value >= StaminaManager.RunCost) return true;
                }
                if (DateTime.Today != lastDay)
                {
                    // daily purchase limits reset at midnight
                    CheckDayRollover();
                    return false;
                }
            }
        }
        finally
        {
            if (ctx.Settings.UnblockWhileWaiting) ctx.RequestBlock(true);
        }
    }

    private void CheckDayRollover()
    {
        if (DateTime.Today == lastDay) return;
        lastDay = DateTime.Today;
        if (ctx.Settings.DailyAfterReset && (ctx.Settings.DailyContents.ContainsValue(true) || ctx.Settings.GloryEnabled))
        {
            ctx.Log.Info("일일 초기화(00시)가 지났습니다. 일일퀘스트를 먼저 진행합니다.");
            DailyDue = true;
        }
        else ctx.Log.Info("일일 초기화(00시)가 지났습니다.");
    }

    /// <summary>Set when midnight passed and daily quests should run before the next round.</summary>
    public bool DailyDue { get; set; }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"(\d)/(\d)")] private static partial Regex Progress();
    // OCR variants seen: "VoD5-1", ",VoD5-2", "VoD5-14"
    [GeneratedRegex(@"[VvY][oO0]D\d+-(\d)")] private static partial Regex BattleLabel();
    [GeneratedRegex(@"고급보상(\d+)")] private static partial Regex AdvancedCount();
}
