using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Net.Http;
using SocMacro.Core;
using SocMacro.License;
using SocMacro.Macro;
using SocMacro.Settings;

namespace SocMacro;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int HotkeyStart = 1, HotkeyStop = 2;
    private const uint VkF9 = 0x78, VkF10 = 0x79, ModNoRepeat = 0x4000;
    private const int MaxLogLines = 500, RecentLines = 8;

    private static readonly string[] Resolutions = ["자동", "1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440"];
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush Running = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush Failed = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    private readonly AppSettings settings = AppSettings.Load();
    private readonly string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private readonly Log log;
    private readonly InputBlocker blocker = new(VkF9, VkF10);
    private sealed record DailyRow(DailyContent Content, ToggleButton Enabled, Wpf.Ui.Controls.TextBox? Count, ComboBox[]? Weekdays);

    private readonly List<DailyRow> dailyRows = [];
    private readonly ObservableCollection<string> recent = [];
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? cts;
    private Task<string>? running;
    private DateTime startedAt;

    private readonly LicenseService license;
    private LicenseStatus licenseStatus;

    public MainWindow(LicenseService license, LicenseStatus status)
    {
        InitializeComponent();
        this.license = license;
        licenseStatus = status;
        ShowLicense();
        log = new Log(logDir);
        RecentLog.ItemsSource = recent;
        foreach (var r in Resolutions) Resolution.Items.Add(r);
        foreach (var c in DailyContent.All) DailyPanel.Children.Add(BuildDailyCard(c));
        foreach (var page in new[] { DashboardPage, JourneyPage, DailyPage, SettingsPage, AccountPage, AdminPage })
            page.PreviewMouseWheel += ForwardWheel;
        clock.Tick += (_, _) => ElapsedText.Text = (DateTime.Now - startedAt).ToString(@"hh\:mm\:ss");
        log.Line += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        BuildScheduleControls();
        LoadToUi();
        UpdateScheduleNext();
        scheduleTimer.Tick += (_, _) => OnScheduleTick();
        scheduleTimer.Start();
        // e.g. for Task Scheduler: SocMacro.exe --autostart
        if (Environment.GetCommandLineArgs().Contains("--autostart"))
            Loaded += (_, _) => StartMacro(Environment.GetCommandLineArgs().Contains("--daily") ? RunMode.Daily : RunMode.Journey);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source.AddHook(WndProc);
        Native.RegisterHotKey(source.Handle, HotkeyStart, ModNoRepeat, VkF9);
        Native.RegisterHotKey(source.Handle, HotkeyStop, ModNoRepeat, VkF10);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == HotkeyStart && running == null) StartMacro(RunMode.Journey);
            if (wParam.ToInt32() == HotkeyStop) StopMacro();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DashboardPage == null) return; // fires during InitializeComponent
        string page = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "Dashboard";
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        JourneyPage.Visibility = page == "Journey" ? Visibility.Visible : Visibility.Collapsed;
        DailyPage.Visibility = page == "Daily" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = page == "Account" ? Visibility.Visible : Visibility.Collapsed;
        AdminPage.Visibility = page == "Admin" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Admin" && KeyList.Items.Count == 0) LoadKeys_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Cards, lists and text boxes inside a page swallow the mouse wheel, so the page never scrolls.
    /// Scroll the page unless the pointer is over an inner list that can still scroll that way.
    /// </summary>
    private static void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        var outer = (ScrollViewer)sender;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != outer;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                bool canScroll = e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight;
                if (canScroll) return;
            }
        }
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // ───────────────────────── schedule ─────────────────────────

    private readonly DispatcherTimer scheduleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly CheckBox[] scheduleDayBoxes = new CheckBox[7]; // display order: 월..일

    private void BuildScheduleControls()
    {
        for (int h = 0; h < 24; h++) ScheduleHour.Items.Add(h.ToString("00"));
        for (int m = 0; m < 60; m++) ScheduleMinute.Items.Add(m.ToString("00"));
        for (int i = 0; i < 7; i++)
        {
            var box = new CheckBox { Content = WeekdayNames[i], Margin = new Thickness(0, 0, 12, 0) };
            box.Click += (_, _) => UpdateScheduleNext();
            scheduleDayBoxes[i] = box;
            ScheduleDaysPanel.Children.Add(box);
        }
        ScheduleEnabled.Click += (_, _) => UpdateScheduleNext();
        ScheduleHour.SelectionChanged += (_, _) => UpdateScheduleNext();
        ScheduleMinute.SelectionChanged += (_, _) => UpdateScheduleNext();
    }

    /// <summary>The schedule as currently shown on screen (so edits apply without restarting).</summary>
    private bool ScheduleFromUi(out TimeSpan time, out bool[] days, out RunMode mode)
    {
        time = new TimeSpan(Math.Max(0, ScheduleHour.SelectedIndex), Math.Max(0, ScheduleMinute.SelectedIndex), 0);
        days = new bool[7];
        for (int i = 0; i < 7; i++) days[(int)WeekdayOrder[i]] = scheduleDayBoxes[i].IsChecked == true;
        mode = ScheduleMode.SelectedIndex == 1 && licenseStatus.IsPro ? RunMode.Daily : RunMode.Journey;
        return ScheduleEnabled.IsChecked == true;
    }

    private void OnScheduleTick()
    {
        if (ScheduleFromUi(out var time, out var days, out var mode))
        {
            var now = Schedule.NowKst();
            if (Schedule.ShouldFire(now, time, days, settings.ScheduleLastRun))
            {
                // remember first, so a restart or a failed start doesn't fire it twice today
                settings.ScheduleLastRun = now.ToString("yyyy-MM-dd");
                ReadFromUi();
                settings.Save();
                string what = mode == RunMode.Daily ? "일일퀘스트" : "탐험의 여정";
                if (running != null || checkingLicense)
                    log.Info($"[예약] {time:hh\\:mm}(KST) 예약 시각이지만 이미 실행 중이라 건너뜁니다.");
                else
                {
                    log.Info($"[예약] {time:hh\\:mm}(KST) 예약 실행 - {what}");
                    StartMacro(mode);
                }
            }
        }
        UpdateScheduleNext();
    }

    private void UpdateScheduleNext()
    {
        if (!ScheduleFromUi(out var time, out var days, out var mode))
        {
            ScheduleNext.Text = "예약 실행이 꺼져 있습니다.";
            return;
        }
        var next = Schedule.Next(Schedule.NowKst(), time, days, settings.ScheduleLastRun);
        string what = mode == RunMode.Daily ? "일일퀘스트" : "탐험의 여정";
        ScheduleNext.Text = next is { } at
            ? $"다음 실행: {at:MM/dd}({"일월화수목금토"[(int)at.DayOfWeek]}) {at:HH:mm} KST · {what}"
            : "선택된 요일이 없습니다.";
    }

    // ───────────────────────── license ─────────────────────────

    private void ShowLicense()
    {
        AccountKey.Text = license.Key;
        AccountStatus.Text = $"{licenseStatus.Describe()} 키";
        SideLicense.Text = $"{licenseStatus.Describe()} 키";
        // only an admin key sees the admin page (and the key list)
        AdminNav.Visibility = licenseStatus.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (!licenseStatus.IsAdmin && AdminPage.Visibility == Visibility.Visible) Nav.SelectedIndex = 0;

        // basic keys: 탐험의 여정 only
        bool pro = licenseStatus.IsPro;
        BasicInfo.IsOpen = !pro;
        DailyButton.IsEnabled = pro && running == null;
        DailyPage.IsEnabled = pro;
        DailyNavLabel.Text = pro ? "일일퀘스트" : "일일퀘스트 (프로)";
    }

    /// <summary>Re-check with the server. Returns null (and tells the user) if the server can't be reached.</summary>
    private async Task<LicenseStatus?> RefreshLicense()
    {
        try
        {
            licenseStatus = await license.Check();
            ShowLicense();
            return licenseStatus;
        }
        catch (Exception ex) when (ex is LicenseException or HttpRequestException or TaskCanceledException)
        {
            ShowAlert(ex is LicenseException ? ex.Message : "라이센스 서버에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.");
            return null;
        }
    }

    private async void RefreshLicense_Click(object sender, RoutedEventArgs e) => await RefreshLicense();

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (running != null) StopMacro();
        license.ForgetKey();
        // restart into the key window
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        Application.Current.Shutdown();
    }

    // ───────────────────────── admin ─────────────────────────

    private readonly LicenseAdmin admin = new();
    private List<LicenseKeyInfo> keyInfos = [];

    private async Task Admin(Func<Task> action)
    {
        AdminMessage.Text = "처리 중...";
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is LicenseException or HttpRequestException or TaskCanceledException)
        {
            AdminMessage.Text = ex is LicenseException ? ex.Message : "서버에 연결할 수 없습니다.";
        }
    }

    private async void IssueKeys_Click(object sender, RoutedEventArgs e)
    {
        int count = ParseInt(KeyCount.Text, 1, 1, 50);
        var tier = NewTier.SelectedIndex == 0 ? LicenseTier.Basic : LicenseTier.Pro;
        string custom = count == 1 ? CustomKey.Text.Trim() : "";
        await Admin(async () =>
        {
            var keys = new List<string>();
            for (int i = 0; i < count; i++)
                keys.Add(await admin.CreateKey(custom, tier, KeyNote.Text.Trim()));
            IssuedKeys.Text = string.Join(Environment.NewLine, keys);
            Clipboard.SetText(IssuedKeys.Text);
            CustomKey.Text = "";
            AdminMessage.Text = $"{(tier == LicenseTier.Pro ? "프로" : "베이직")} 키 {keys.Count}개를 발급했습니다. (클립보드에 복사됨)";
            await LoadKeys();
        });
    }

    private async void LoadKeys_Click(object sender, RoutedEventArgs e) => await Admin(LoadKeys);

    private async Task LoadKeys()
    {
        keyInfos = await admin.List();
        KeyList.Items.Clear();
        foreach (var k in keyInfos)
        {
            string tier = k.Admin ? "관리자" : k.Tier == "pro" ? "프로  " : "베이직";
            string seen = k.LastSeen is { } s ? $"최근 {s:MM-dd HH:mm}" : "미사용";
            KeyList.Items.Add($"{k.Key,-16} {tier}  {seen,-16} {k.Note}");
        }
        if (keyInfos.Count == 0) KeyList.Items.Add("발급한 키가 없습니다.");
        AdminMessage.Text = $"키 {keyInfos.Count}개";
    }

    private void KeyList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        KeyActions.IsEnabled = SelectedKey() != null;

    private LicenseKeyInfo? SelectedKey() =>
        KeyList.SelectedIndex >= 0 && KeyList.SelectedIndex < keyInfos.Count ? keyInfos[KeyList.SelectedIndex] : null;

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKey() is { } k)
        {
            Clipboard.SetText(k.Key);
            AdminMessage.Text = $"{k.Key} 복사됨";
        }
    }

    private async void MakePro_Click(object sender, RoutedEventArgs e) => await KeyAction(k => admin.SetTier(k, LicenseTier.Pro));

    private async void MakeBasic_Click(object sender, RoutedEventArgs e) => await KeyAction(k => admin.SetTier(k, LicenseTier.Basic));

    private async void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKey() is not { } k) return;
        if (System.Windows.MessageBox.Show(this, $"{k.Key} 키를 삭제할까요? 이 키로는 더 이상 사용할 수 없게 됩니다.", "키 삭제",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await KeyAction(admin.Delete);
    }

    private async Task KeyAction(Func<string, Task> action)
    {
        if (SelectedKey() is not { } k) return;
        if (k.Admin)
        {
            AdminMessage.Text = "관리자 키는 여기서 바꾸거나 삭제할 수 없습니다.";
            return;
        }
        await Admin(async () =>
        {
            await action(k.Key);
            await LoadKeys();
        });
    }

    private static readonly string[] WeekdayNames = ["월", "화", "수", "목", "금", "토", "일"];
    private static readonly DayOfWeek[] WeekdayOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>One card per daily content: on/off, sweep count, and per-weekday sub-trial choice.</summary>
    private UIElement BuildDailyCard(DailyContent c)
    {
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
        string desc = c.Kind == DailyKind.Battle
            ? "전투 (마지막 스테이지, 기존 배치 그대로) · 하루 1회"
            : c.CountConfigurable ? "마지막 클리어 스테이지 소탕" : "마지막 클리어 스테이지 소탕 · 하루 1회";

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);

        Wpf.Ui.Controls.TextBox? count = null;
        if (c.CountConfigurable)
        {
            count = new Wpf.Ui.Controls.TextBox { Width = 70, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 16, 0) };
            var countPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            countPanel.Children.Add(new TextBlock { Text = "횟수", Style = (Style)FindResource("RowDesc"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            countPanel.Children.Add(count);
            DockPanel.SetDock(countPanel, Dock.Right);
            header.Children.Add(countPanel);
        }

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = c.Name, Style = (Style)FindResource("RowTitle") });
        titles.Children.Add(new TextBlock { Text = desc, Style = (Style)FindResource("RowDesc") });
        header.Children.Add(titles);

        var body = new StackPanel();
        body.Children.Add(header);

        ComboBox[]? weekdays = null;
        if (c.HasSub)
        {
            weekdays = new ComboBox[7];
            var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 12, 0, 0) };
            var options = new[] { "건너뜀" }.Concat(c.SubNames).ToArray();
            for (int i = 0; i < 7; i++)
            {
                var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                cell.Children.Add(new TextBlock
                {
                    Text = WeekdayNames[i], Style = (Style)FindResource("RowDesc"),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
                });
                var combo = new ComboBox { ItemsSource = options, MinWidth = 0, Padding = new Thickness(8, 4, 4, 4) };
                weekdays[i] = combo;
                cell.Children.Add(combo);
                grid.Children.Add(cell);
            }
            body.Children.Add(grid);
        }

        dailyRows.Add(new DailyRow(c, toggle, count, weekdays));
        return new Wpf.Ui.Controls.Card { Content = body, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4) };
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartMacro(RunMode.Journey);

    private void StartDaily_Click(object sender, RoutedEventArgs e) => StartMacro(RunMode.Daily);

    private void Stop_Click(object sender, RoutedEventArgs e) => StopMacro();

    private bool checkingLicense;

    /// <summary>Re-checks the license with the server before every run, so an expired trial can't keep going.</summary>
    private async void StartMacro(RunMode mode)
    {
        if (running != null || checkingLicense) return;
        checkingLicense = true;
        try
        {
            // a deleted key fails here with "등록되지 않은 라이센스 키입니다"
            if (await RefreshLicense() == null) return;
        }
        finally { checkingLicense = false; }
        StartMacroNow(mode);
    }

    private void StartMacroNow(RunMode mode)
    {
        if (running != null) return;
        if (mode == RunMode.Daily && !licenseStatus.IsPro)
        {
            ShowAlert("일일퀘스트와 글로리 전장은 프로 키에서만 사용할 수 있습니다.");
            return;
        }
        ReadFromUi();
        settings.Save();

        OcrService ocr;
        Vision vision;
        try
        {
            ocr = new OcrService();
            vision = new Vision();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "시작할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        cts = new CancellationTokenSource();
        var ctx = new MacroContext(new GameWindow(), ocr, vision, settings, log, cts.Token);
        ctx.BlockRequested += block => Dispatcher.Invoke(() => SetBlocking(block));
        ctx.StatusChanged += s => Dispatcher.BeginInvoke(() => StepText.Text = s);
        ctx.StaminaRead += v => Dispatcher.BeginInvoke(() => StaminaText.Text = $"{v}/240");
        ctx.AlertRaised += msg => Dispatcher.BeginInvoke(() => ShowAlert(msg));
        AlertBar.IsOpen = false;
        AlertBar.Message = "";
        var runner = new MacroRunner(ctx, allowDaily: licenseStatus.IsPro);
        runner.RoundCompleted += n => Dispatcher.BeginInvoke(() => RoundText.Text = $"{n}회");

        RoundText.Text = "0회";
        startedAt = DateTime.Now;
        clock.Start();
        SetState(mode == RunMode.Daily ? "일일퀘스트 실행 중" : "실행 중", Running, "게임 화면을 확인하는 중");
        SetRunningUi(true);
        log.Info($"매크로 시작 - {(mode == RunMode.Daily ? "일일퀘스트" : "탐험의 여정")} (정지: F10)");
        // keep our window from covering the game while it is captured and clicked
        WindowState = WindowState.Minimized;

        running = Task.Run(() =>
        {
            try
            {
                runner.Run(mode);
                return "완료";
            }
            catch (OperationCanceledException) { return "정지됨"; }
            catch (MacroStopException ex) { log.Info(ex.Message); return "중단됨"; }
            catch (Exception ex) { log.Info($"오류: {ex}"); return "오류로 중단됨"; }
            finally { vision.Dispose(); }
        });
        running.ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            SetBlocking(false);
            SetRunningUi(false);
            clock.Stop();
            string result = t.Result;
            SetState(result, result is "완료" or "정지됨" ? Idle : Failed,
                result is "완료" or "정지됨" ? "F9로 다시 시작할 수 있습니다." : "로그에서 원인을 확인해 주세요.");
            log.Info($"매크로 {result}");
            running = null;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }));
    }

    private void StopMacro()
    {
        cts?.Cancel();
        SetBlocking(false);
    }

    private void SetBlocking(bool block)
    {
        if (block && settings.BlockInput && running != null) blocker.Enable();
        else blocker.Disable();
    }

    private void SetState(string status, Brush color, string step)
    {
        StatusText.Text = status;
        SideStatus.Text = status;
        StatusDot.Fill = color;
        SideDot.Fill = color;
        StepText.Text = step;
    }

    private void SetRunningUi(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        DailyButton.IsEnabled = !isRunning && licenseStatus.IsPro;
        StopButton.IsEnabled = isRunning;
    }

    private void ShowAlert(string message)
    {
        var line = $"[{DateTime.Now:HH:mm}] {message}";
        AlertBar.Message = string.IsNullOrEmpty(AlertBar.Message) ? line : AlertBar.Message + Environment.NewLine + line;
        AlertBar.IsOpen = true;
    }

    private void AppendLog(string line)
    {
        LogList.Items.Add(line);
        while (LogList.Items.Count > MaxLogLines) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(line);
        recent.Add(line);
        while (recent.Count > RecentLines) recent.RemoveAt(0);
        RecentEmpty.Visibility = Visibility.Collapsed;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "XD.exe|XD.exe|실행 파일|*.exe", FileName = OfficialPath.Text };
        if (dlg.ShowDialog(this) == true) OfficialPath.Text = dlg.FileName;
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var game = new GameWindow();
        if (!game.Find())
        {
            DetectText.Text = "게임 창을 찾지 못했습니다. 게임을 켠 뒤 다시 눌러 주세요.";
            return;
        }
        var (_, _, w, h) = game.ClientRect();
        string ratio = Math.Abs(w / (double)h - 16.0 / 9) < 0.03 ? "16:9" : "16:9 아님 - 버튼 위치가 어긋날 수 있음";
        DetectText.Text = $"게임 화면 {w}x{h} ({ratio}), Windows 배율 {game.Dpi * 100 / 96}%";
    }

    private void LoadToUi()
    {
        var s = settings;
        LaunchOfficial.IsChecked = s.Launch == LaunchMode.Official;
        LaunchSteam.IsChecked = s.Launch == LaunchMode.Steam;
        OfficialPath.Text = s.OfficialPath;
        AutoLaunch.IsChecked = s.AutoLaunch;
        Display.SelectedIndex = (int)s.Display;
        Resolution.Text = s.Resolution;
        BlockInput.IsChecked = s.BlockInput;

        JourneyEnabled.IsChecked = s.JourneyEnabled;
        Stage.SelectedIndex = Math.Clamp(s.Stage, 1, 5) - 1;
        Genre.SelectedIndex = (int)s.Genre;
        MaxRounds.Text = s.MaxRounds.ToString();
        UseAdvancedReward.IsChecked = s.UseAdvancedReward;
        BlessingKeywords.Text = string.Join(Environment.NewLine, s.BlessingKeywords);
        BlessingExcludes.Text = string.Join(Environment.NewLine, s.BlessingExcludeKeywords);
        BlessingFallback.SelectedIndex = Math.Clamp(s.BlessingFallback, 1, 3) - 1;
        MaxLosses.Text = s.MaxLossesPerNode.ToString();
        Swaps.Text = s.SwapsOnRetry.ToString();
        UseStaminaItem.IsChecked = s.UseStaminaItem;
        GemLimit.SelectedIndex = Math.Clamp(s.GemRefillLimit, 0, 4);
        UnblockWhileWaiting.IsChecked = s.UnblockWhileWaiting;
        ClaimLevelRewards.IsChecked = s.ClaimLevelRewards;
        BuySupplyShop.IsChecked = s.BuySupplyShop;

        foreach (var row in dailyRows)
        {
            row.Enabled.IsChecked = s.DailyContents.GetValueOrDefault(row.Content.Name);
            if (row.Count != null) row.Count.Text = s.DailyCount(row.Content.Name).ToString();
            if (row.Weekdays != null)
                for (int i = 0; i < 7; i++) row.Weekdays[i].SelectedIndex = s.DailyPick(row.Content, WeekdayOrder[i]);
        }
        DailyAfterReset.IsChecked = s.DailyAfterReset;
        UseOctopusCan.IsChecked = s.UseOctopusCan;

        ScheduleEnabled.IsChecked = s.ScheduleEnabled;
        if (!Schedule.TryParseTime(s.ScheduleTime, out var schedTime)) schedTime = new TimeSpan(0, 10, 0);
        ScheduleHour.SelectedIndex = schedTime.Hours;
        ScheduleMinute.SelectedIndex = schedTime.Minutes;
        ScheduleModeDaily.IsEnabled = licenseStatus.IsPro; // basic keys: 탐험의 여정 only
        ScheduleMode.SelectedIndex = s.ScheduleMode == RunMode.Daily && licenseStatus.IsPro ? 1 : 0;
        for (int i = 0; i < 7; i++)
            scheduleDayBoxes[i].IsChecked = s.ScheduleDays is { Length: 7 } d ? d[(int)WeekdayOrder[i]] : true;
        GloryEnabled.IsChecked = s.GloryEnabled;
        GloryDifficulty.SelectedIndex = Math.Clamp(s.GloryDifficulty, 0, 2);
        GloryCount.SelectedIndex = Math.Clamp(s.GloryCount, 1, GloryBot.DailyLimit) - 1;
    }

    private void ReadFromUi()
    {
        var s = settings;
        s.Launch = LaunchSteam.IsChecked == true ? LaunchMode.Steam : LaunchMode.Official;
        s.OfficialPath = OfficialPath.Text.Trim();
        s.AutoLaunch = AutoLaunch.IsChecked == true;
        s.Display = (DisplayMode)Math.Max(0, Display.SelectedIndex);
        s.Resolution = MacroRunner.TryParseSize(Resolution.Text, out _, out _) ? Resolution.Text.Trim() : "자동";
        s.BlockInput = BlockInput.IsChecked == true;

        s.JourneyEnabled = JourneyEnabled.IsChecked == true;
        s.Stage = Stage.SelectedIndex + 1;
        s.Genre = (Settings.Genre)Math.Max(0, Genre.SelectedIndex);
        s.MaxRounds = ParseInt(MaxRounds.Text, 0, 0, 9999);
        s.UseAdvancedReward = UseAdvancedReward.IsChecked == true;
        s.BlessingKeywords = Lines(BlessingKeywords.Text);
        s.BlessingExcludeKeywords = Lines(BlessingExcludes.Text);
        s.BlessingFallback = BlessingFallback.SelectedIndex + 1;
        s.MaxLossesPerNode = ParseInt(MaxLosses.Text, 3, 1, 99);
        s.SwapsOnRetry = ParseInt(Swaps.Text, 2, 1, 3);
        s.UseStaminaItem = UseStaminaItem.IsChecked == true;
        s.GemRefillLimit = Math.Max(0, GemLimit.SelectedIndex);
        s.UnblockWhileWaiting = UnblockWhileWaiting.IsChecked == true;
        s.ClaimLevelRewards = ClaimLevelRewards.IsChecked == true;
        s.BuySupplyShop = BuySupplyShop.IsChecked == true;

        foreach (var row in dailyRows)
        {
            string name = row.Content.Name;
            s.DailyContents[name] = row.Enabled.IsChecked == true;
            if (row.Count != null) s.DailyCounts[name] = ParseInt(row.Count.Text, 1, 1, 99);
            if (row.Weekdays != null)
            {
                var picks = new int[7];
                for (int i = 0; i < 7; i++) picks[(int)WeekdayOrder[i]] = Math.Max(0, row.Weekdays[i].SelectedIndex);
                s.DailyWeekdayPicks[name] = picks;
            }
        }
        s.DailyAfterReset = DailyAfterReset.IsChecked == true;
        s.UseOctopusCan = UseOctopusCan.IsChecked == true;

        // ScheduleLastRun is written only when the schedule fires
        s.ScheduleEnabled = ScheduleFromUi(out var schedTime, out var schedDays, out var schedMode);
        s.ScheduleTime = schedTime.ToString(@"hh\:mm");
        s.ScheduleDays = schedDays;
        s.ScheduleMode = schedMode;
        s.GloryEnabled = GloryEnabled.IsChecked == true;
        s.GloryDifficulty = Math.Max(0, GloryDifficulty.SelectedIndex);
        s.GloryCount = GloryCount.SelectedIndex + 1;
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out int v) ? Math.Clamp(v, min, max) : fallback;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        cts?.Cancel();
        blocker.Dispose();
        ReadFromUi();
        settings.Save();
        base.OnClosing(e);
    }
}
