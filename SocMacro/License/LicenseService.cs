using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocMacro.License;

/// <summary>Basic = 탐험의 여정 only; Pro = everything.</summary>
public enum LicenseTier { Basic, Pro }

/// <summary>A valid key. Admin keys (set only from the owner side) unlock the admin page.</summary>
public sealed record LicenseStatus(LicenseTier Tier, bool IsAdmin)
{
    public bool IsPro => Tier == LicenseTier.Pro;
    public string Describe() => IsAdmin ? "관리자" : IsPro ? "프로" : "베이직";
}

public sealed record LicenseKeyInfo(string Key, string Tier, bool Admin, DateTime CreatedAt, DateTime? LastSeen, string Note);
public sealed class LicenseException(string message) : Exception(message);

/// <summary>
/// The license key is the only credential: the app reads ids/{key} and stamps server time on it.
/// Everything a client may change is limited by server/firestore.rules.
/// </summary>
public sealed class LicenseService
{
    // public web API key (safe to ship; access is controlled by the security rules)
    private const string ApiKey = "AIzaSyA61nT57Dn5NmRcauNPoMj3CeCQrjbSnow";
    public const string ProjectId = "soc-macro-aq12z";

    internal const string Docs = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents";
    internal const string DocName = $"projects/{ProjectId}/databases/(default)/documents";
    private static readonly string SessionPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SocMacro", "license.bin");

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string Key { get; private set; } = "";

    /// <summary>Validate a key against the server. Throws LicenseException if it doesn't exist.</summary>
    public async Task<LicenseStatus> Login(string key)
    {
        key = NormalizeKey(key);
        if (key.Length < 4) throw new LicenseException("라이센스 키를 입력해 주세요.");
        var doc = await Get(key) ?? throw new LicenseException("등록되지 않은 라이센스 키입니다.");
        Key = key;

        // "last used" for the admin list; failure here shouldn't block a valid key
        try { await Commit(Update(key, [], [], "lastSeen")); } catch (LicenseException) { }

        bool admin = (bool?)doc["fields"]?["admin"]?["booleanValue"] ?? false;
        var tier = admin || Str(doc, "tier") == "pro" ? LicenseTier.Pro : LicenseTier.Basic;
        return new LicenseStatus(tier, admin);
    }

    /// <summary>Re-check the current key (before each macro run). A deleted key stops working here.</summary>
    public Task<LicenseStatus> Check() => Login(Key);

    // ───────────────────────── remembered key ─────────────────────────

    public string? SavedKey()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException) { return null; }
    }

    public void RememberKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllBytes(SessionPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(Key), null, DataProtectionScope.CurrentUser));
    }

    public void ForgetKey()
    {
        try { File.Delete(SessionPath); } catch (IOException) { }
    }

    // ───────────────────────── Firestore REST (unauthenticated, rule-limited) ─────────────────────────

    private async Task<JsonNode?> Get(string key)
    {
        var resp = await http.GetAsync($"{Docs}/ids/{Uri.EscapeDataString(key)}?key={ApiKey}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await ReadJson(resp);
    }

    private async Task Commit(params JsonObject[] writes) =>
        await ReadJson(await http.PostAsync($"{Docs}:commit?key={ApiKey}",
            JsonContent.Create(new JsonObject { ["writes"] = new JsonArray(writes) })));

    internal static JsonObject Update(string key, Dictionary<string, JsonNode> fields, string[] mask, params string[] serverTimes)
    {
        var fieldObj = new JsonObject();
        foreach (var (k, v) in fields) fieldObj[k] = v;
        var w = new JsonObject
        {
            ["update"] = new JsonObject { ["name"] = $"{DocName}/ids/{key}", ["fields"] = fieldObj },
            ["updateMask"] = new JsonObject { ["fieldPaths"] = new JsonArray(mask.Select(m => (JsonNode)m).ToArray()) },
            ["currentDocument"] = new JsonObject { ["exists"] = true },
        };
        if (serverTimes.Length > 0)
            w["updateTransforms"] = new JsonArray(serverTimes
                .Select(f => (JsonNode)new JsonObject { ["fieldPath"] = f, ["setToServerValue"] = "REQUEST_TIME" }).ToArray());
        return w;
    }

    internal static JsonNode Str(string s) => new JsonObject { ["stringValue"] = s };
    internal static JsonNode Null() => new JsonObject { ["nullValue"] = null };

    internal static string? Str(JsonNode doc, string name) => (string?)doc["fields"]?[name]?["stringValue"];

    internal static DateTime? Time(JsonNode doc, string name) =>
        doc["fields"]?[name]?["timestampValue"] is { } t ? DateTime.Parse((string)t!).ToUniversalTime() : null;

    internal static async Task<JsonNode> ReadJson(HttpResponseMessage resp)
    {
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string status = "";
            try { status = (string?)JsonNode.Parse(text)?["error"]?["status"] ?? ""; } catch (JsonException) { }
            throw new LicenseException(status switch
            {
                "PERMISSION_DENIED" => "서버가 요청을 거부했습니다. (PERMISSION_DENIED)",
                "RESOURCE_EXHAUSTED" => "서버 사용량 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
                _ => $"서버 오류 ({(int)resp.StatusCode} {status})",
            });
        }
        return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)!;
    }

    // ───────────────────────── keys ─────────────────────────

    private const string KeyAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // no 0/O, 1/I

    public static string NewKey()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Upper-case and drop spaces/dashes; any 12-character letter/digit key becomes XXXX-XXXX-XXXX,
    /// so "7771 7771 7771", "777177717771" and "7771-7771-7771" are the same key.
    /// </summary>
    public static string NormalizeKey(string key)
    {
        string raw = new string(key.ToUpperInvariant().Where(c => c is not (' ' or '-' or '\t')).ToArray());
        if (raw.Length == 12 && raw.All(char.IsAsciiLetterOrDigit))
            return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
        return raw.Replace("/", "");
    }
}

/// <summary>
/// Admin operations with the project owner's Google credentials from the local Firebase CLI login.
/// These bypass the security rules, so they only work on the seller's PC.
/// </summary>
public sealed class LicenseAdmin
{
    private static readonly string CliConfig =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "configstore", "firebase-tools.json");

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool Available => File.Exists(CliConfig);

    internal static string PrepareNewKey(string? customKey)
    {
        string key = string.IsNullOrWhiteSpace(customKey) ? LicenseService.NewKey() : LicenseService.NormalizeKey(customKey);
        int length = key.Count(c => c != '-');
        if (length < 4) throw new LicenseException("키는 공백과 하이픈을 제외하고 4자 이상이어야 합니다.");
        if (length > 16) throw new LicenseException("키는 공백과 하이픈을 제외하고 최대 16자까지 지정할 수 있습니다.");
        return key;
    }

    public async Task<string> CreateKey(string? customKey, LicenseTier tier, string note)
    {
        // Validate before building a request or accessing owner credentials.
        string key = PrepareNewKey(customKey);
        var write = new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["name"] = $"{LicenseService.DocName}/ids/{key}",
                ["fields"] = new JsonObject
                {
                    ["tier"] = LicenseService.Str(tier == LicenseTier.Pro ? "pro" : "basic"),
                    ["note"] = LicenseService.Str(note),
                    ["lastSeen"] = LicenseService.Null(),
                },
            },
            ["currentDocument"] = new JsonObject { ["exists"] = false },
            ["updateTransforms"] = new JsonArray(new JsonObject { ["fieldPath"] = "createdAt", ["setToServerValue"] = "REQUEST_TIME" }),
        };
        try { await Commit(write); }
        catch (LicenseException ex) when (ex.Message.Contains("ALREADY_EXISTS") || ex.Message.Contains("FAILED_PRECONDITION"))
        {
            throw new LicenseException($"이미 있는 키입니다: {key}");
        }
        return key;
    }

    public async Task<List<LicenseKeyInfo>> List()
    {
        var list = new List<LicenseKeyInfo>();
        string? page = null;
        do
        {
            var res = await Send(HttpMethod.Get, $"{LicenseService.Docs}/ids?pageSize=300" + (page != null ? $"&pageToken={page}" : ""));
            foreach (var d in res["documents"]?.AsArray() ?? [])
            {
                list.Add(new LicenseKeyInfo(((string)d!["name"]!).Split('/').Last(),
                    LicenseService.Str(d, "tier") ?? "basic",
                    (bool?)d["fields"]?["admin"]?["booleanValue"] ?? false,
                    LicenseService.Time(d, "createdAt")?.ToLocalTime() ?? default,
                    LicenseService.Time(d, "lastSeen")?.ToLocalTime(),
                    LicenseService.Str(d, "note") ?? ""));
            }
            page = (string?)res["nextPageToken"];
        } while (page != null);
        return list.OrderByDescending(k => k.CreatedAt).ToList();
    }


    public Task SetTier(string key, LicenseTier tier) =>
        Commit(LicenseService.Update(key, new() { ["tier"] = LicenseService.Str(tier == LicenseTier.Pro ? "pro" : "basic") }, ["tier"]));

    public async Task Delete(string key) =>
        await Send(HttpMethod.Delete, $"{LicenseService.Docs}/ids/{Uri.EscapeDataString(key)}");

    private Task Commit(params JsonObject[] writes) =>
        Send(HttpMethod.Post, $"{LicenseService.Docs}:commit", new JsonObject { ["writes"] = new JsonArray(writes) });

    private async Task<JsonNode> Send(HttpMethod method, string url, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await OwnerToken());
        req.Headers.Add("X-Goog-User-Project", LicenseService.ProjectId);
        if (body != null) req.Content = JsonContent.Create(body);
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            string text = await resp.Content.ReadAsStringAsync();
            string status = "";
            try { status = (string?)JsonNode.Parse(text)?["error"]?["status"] ?? ""; } catch (JsonException) { }
            throw new LicenseException($"관리자 요청 실패 ({(int)resp.StatusCode} {status})");
        }
        return await LicenseService.ReadJson(resp);
    }

    /// <summary>The Firebase CLI keeps a refreshed OAuth token in its config; running any command refreshes it.</summary>
    private static async Task<string> OwnerToken()
    {
        var token = ReadToken();
        if (token is { } t && t.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return t.Token;

        var psi = new ProcessStartInfo("cmd.exe", "/c firebase projects:list --json")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["NODE_NO_WARNINGS"] = "1";
        using (var p = Process.Start(psi) ?? throw new LicenseException("Firebase CLI를 실행할 수 없습니다."))
        {
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        return ReadToken()?.Token ?? throw new LicenseException("이 PC의 Firebase CLI 로그인 정보를 찾을 수 없습니다. (firebase login)");
    }

    private static (string Token, DateTimeOffset ExpiresAt)? ReadToken()
    {
        try
        {
            var cfg = JsonNode.Parse(File.ReadAllText(CliConfig));
            var tok = cfg?["tokens"];
            string? access = (string?)tok?["access_token"];
            long expires = (long?)tok?["expires_at"] ?? 0;
            return access == null ? null : (access, DateTimeOffset.FromUnixTimeMilliseconds(expires));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
