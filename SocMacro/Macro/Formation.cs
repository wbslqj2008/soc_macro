using SocMacro.Core;

namespace SocMacro.Macro;

internal static class Formation
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Swap disjoint pairs of deployed allies on the "캐릭터 배치" screen. Independent random pairs
    /// could undo each other (A↔B then B↔A); disjoint pairs always leave a different formation.
    /// </summary>
    public static void Reshuffle(MacroContext ctx, Frame f)
    {
        var allies = Vision.FindAllies(f);
        ctx.Log.SaveShot(f.Image, $"retry_deploy_{allies.Count}"); // for tuning ally detection
        if (allies.Count < 2)
        {
            ctx.Log.Info("재도전: 배치된 아군을 찾지 못해 그대로 시작합니다.");
            return;
        }

        var order = allies.OrderBy(_ => Rng.Next()).ToList();
        int pairs = Math.Clamp(ctx.Settings.SwapsOnRetry, 1, order.Count / 2);
        for (int i = 0; i < pairs; i++)
        {
            var (a, b) = (order[2 * i], order[2 * i + 1]);
            ctx.Input.Drag(a.X, a.Y - 20, b.X, b.Y - 10);
            ctx.Sleep(700);
        }
        ctx.Log.Info($"재도전: 아군 {allies.Count}명 중 {pairs * 2}명의 자리를 무작위로 교체");
    }
}
