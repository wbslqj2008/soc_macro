using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Loader;
using SocMacro.Updates;

var root = Path.Combine(Path.GetTempPath(), "SocMacro-UpdateChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var bytes = Encoding.UTF8.GetBytes("A simulated new release executable");
var hash = Convert.ToHexString(SHA256.HashData(bytes));
var folder = "https://github.com/example/releases/releases/download/v1.2.3/";
GitHubRelease Release(string tag = "v1.2.3", bool prerelease = false, string? url = null) => new(tag, "Release notes", false, prerelease,
    [new("SocMacro.exe", url ?? folder + "SocMacro.exe", bytes.Length), new("SocMacro.exe.sha256", folder + "SocMacro.exe.sha256", 79)]);
AvailableUpdate Update() => new(new Version(1, 2, 3, 0), "", Release().Assets[0], Release().Assets[1]);
var progress = new InlineProgress();

async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected rejection");
}
HttpClient Client(GitHubRelease release, string? digest = null, byte[]? payload = null) => new(new Handler(request =>
{
    if (request.RequestUri!.Host == "api.github.com")
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release)) };
    return new(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath.EndsWith(".sha256")
        ? new StringContent((digest ?? hash) + "  SocMacro.exe")
        : new ByteArrayContent(payload ?? bytes) };
}));

try
{
    await Check("new version detected; same/older versions ignored", async () =>
    {
        using var http = Client(Release());
        var service = new UpdateService(http, "example/releases", root);
        Assert(await service.CheckAsync(new Version(1, 2, 2), default) != null);
        Assert(await service.CheckAsync(new Version(1, 2, 3), default) == null);
        Assert(await service.CheckAsync(new Version(2, 0, 0), default) == null);
    });
    await Check("non-public releases cannot bypass required version check", async () =>
    {
        foreach (var release in new[] { Release(prerelease: true), Release() with { Draft = true } })
        {
            using var http = Client(release);
            await Reject(async () => { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), default); });
        }
    });
    await Check("missing release, rate limit and server errors cannot bypass required version check", async () =>
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            using var http = new HttpClient(new Handler(_ => new(status)));
            try { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), default); }
            catch (HttpRequestException ex) when (ex.StatusCode == status) { continue; }
            throw new Exception("Failed version check was not rejected: " + status);
        }
    });
    await Check("network failure cannot bypass required version check", async () =>
    {
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        try { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), default); }
        catch (HttpRequestException) { return; }
        throw new Exception("Network failure was not propagated");
    });
    await Check("canceled version check cannot allow startup", async () =>
    {
        using var http = Client(Release());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), cancel.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Version check cancellation was not propagated");
    });
    await Check("foreign URL rejected", async () =>
    {
        using var http = Client(Release(url: "https://example.com/malicious.exe"));
        await Reject(async () => { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), default); });
    });
    await Check("missing verification asset rejected", async () =>
    {
        using var http = Client(Release() with { Assets = [Release().Assets[0]] });
        await Reject(async () => { await new UpdateService(http, "example/releases", root).CheckAsync(new Version(1, 0), default); });
    });
    await Check("download, hash, progress and cached download", async () =>
    {
        using var http = Client(Release());
        var service = new UpdateService(http, "example/releases", root);
        var file = await service.DownloadAsync(Update(), progress, default);
        Assert(File.ReadAllBytes(file.Path).SequenceEqual(bytes) && progress.Value == 100);
        Assert((await service.DownloadAsync(Update(), progress, default)).Path == file.Path);
    });
    await Check("tampered cache replaced", async () =>
    {
        var target = Path.Combine(root, "1.2.3", "SocMacro.exe");
        File.WriteAllBytes(target, Enumerable.Repeat((byte)0, bytes.Length).ToArray());
        using var http = Client(Release());
        var result = await new UpdateService(http, "example/releases", root).DownloadAsync(Update(), progress, default);
        Assert(File.ReadAllBytes(result.Path).SequenceEqual(bytes));
    });
    await Check("hash mismatch preserves previous download and removes partial file", async () =>
    {
        using var http = Client(Release(), new string('0', 64));
        await Reject(async () => { await new UpdateService(http, "example/releases", root).DownloadAsync(Update(), progress, default); });
        Assert(!Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any());
        Assert(File.ReadAllBytes(Path.Combine(root, "1.2.3", "SocMacro.exe")).SequenceEqual(bytes));
    });
    await Check("truncated transfer rejected", async () =>
    {
        using var http = Client(Release(), new string('1', 64), [1, 2]);
        await Reject(async () => { await new UpdateService(http, "example/releases", root).DownloadAsync(Update(), progress, default); });
    });
    await Check("cancellation propagates", async () =>
    {
        using var http = Client(Release());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await new UpdateService(http, "example/releases", root).DownloadAsync(Update(), progress, cancel.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Cancellation not propagated");
    });
    await Check("replacement backs up original and leaves other files intact", () =>
    {
        var target = Path.Combine(root, "installed.exe");
        var source = Path.Combine(root, "new.exe");
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "new");
        File.WriteAllText(settings, "settings");
        var backup = UpdateInstaller.ReplaceFile(source, target);
        Assert(File.ReadAllText(target) == "new" && File.ReadAllText(backup) == "old");
        Assert(File.ReadAllText(settings) == "settings");
        return Task.CompletedTask;
    });
    await Check("failed replacement preserves original", () =>
    {
        var target = Path.Combine(root, "installed.exe");
        try { UpdateInstaller.ReplaceFile(Path.Combine(root, "missing.exe"), target); }
        catch (FileNotFoundException) { Assert(File.ReadAllText(target) == "new"); return Task.CompletedTask; }
        throw new Exception("Expected failed copy");
    });
    if (args.Length == 1)
    {
        await Check("published obfuscated DTOs deserialize", () =>
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
            var releaseType = assembly.GetType("SocMacro.Updates.GitHubRelease", throwOnError: true)!;
            var release = JsonSerializer.Deserialize(JsonSerializer.Serialize(Release()), releaseType)!;
            Assert((string?)releaseType.GetProperty("Tag")!.GetValue(release) == "v1.2.3");
            var requestType = assembly.GetType("SocMacro.Updates.InstallRequest", throwOnError: true)!;
            var request = JsonSerializer.Deserialize("{\"Source\":\"source\",\"Target\":\"target\",\"Sha256\":\"hash\",\"ParentId\":1,\"ParentStarted\":2}", requestType)!;
            Assert((string?)requestType.GetProperty("Source")!.GetValue(request) == "source");
            return Task.CompletedTask;
        });
    }
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    // This unique directory was created by this process and contains test fixtures only.
    if (!Path.GetFileName(root).StartsWith("SocMacro-UpdateChecks-")) throw new Exception("Invalid test cleanup path");
    Directory.Delete(root, recursive: true);
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
sealed class InlineProgress : IProgress<double>
{
    public double Value { get; private set; }
    public void Report(double value) => Value = value;
}
