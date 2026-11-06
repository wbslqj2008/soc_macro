using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using SocMacro.License;

namespace SocMacro;

/// <summary>Asks for a license key and checks it. Closes with DialogResult = true when the app may be used.</summary>
public partial class LoginWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly LicenseService license;

    public LicenseStatus? Status { get; private set; }

    public LoginWindow(LicenseService license)
    {
        InitializeComponent();
        this.license = license;
        Loaded += async (_, _) =>
        {
            if (license.SavedKey() is { } saved)
            {
                KeyBox.Text = saved;
                await TryKey(saved, remember: true);
            }
        };
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await TryKey(KeyBox.Text, RememberBox.IsChecked == true);

    private async void Key_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryKey(KeyBox.Text, RememberBox.IsChecked == true);
    }

    private async Task TryKey(string key, bool remember)
    {
        Message.Text = "";
        KeyPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        try
        {
            var status = await license.Login(key);
            if (remember) license.RememberKey();
            else license.ForgetKey();
            Status = status;
            DialogResult = true;
        }
        catch (LicenseException ex)
        {
            Message.Text = ex.Message;
            license.ForgetKey(); // e.g. a remembered key that was deleted
        }
        catch (HttpRequestException) { Message.Text = "서버에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요."; }
        catch (TaskCanceledException) { Message.Text = "서버 응답이 없습니다. 잠시 후 다시 시도해 주세요."; }
        finally
        {
            if (Status == null)
            {
                KeyPanel.Visibility = Visibility.Visible;
                BusyPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
