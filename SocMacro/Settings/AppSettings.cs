using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocMacro.Settings;

public enum LaunchMode { Official, Steam }

public enum DisplayMode { Windowed, Fullscreen }

public enum Genre { Swift, Sturdy, Firepower }

public sealed class AppSettings
{
    // 일반
    public LaunchMode Launch { get; set; } = LaunchMode.Official;
    public string OfficialPath { get; set; } = @"C:\Program Files\SwordofConvallaria\SoCLauncher\XD.exe";
    public bool AutoLaunch { get; set; } = true;
    public DisplayMode Display { get; set; } = DisplayMode.Windowed;
    /// <summary>"자동" keeps the current window size; otherwise "WxH" resizes the client area (windowed only).</summary>
    public string Resolution { get; set; } = "자동";
    public bool BlockInput { get; set; } = true;

    // 탐험의 여정
    public bool JourneyEnabled { get; set; } = true;
    public int Stage { get; set; } = 5;
    public Genre Genre { get; set; } = Genre.Sturdy;
    /// <summary>Blessing keywords, highest priority first.</summary>
    public List<string> BlessingKeywords { get; set; } = ["보호막", "회복", "치료", "최대 HP"];
    /// <summary>Cards containing any of these are never picked unless all three do.</summary>
    public List<string> BlessingExcludeKeywords { get; set; } = [];
    /// <summary>Card picked when no keyword matches (1-3).</summary>
    public int BlessingFallback { get; set; } = 2;
    public bool UseAdvancedReward { get; set; } = true;
    public int MaxRounds { get; set; } = 0;
    public int MaxLossesPerNode { get; set; } = 3;
    public int SwapsOnRetry { get; set; } = 2;

    // 행동력
    public bool UseStaminaItem { get; set; } = true;
    /// <summary>How many 희망의 결정석 purchases per day the macro may make (0-4).</summary>
    public int GemRefillLimit { get; set; } = 0;
    public bool UnblockWhileWaiting { get; set; } = true;

    // 부가 기능
    public bool ClaimLevelRewards { get; set; } = false;
    public bool BuySupplyShop { get; set; } = false;

    // 일일퀘스트
    public bool DailyAfterReset { get; set; } = true;
    public bool UseOctopusCan { get; set; } = true;
    public Dictionary<string, bool> DailyContents { get; set; } = new()
    {
        ["타로의 자취"] = false,
        ["장비 시련"] = false,
        ["기억 회복"] = false,
        ["실전 훈련"] = false,
        ["진급 시련"] = false,
        ["뭇별의 시련"] = false,
        ["제작 시련"] = false,
    };
    /// <summary>Sweeps per content (only for contents whose count is configurable).</summary>
    public Dictionary<string, int> DailyCounts { get; set; } = [];
    /// <summary>Per content, 7 entries indexed by DayOfWeek (Sunday = 0): 1-based sub-trial, 0 = skip that day.</summary>
    public Dictionary<string, int[]> DailyWeekdayPicks { get; set; } = [];

    // 예약 실행 (KST, only while the program is running)
    public bool ScheduleEnabled { get; set; } = false;
    /// <summary>"HH:mm" in Korea Standard Time.</summary>
    public string ScheduleTime { get; set; } = "00:10";
    public Macro.RunMode ScheduleMode { get; set; } = Macro.RunMode.Journey;
    /// <summary>Indexed by DayOfWeek (Sunday = 0).</summary>
    public bool[] ScheduleDays { get; set; } = [true, true, true, true, true, true, true];
    /// <summary>KST date (yyyy-MM-dd) the schedule last fired, so it fires once a day across restarts.</summary>
    public string ScheduleLastRun { get; set; } = "";

    // 글로리 전장
    public bool GloryEnabled { get; set; } = false;
    /// <summary>0 = 쉬움, 1 = 일반, 2 = 어려움.</summary>
    public int GloryDifficulty { get; set; } = 0;
    public int GloryCount { get; set; } = 5;

    public int DailyCount(string name) => Math.Max(1, DailyCounts.GetValueOrDefault(name, 1));

    public int DailyPick(Macro.DailyContent c, DayOfWeek day)
    {
        var picks = DailyWeekdayPicks.GetValueOrDefault(c.Name);
        if (picks is not { Length: 7 }) picks = c.DefaultPicks();
        return Math.Clamp(picks[(int)day], 0, c.SubLabels.Length);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (JsonException) { }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
}
