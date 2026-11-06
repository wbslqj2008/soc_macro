using System.Diagnostics;
using System.Text.Json;

namespace SocMacro.Updates;

public sealed record InstallRequest(string Source, string Target, string Sha256, int ParentId, long ParentStarted);

public static class UpdateInstaller
{
    // A copied single-file app acts as the helper so no PowerShell command interpolation is needed.
    public static bool CanInstall => string.IsNullOrEmpty(typeof(UpdateInstaller).Assembly.Location);

    public static void Start(DownloadedUpdate update)
    {
        if (!CanInstall) throw new InvalidOperationException("개발 빌드에서는 자동 설치를 지원하지 않습니다.");
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("실행 경로를 확인할 수 없습니다.");
        var folder = Path.Combine(UpdateService.DownloadRoot, "installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var helper = Path.Combine(folder, "SocMacro.Updater.exe");
        File.Copy(target, helper);
        using var parent = Process.GetCurrentProcess();
        var request = new InstallRequest(update.Path, target, update.Sha256, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        var requestPath = Path.Combine(folder, "install.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo(helper) { UseShellExecute = true, WorkingDirectory = folder };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(requestPath);
        if (Process.Start(start) == null) throw new IOException("업데이트 설치 도우미를 시작하지 못했습니다.");
    }

    public static async Task ApplyAsync(string requestPath)
    {
        var helperFolder = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (!CanInstall || !Path.GetFullPath(requestPath).Equals(Path.Combine(helperFolder, "install.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 설치 요청 경로가 올바르지 않습니다.");
        var request = JsonSerializer.Deserialize<InstallRequest>(await File.ReadAllTextAsync(requestPath))
            ?? throw new InvalidDataException("업데이트 설치 정보를 읽을 수 없습니다.");
        var source = Path.GetFullPath(request.Source);
        var target = Path.GetFullPath(request.Target);
        if (!source.StartsWith(Path.GetFullPath(UpdateService.DownloadRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(source).Equals("SocMacro.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).Equals("SocMacro.exe", StringComparison.OrdinalIgnoreCase) || source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 설치 경로가 올바르지 않습니다.");
        if (!await UpdateService.HasHashAsync(source, request.Sha256, CancellationToken.None))
            throw new InvalidDataException("업데이트 파일이 변경되어 설치를 중단했습니다.");

        Process? parent = null;
        try { parent = Process.GetProcessById(request.ParentId); }
        catch (ArgumentException) { } // parent has already exited
        using (parent)
        {
            bool waitForParent = false;
            try { waitForParent = parent != null && !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted; }
            catch (InvalidOperationException) { } // it exited while inspecting its start time
            if (waitForParent)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await parent!.WaitForExitAsync(timeout.Token);
            }
        }
        // The original remains available if copy/replace fails. Keep a backup after a successful update.
        var backup = ReplaceFile(source, target);
        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! };
            if (Process.Start(start) == null) throw new IOException("새 버전을 시작하지 못했습니다.");
        }
        catch
        {
            File.Replace(backup, target, null);
            throw;
        }
    }

    public static string ReplaceFile(string source, string target)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staged = target + ".update-" + suffix;
        var backup = target + ".previous-" + suffix;
        try
        {
            File.Copy(source, staged, overwrite: false);
            File.Replace(staged, target, backup);
            return backup;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
