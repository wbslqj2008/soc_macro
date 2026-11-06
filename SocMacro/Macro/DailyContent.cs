namespace SocMacro.Macro;

public enum DailyKind { Sweep, Battle }

/// <summary>
/// One daily content in 엇갈린 세계. SubLabels are the (normalized) card labels on its selection
/// page; contents without them open straight onto the stage map.
/// </summary>
public sealed record DailyContent(string Name, DailyKind Kind, string[] SubLabels, string[] SubNames, bool CountConfigurable)
{
    public string Norm => Name.Replace(" ", "");
    public bool HasSub => SubLabels.Length > 0;

    public static readonly DailyContent[] All =
    [
        new("타로의 자취", DailyKind.Sweep,
            ["타로의자취1", "타로의자취2", "타로의자취3", "타로의자취4", "타로의자취5"],
            ["1", "2", "3", "4", "5"], CountConfigurable: false),
        new("장비 시련", DailyKind.Sweep,
            ["장비시련1", "장비시련2", "장비시련3"],
            ["1", "2", "3"], CountConfigurable: false),
        new("기억 회복", DailyKind.Battle, [], [], CountConfigurable: false),
        new("실전 훈련", DailyKind.Sweep, [], [], CountConfigurable: true),
        new("진급 시련", DailyKind.Sweep,
            ["수호자시련", "침입자시련", "파멸자시련", "분쇄자시련", "감시자시련"],
            ["수호자", "침입자", "파멸자", "분쇄자", "감시자"], CountConfigurable: true),
        new("뭇별의 시련", DailyKind.Sweep,
            ["뭇별의시련1", "뭇별의시련2", "뭇별의시련3"],
            ["1", "2", "3"], CountConfigurable: true),
        new("제작 시련", DailyKind.Sweep, [], [], CountConfigurable: true),
    ];

    /// <summary>Carousel order, used to decide which way to scroll.</summary>
    public int Order => Array.IndexOf(All, this);

    /// <summary>Default weekday picks (index = DayOfWeek, Sunday = 0; value = 1-based sub, 0 = skip).</summary>
    public int[] DefaultPicks() => Name switch
    {
        // observed: 2 화/금, 3 수/토, 5 화/목/토, everything on Sunday, 1 and 4 open on Monday
        "타로의 자취" => [1, 1, 2, 3, 5, 2, 3],
        // observed: 2 화/금, 3 수/토, everything on Sunday, 1 open on Monday
        "장비 시련" => [1, 1, 2, 3, 1, 2, 3],
        _ => [1, 1, 1, 1, 1, 1, 1],
    };
}
