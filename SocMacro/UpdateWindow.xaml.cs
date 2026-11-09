using System.Diagnostics;
using System.Windows;
using SocMacro.Updates;

namespace SocMacro;

public partial class UpdateWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateService service;
    private readonly AvailableUpdate update;
    private readonly CancellationTokenSource cancellation = new();
    private DownloadedUpdate? downloaded;
    private bool closed;

    public UpdateWindow(UpdateService service, AvailableUpdate update)
    {
        InitializeComponent();
        this.service = service;
        this.update = update;
        VersionText.Text = $"새 버전 {update.Version.ToString(3)} 업데이트가 필요합니다";
        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes) ? "개선된 새 버전을 사용할 수 있습니다." : update.Notes;
        Loaded += async (_, _) => await DownloadAsync();
        Closed += (_, _) => { closed = true; cancellation.Cancel(); };
    }

    private async Task DownloadAsync()
    {
        ActionButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "프로그램을 사용하려면 업데이트가 필요합니다. 새 버전을 다운로드하는 중...";
        var progress = new Progress<double>(value =>
        {
            if (closed || downloaded != null) return;
            DownloadProgress.Value = value;
            StatusText.Text = $"새 버전 다운로드 중... {value:0}%";
        });
        try
        {
            downloaded = await service.DownloadAsync(update, progress, cancellation.Token);
            if (closed) return;
            DownloadProgress.Value = 100;
            StatusText.Text = "다운로드와 파일 검증이 완료되었습니다. 새 버전으로 다시 시작해 주세요.";
            ActionButton.Content = UpdateInstaller.CanInstall ? "업데이트 후 다시 시작" : "다운로드 폴더 열기";
            ActionButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closed) return;
            StatusText.Text = $"다운로드 실패: {ex.Message}\n업데이트를 완료해야 프로그램을 사용할 수 있습니다. 다시 시도해 주세요.";
            ActionButton.Content = "다시 시도";
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (downloaded == null) { await DownloadAsync(); return; }
        try
        {
            if (!UpdateInstaller.CanInstall)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetDirectoryName(downloaded.Path)! } });
                return;
            }
            ActionButton.IsEnabled = false;
            UpdateInstaller.Start(downloaded);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"업데이트 설치를 시작하지 못했습니다: {ex.Message}";
            ActionButton.IsEnabled = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
