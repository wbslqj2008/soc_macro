using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SocMacro.Updates;

public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size);

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("body")] string? Notes,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets);

public sealed record AvailableUpdate(Version Version, string Notes, ReleaseAsset Exe, ReleaseAsset Checksum);
public sealed record DownloadedUpdate(string Path, string Sha256);

/// <summary>Public release assets only: no GitHub credential is distributed with the app.</summary>
public sealed class UpdateService(HttpClient http, string repository, string downloadRoot)
{
    public const string DefaultRepository = "wbslqj2008/soc_macro";
    private const long MaxDownloadSize = 512L * 1024 * 1024;

    public static string Repository => typeof(UpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? DefaultRepository;

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
    public static string DownloadRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocMacro", "Updates");

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocMacro-Updater/1.0");
        return client;
    }

    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("업데이트 저장소 주소가 올바르지 않습니다.");
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct)
            ?? throw new InvalidDataException("업데이트 정보를 읽을 수 없습니다.");
        if (release.Draft || release.Prerelease)
            throw new InvalidDataException("정식 업데이트 버전을 확인할 수 없습니다.");
        if (!Version.TryParse(release.Tag?.TrimStart('v', 'V'), out var version))
            throw new InvalidDataException("업데이트 버전이 올바르지 않습니다.");
        version = Normalize(version);
        if (version <= Normalize(current)) return null;
        var exe = release.Assets?.FirstOrDefault(a => a.Name == "SocMacro.exe");
        var checksum = release.Assets?.FirstOrDefault(a => a.Name == "SocMacro.exe.sha256");
        if (exe == null || checksum == null)
            throw new InvalidDataException("업데이트 실행 파일 또는 검증 파일이 누락되었습니다.");
        ValidateUrl(exe.DownloadUrl);
        ValidateUrl(checksum.DownloadUrl);
        if (exe.Size <= 0 || exe.Size > MaxDownloadSize || checksum.Size <= 0 || checksum.Size > 1024)
            throw new InvalidDataException("업데이트 파일 크기가 올바르지 않습니다.");
        return new AvailableUpdate(version, release.Notes ?? "", exe, checksum);
    }

    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken ct)
    {
        ValidateUrl(update.Exe.DownloadUrl);
        ValidateUrl(update.Checksum.DownloadUrl);
        using var checksumResponse = await http.GetAsync(update.Checksum.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        checksumResponse.EnsureSuccessStatusCode();
        await checksumResponse.Content.LoadIntoBufferAsync(1024, ct);
        var checksumText = await checksumResponse.Content.ReadAsStringAsync(ct);
        var hash = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("업데이트 검증 정보가 올바르지 않습니다.");

        var folder = Path.Combine(downloadRoot, update.Version.ToString(3));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "SocMacro.exe");
        if (File.Exists(target) && new FileInfo(target).Length == update.Exe.Size && await HasHashAsync(target, hash, ct))
        {
            progress.Report(100);
            return new DownloadedUpdate(target, hash);
        }
        // Each process owns its partial file; another app instance cannot install a partial download.
        var partial = Path.Combine(folder, $"{Guid.NewGuid():N}.partial");
        try
        {
            using var response = await http.GetAsync(update.Exe.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Exe.Size)
                throw new InvalidDataException("업데이트 파일 크기가 일치하지 않습니다.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += count;
                    if (total > update.Exe.Size || total > MaxDownloadSize)
                        throw new InvalidDataException("업데이트 파일 크기가 예상보다 큽니다.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress.Report(total * 100d / update.Exe.Size);
                }
                if (total != update.Exe.Size) throw new InvalidDataException("다운로드가 완료되지 않았습니다.");
            }
            if (!await HasHashAsync(partial, hash, ct))
                throw new InvalidDataException("업데이트 파일 검증에 실패했습니다. 다시 시도해 주세요.");
            File.Move(partial, target, overwrite: true);
            progress.Report(100);
            return new DownloadedUpdate(target, hash);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static async Task<bool> HasHashAsync(string path, string hash, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("허용되지 않은 업데이트 다운로드 주소입니다.");
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
