using System.Text;
using System.Windows;
using OpenCvSharp;
using SocMacro.Core;
using SocMacro.License;

namespace SocMacro;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now}] {args.Exception}{Environment.NewLine}");
            MessageBox.Show($"예기치 못한 오류가 발생했습니다.\n{args.Exception.Message}\n\n자세한 내용: {path}", "소오콘 매크로",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        // diagnostics: SocMacro.exe --dump shot.png  → shot.png.txt with OCR lines and boxes (base coords)
        if (e.Args.Length >= 2 && e.Args[0] == "--dump")
        {
            var sb = new StringBuilder();
            var ocr = new OcrService();
            using var vision = new Vision();
            foreach (var path in e.Args.Skip(1))
            {
                using var img = Cv2.ImRead(path, ImreadModes.Color);
                using var f = new Frame(img.Clone(), img.Width / (double)GameWindow.BaseW, img.Height / (double)GameWindow.BaseH, ocr);
                var lines = f.Lines;
                sb.AppendLine($"# {path} hud={vision.BattleHud(f)} defeat={vision.IsDefeatScreen(f)} eventNode={vision.FindActiveEventNode(f)} allies={Vision.FindAllies(f).Count} angle={ocr.LastTextAngle:0.00}");
                foreach (var l in lines) sb.AppendLine($"{l.Box}\t{l.Norm}");
                File.WriteAllText(path + ".txt", sb.ToString());
                sb.Clear();
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);

        var license = new LicenseService();
        var login = new LoginWindow(license);
        if (login.ShowDialog() != true || login.Status == null)
        {
            Shutdown();
            return;
        }
        var main = new MainWindow(license, login.Status);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
