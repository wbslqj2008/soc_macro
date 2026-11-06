using System.Text.RegularExpressions;
using SocMacro.Core;

namespace SocMacro.Macro;

/// <summary>Reads 존재의 힘 (stamina) and refills it from the "존재의 힘 보충" popup.</summary>
public sealed partial class StaminaManager(MacroContext ctx)
{
    public const int RunCost = 120;

    // top-right counter "121/240" on every screen that shows it
    private static readonly Box CounterRegion = new(1090, 14, 160, 44);
    private static readonly (double X, double Y) PlusButton = (1237, 36);
    private static readonly (double X, double Y) TabItem = (237, 205);
    private static readonly (double X, double Y) TabGem = (237, 310);
    private static readonly (double X, double Y) ConfirmButton = (895, 598);
    private static readonly (double X, double Y) CancelButton = (540, 598);

    public static int? Read(Frame f)
    {
        foreach (var line in f.ReadRegion(CounterRegion))
        {
            var m = Fraction().Match(line.Norm);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>Try one refill using the allowed sources. Returns true if stamina was added.</summary>
    public bool TryRefill()
    {
        var s = ctx.Settings;
        if (!s.UseStaminaItem && s.GemRefillLimit <= 0) return false;

        ctx.Click(PlusButton.X, PlusButton.Y, 1500);
        using (var f = ctx.Capture())
        {
            if (!f.Has("존재의힘보충"))
            {
                ctx.Log.Info("행동력 보충 창이 열리지 않았습니다.");
                return false;
            }
        }

        if (s.UseStaminaItem)
        {
            ctx.Click(TabItem.X, TabItem.Y, 1000);
            using var f = ctx.Capture();
            // quantity selector shows "selected/owned", e.g. "1/0"
            var owned = f.ReadRegion(new Box(590, 440, 250, 60))
                .Select(l => Fraction().Match(l.Norm)).FirstOrDefault(m => m.Success);
            if (owned != null && int.Parse(owned.Groups[2].Value) > 0)
            {
                ctx.Log.Info($"존재의 힘 결정 사용 (보유 {owned.Groups[2].Value}개)");
                ctx.Click(ConfirmButton.X, ConfirmButton.Y, 2000);
                DismissResult();
                return true;
            }
        }

        if (s.GemRefillLimit > 0)
        {
            ctx.Click(TabGem.X, TabGem.Y, 1000);
            using var f = ctx.Capture();
            var m = f.Match(DailyLimit());
            if (m is { } hit)
            {
                int remaining = int.Parse(hit.Match.Groups[1].Value);
                int total = int.Parse(hit.Match.Groups[2].Value);
                int used = total - remaining;
                if (remaining > 0 && used < s.GemRefillLimit)
                {
                    ctx.Log.Info($"희망의 결정석으로 행동력 구매 (오늘 {used + 1}/{s.GemRefillLimit}회째)");
                    ctx.Click(ConfirmButton.X, ConfirmButton.Y, 2000);
                    DismissResult();
                    return true;
                }
                ctx.Log.Info($"결정석 구매 한도 도달 (오늘 {used}회 사용, 설정 한도 {s.GemRefillLimit}회)");
            }
        }

        ctx.Click(CancelButton.X, CancelButton.Y, 1000);
        return false;
    }

    /// <summary>Close whatever popup is left after using/buying (reward toast or the refill window).</summary>
    private void DismissResult()
    {
        for (int i = 0; i < 3; i++)
        {
            using var f = ctx.Capture();
            if (f.Has("존재의힘보충")) ctx.Click(CancelButton.X, CancelButton.Y, 1000);
            else if (f.HasAny("획득", "보상")) ctx.Click(637, 650, 1000);
            else return;
        }
    }

    [GeneratedRegex(@"(\d+)/(\d+)")] private static partial Regex Fraction();
    [GeneratedRegex(@"일일한정구매\((\d)/(\d)\)")] private static partial Regex DailyLimit();
}
