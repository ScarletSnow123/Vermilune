using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class YggdrasilProfile
{
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string ClientToken { get; init; } = "";
    public string Server { get; init; } = "";
}

/// <summary>
/// Yggdrasil / authlib-injector 外置登录。
/// 支持 LittleSkin 这类提供 /authserver/authenticate 的皮肤站。
/// </summary>
public static class YggdrasilService
{
    public const string DefaultServer = "https://littleskin.cn/api/yggdrasil";

    public static string NormalizeServer(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) throw new AuthException("请填写外置登录服务器地址。");
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new AuthException("外置登录服务器地址格式不正确。");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) path = "/api/yggdrasil";
        var builder = new UriBuilder(uri) { Path = path };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static async Task<YggdrasilProfile> AuthenticateAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        server = NormalizeServer(server);
        username = (username ?? "").Trim();
        if (username.Length == 0) throw new AuthException("请填写外置账号邮箱或用户名。");
        if (string.IsNullOrEmpty(password)) throw new AuthException("请填写外置账号密码。");

        var clientToken = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true
        };

        using var response = await PostJsonAsync(server + "/authserver/authenticate", payload.ToJsonString(), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException(FriendlyError(body, "外置登录失败，请检查邮箱、密码和服务器地址。"));

        var root = JsonNode.Parse(body) as JsonObject;
        var token = root?["accessToken"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(token)) throw new AuthException("服务器没有返回 accessToken。");

        var selected = root?["selectedProfile"] as JsonObject;
        if (selected is null && root?["availableProfiles"] is JsonArray profiles && profiles.Count > 0)
            selected = profiles[0] as JsonObject;

        var uuid = selected?["id"]?.GetValue<string>() ?? "";
        var name = selected?["name"]?.GetValue<string>() ?? username;
        if (string.IsNullOrEmpty(uuid)) throw new AuthException("该外置账号还没有创建角色，请先在皮肤站创建角色。");

        return new YggdrasilProfile
        {
            Name = name,
            Uuid = uuid,
            AccessToken = token,
            ClientToken = root?["clientToken"]?.GetValue<string>() ?? clientToken,
            Server = server
        };
    }

    public static async Task<YggdrasilProfile> RefreshAsync(AccountSettings account, CancellationToken ct = default)
    {
        var server = NormalizeServer(account.YggdrasilServer);
        var access = Dpapi.Unprotect(account.AccessTokenProtected);
        var client = Dpapi.Unprotect(account.ClientTokenProtected);
        if (string.IsNullOrEmpty(access)) throw new AuthException("外置登录令牌为空，请重新登录。");

        var payload = new JsonObject
        {
            ["accessToken"] = access,
            ["clientToken"] = client,
            ["requestUser"] = true
        };
        using var response = await PostJsonAsync(server + "/authserver/refresh", payload.ToJsonString(), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new AuthException(FriendlyError(body, "外置登录已过期，请重新登录。"));

        var root = JsonNode.Parse(body) as JsonObject;
        var token = root?["accessToken"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(token)) throw new AuthException("刷新外置登录令牌失败。");

        return new YggdrasilProfile
        {
            Name = account.Name,
            Uuid = account.Uuid,
            AccessToken = token,
            ClientToken = root?["clientToken"]?.GetValue<string>() ?? client,
            Server = server
        };
    }

    public static void Apply(AccountSettings account, YggdrasilProfile profile, int hours = 6)
    {
        account.Type = "yggdrasil";
        account.Name = profile.Name;
        account.Uuid = profile.Uuid;
        account.YggdrasilServer = profile.Server;
        account.AccessTokenProtected = Dpapi.Protect(profile.AccessToken);
        account.ClientTokenProtected = Dpapi.Protect(profile.ClientToken);
        account.RefreshTokenProtected = "";
        account.AccessTokenExpiresUtc = DateTime.UtcNow.AddHours(hours);
        account.LastLoginUtc = DateTime.UtcNow;
        account.Xuid = "";
    }

    /// <summary>读取皮肤站 profile 里的 SKIN / CAPE 纹理。</summary>
    public static async Task<bool> UpdateTexturesAsync(AccountSettings account, CancellationToken ct = default)
    {
        try
        {
            var server = NormalizeServer(account.YggdrasilServer);
            var uuid = account.Uuid.Replace("-", "");
            using var response = await HttpService.Client.GetAsync(
                $"{server}/sessionserver/session/minecraft/profile/{uuid}", ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(body) as JsonObject;
            if (root?["properties"] is not JsonArray properties) return false;
            foreach (var item in properties.OfType<JsonObject>())
            {
                if (!string.Equals(item["name"]?.GetValue<string>(), "textures", StringComparison.OrdinalIgnoreCase))
                    continue;
                var encoded = item["value"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(encoded)) continue;
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var textures = (JsonNode.Parse(decoded) as JsonObject)?["textures"] as JsonObject;
                if (textures is null) return false;
                var skin = textures["SKIN"] as JsonObject;
                var cape = textures["CAPE"] as JsonObject;
                account.SkinUrl = skin?["url"]?.GetValue<string>() ?? "";
                account.CapeUrl = cape?["url"]?.GetValue<string>() ?? "";
                account.SkinModel = skin?["metadata"]?["model"]?.GetValue<string>() ?? "classic";
                LauncherApp.I.Skins.InvalidateAccountTextureCache();
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("读取外置账号皮肤失败：" + ex.Message);
        }
        return false;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(string url, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await HttpService.Client.PostAsync(url, content, ct);
    }

    private static string FriendlyError(string body, string fallback)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var msg = root?["errorMessage"]?.GetValue<string>() ??
                      root?["message"]?.GetValue<string>() ??
                      root?["error"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
        }
        catch { }
        return fallback;
    }
}

/// <summary>
/// 下载并缓存 authlib-injector，供外置登录的 JVM 参数使用。
/// </summary>
public static class AuthlibInjector
{
    private const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

    public static string JarPath => Path.Combine(AppPaths.DataDir, "libraries", "authlib-injector.jar");

    public static async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        var path = JarPath;
        if (File.Exists(path) && new FileInfo(path).Length > 100_000) return path;

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var metadata = await HttpService.GetStringAsync(LatestUrl, ct);
        var root = JsonNode.Parse(metadata) as JsonObject;
        var url = root?["download_url"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(url)) throw new AuthException("authlib-injector 下载地址无效。");

        var bytes = await HttpService.GetBytesAsync(url, ct);
        if (bytes.Length < 100_000) throw new AuthException("authlib-injector 下载不完整。");

        var expected = root?["checksums"]?["sha256"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(expected))
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new AuthException("authlib-injector 校验失败，请重试。");
        }

        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        Log.Info("authlib-injector 已下载：" + path);
        return path;
    }
}
