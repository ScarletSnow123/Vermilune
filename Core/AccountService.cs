using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

public sealed class DeviceCodeInfo
{
    public string DeviceCode { get; init; } = "";
    public string UserCode { get; init; } = "";
    public string VerificationUri { get; init; } = "";
    public string Message { get; init; } = "";
    public int ExpiresIn { get; init; }
    public int Interval { get; init; } = 5;
}

public sealed class MicrosoftProfile
{
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public string Xuid { get; init; } = "";
    public string SkinUrl { get; init; } = "";
    public string CapeUrl { get; init; } = "";
    public string SkinModel { get; init; } = "classic";
    public DateTime ExpiresUtc { get; init; }
}

public sealed class MinecraftCape
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Active { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? "披风" : Alias;
}

public sealed class AccountService
{
    // 公共 Microsoft OAuth Client ID（来自开源启动器生态）。
    // 可在设置中替换为自己的 Azure 应用 ID。
    public static string ClientId => LauncherApp.I.Settings.MicrosoftClientId;
    private const string Scope = "XboxLive.signin offline_access";
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private static async Task<HttpResponseMessage> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent(form);
                var response = await HttpService.Client.PostAsync(url, content, ct);
                if (response.IsSuccessStatusCode) return response;
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
                last = new AuthException(FriendlyError(body));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
            if (attempt < 2) await Task.Delay(400 * (attempt + 1), ct);
        }
        throw new AuthException("无法连接 Microsoft 登录服务：" + (last?.Message ?? "网络错误") + "\n请检查网络或代理后重试。");
    }

    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct)
    {
        using var response = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope
        }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(text) as JsonObject ?? throw new AuthException("微软返回了无法解析的数据。");
        return new DeviceCodeInfo
        {
            DeviceCode = root["device_code"]?.GetValue<string>() ?? "",
            UserCode = root["user_code"]?.GetValue<string>() ?? "",
            VerificationUri = root["verification_uri"]?.GetValue<string>() ?? "https://www.microsoft.com/link",
            Message = root["message"]?.GetValue<string>() ?? "",
            ExpiresIn = root["expires_in"]?.GetValue<int>() ?? 900,
            Interval = Math.Max(2, root["interval"]?.GetValue<int>() ?? 5)
        };
    }

    private static async Task<(bool Ok, string Text)> PostTokenAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent(form);
                using var response = await HttpService.Client.PostAsync(url, content, ct);
                return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
            if (attempt < 2) await Task.Delay(400 * (attempt + 1), ct);
        }
        throw new AuthException("无法连接 Microsoft 登录服务：" + (last?.Message ?? "网络错误") + "\n请检查网络或代理后重试。");
    }

    public async Task<MicrosoftProfile> LoginMicrosoftAsync(IProgress<DeviceCodeInfo>? deviceCode,
        IProgress<string>? status, CancellationToken ct)
    {
        var code = await StartDeviceCodeAsync(ct);
        deviceCode?.Report(code);
        status?.Report("等待在浏览器中完成微软登录…");
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = code.Interval;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var (ok, text) = await PostTokenAsync(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = code.DeviceCode
            }, ct);
            var root = TryParse(text);
            if (ok && root is not null)
            {
                var access = root["access_token"]?.GetValue<string>() ?? "";
                var refresh = root["refresh_token"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(access)) throw new AuthException("微软登录未返回访问令牌。");
                return await CompleteXboxLoginAsync(access, refresh, ct);
            }
            var error = root?["error"]?.GetValue<string>() ?? "";
            if (error is "authorization_pending") { status?.Report("已打开登录页，等待确认…"); continue; }
            if (error is "slow_down") { interval += 2; continue; }
            if (error is "expired_token") throw new AuthException("登录代码已过期，请重新登录。");
            if (error is "authorization_declined") throw new AuthException("登录已被拒绝。");
            throw new AuthException("微软登录失败：" + FriendlyError(text));
        }
        throw new AuthException("登录超时，请重新尝试。");
    }

    public async Task<MicrosoftProfile> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) throw new AuthException("没有可用的刷新令牌，请重新登录。");
        var (ok, text) = await PostTokenAsync(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        }, ct);
        var root = TryParse(text);
        if (!ok || root is null)
            throw new AuthException("微软登录已失效，请重新登录：" + FriendlyError(text));
        var access = root["access_token"]?.GetValue<string>() ?? "";
        var newRefresh = root["refresh_token"]?.GetValue<string>() ?? refreshToken;
        return await CompleteXboxLoginAsync(access, newRefresh, ct);
    }

    private async Task<MicrosoftProfile> CompleteXboxLoginAsync(string msAccessToken, string refreshToken, CancellationToken ct)
    {
        // 1. Xbox Live
        var xblBody = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msAccessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblBody, ct);
        var xblToken = xbl["Token"]?.GetValue<string>() ?? throw new AuthException("Xbox Live 认证失败。");
        var uhs = xbl["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>() ?? "";

        // 2. XSTS
        var xstsBody = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xblToken)
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        };
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsBody, ct, allowError: true);
        if (xsts["Token"] is null)
        {
            var xerr = xsts["XErr"]?.GetValue<long>() ?? 0;
            throw new AuthException(xerr switch
            {
                2148916233 => "该微软账号没有 Xbox 账号，请先在 xbox.com 创建。",
                2148916235 => "该账号所在地区无法使用 Xbox Live。",
                2148916236 or 2148916237 => "该账号需要进行成人验证。",
                2148916238 => "该账号是儿童账号，需要由家庭组批准。",
                2148916227 => "该账号已被 Xbox Live 封禁。",
                _ => "Xbox 认证失败" + (xerr != 0 ? $"（XErr {xerr}）" : "") + "。"
            });
        }
        var xstsToken = xsts["Token"]!.GetValue<string>();
        var xuid = xsts["DisplayClaims"]?["xui"]?[0]?["xid"]?.GetValue<string>() ?? "";

        // 3. Minecraft 服务
        var mcBody = new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" };
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcBody, ct, allowError: true);
        var mcToken = mc["access_token"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(mcToken))
            throw new AuthException("Minecraft 服务拒绝了登录，请确认账号已购买 Minecraft。");

        // 4. 档案
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        using var profileResponse = await HttpService.Client.SendAsync(req, ct);
        var profileText = await profileResponse.Content.ReadAsStringAsync(ct);
        if (!profileResponse.IsSuccessStatusCode)
        {
            if (profileResponse.StatusCode == HttpStatusCode.NotFound)
                throw new AuthException("该账号没有 Minecraft: Java Edition 档案。");
            throw new AuthException("读取 Minecraft 档案失败：" + FriendlyError(profileText));
        }
        var profile = TryParse(profileText) ?? throw new AuthException("Minecraft 档案格式异常。");
        var name = profile["name"]?.GetValue<string>() ?? "";
        var uuid = profile["id"]?.GetValue<string>() ?? "";
        var skin = "";
        var skinModel = "classic";
        if (profile["skins"] is JsonArray skins && skins.Count > 0)
        {
            skin = skins[0]?["url"]?.GetValue<string>() ?? "";
            skinModel = skins[0]?["metadata"]?["model"]?.GetValue<string>() ?? "classic";
        }
        var cape = "";
        if (profile["capes"] is JsonArray capes)
        {
            var active = capes.OfType<JsonObject>().FirstOrDefault(c => string.Equals(c["state"]?.GetValue<string>(), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                         ?? capes.OfType<JsonObject>().FirstOrDefault();
            cape = active?["url"]?.GetValue<string>() ?? "";
        }

        return new MicrosoftProfile
        {
            Name = name,
            Uuid = uuid,
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            Xuid = xuid,
            SkinUrl = skin,
            CapeUrl = cape,
            SkinModel = skinModel,
            ExpiresUtc = DateTime.UtcNow.AddHours(23)
        };
    }

    private static async Task<JsonObject> PostJsonAsync(string url, JsonObject body, CancellationToken ct, bool allowError = false)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await HttpService.Client.PostAsync(url, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var root = TryParse(text) as JsonObject ?? new JsonObject();
        if (!response.IsSuccessStatusCode && !allowError)
            throw new AuthException(FriendlyError(text));
        return root;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); } catch { return null; }
    }

    public static string FriendlyError(string text)
    {
        try
        {
            var node = JsonNode.Parse(text) as JsonObject;
            var desc = node?["error_description"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Split('\n')[0].Trim();
            var err = node?["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(err)) return err;
        }
        catch { }
        return text.Length > 240 ? text[..240] : text;
    }

    public static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static bool IsValidOfflineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 1 || name.Length > 64) return false;
        foreach (var c in name)
            if (c < 0x20) return false;
        return name.Trim() == name;
    }

    public static string EnsureUuid(AccountSettings account)
    {
        if (account.IsOnline && !string.IsNullOrWhiteSpace(account.Uuid)) return account.Uuid;
        // 离线账号的 UUID 必须随用户名变化，否则换名后服务端会认为是同一个人
        return OfflineUuid(string.IsNullOrWhiteSpace(account.Name) ? "Player" : account.Name);
    }

    public static string GetAccessToken()
    {
        var account = LauncherApp.I.Settings.Account;
        if (!account.IsMicrosoft) throw new AuthException("请先登录 Microsoft 账号。");
        var token = Dpapi.Unprotect(account.AccessTokenProtected);
        if (string.IsNullOrWhiteSpace(token)) throw new AuthException("登录已失效，请重新登录 Microsoft 账号。");
        return token;
    }

    public async Task<List<MinecraftCape>> GetCapesAsync(CancellationToken ct)
    {
        try
        {
            return await GetCapesOfficialAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("读取完整披风列表失败，尝试只读取当前披风：" + ex.Message);
            var fallback = await GetCapeFromSessionServerAsync(ct);
            if (fallback is not null)
            {
                LauncherApp.I.Settings.Account.CapeUrl = fallback.Url;
                LauncherApp.I.SaveDebounced();
                return new List<MinecraftCape> { fallback };
            }
            return new List<MinecraftCape>();
        }
    }

    private async Task<List<MinecraftCape>> GetCapesOfficialAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetAccessToken());
        using var response = await HttpService.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new AuthException("读取披风列表失败：" + FriendlyError(text));
        var root = TryParse(text) as JsonObject ?? new JsonObject();
        var list = new List<MinecraftCape>();
        if (root["capes"] is JsonArray capes)
        {
            foreach (var item in capes.OfType<JsonObject>())
            {
                list.Add(new MinecraftCape
                {
                    Id = item["id"]?.GetValue<string>() ?? "",
                    Alias = item["alias"]?.GetValue<string>() ?? "",
                    Url = item["url"]?.GetValue<string>() ?? "",
                    Active = string.Equals(item["state"]?.GetValue<string>(), "ACTIVE", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        var active = list.FirstOrDefault(c => c.Active);
        LauncherApp.I.Settings.Account.CapeUrl = active?.Url ?? "";
        LauncherApp.I.SaveDebounced();
        return list;
    }

    private async Task<MinecraftCape?> GetCapeFromSessionServerAsync(CancellationToken ct)
    {
        var account = LauncherApp.I.Settings.Account;
        if (string.IsNullOrWhiteSpace(account.Uuid)) return null;
        var uuid = account.Uuid.Replace("-", "");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid);
        using var response = await HttpService.Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(ct);
        var root = TryParse(text) as JsonObject;
        if (root?["properties"] is not JsonArray properties) return null;
        foreach (var item in properties.OfType<JsonObject>())
        {
            if (!string.Equals(item["name"]?.GetValue<string>(), "textures", StringComparison.OrdinalIgnoreCase))
                continue;
            var encoded = item["value"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(encoded)) continue;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var textures = (TryParse(decoded) as JsonObject)?["textures"] as JsonObject;
                var cape = textures?["CAPE"] as JsonObject;
                var url = cape?["url"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(url))
                    return new MinecraftCape { Id = "", Alias = "当前披风", Url = url, Active = true };
            }
            catch { }
        }
        return null;
    }

    public async Task SetCapeAsync(string? capeId, CancellationToken ct)
    {
        var token = GetAccessToken();
        using var request = new HttpRequestMessage(capeId is null ? HttpMethod.Delete : HttpMethod.Put,
            "https://api.minecraftservices.com/minecraft/profile/capes/active");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (capeId is not null)
        {
            request.Content = new StringContent(new JsonObject { ["capeId"] = capeId }.ToJsonString(),
                Encoding.UTF8, "application/json");
        }
        using var response = await HttpService.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new AuthException("切换披风失败：" + FriendlyError(text));
        var capes = await GetCapesAsync(ct);
        var active = capes.FirstOrDefault(c => c.Active);
        LauncherApp.I.Settings.Account.CapeUrl = active?.Url ?? "";
        LauncherApp.I.Save();
        try
        {
            var safe = new string(LauncherApp.I.Settings.Account.Uuid.Where(char.IsLetterOrDigit).Take(36).ToArray());
            var cache = Path.Combine(AppPaths.CacheDir, "skins", "cape-" + safe + ".png");
            if (File.Exists(cache)) File.Delete(cache);
        }
        catch { }
    }

    public async Task UploadSkinAsync(string path, bool slim, CancellationToken ct)
    {
        var token = GetAccessToken();
        var bytes = await File.ReadAllBytesAsync(path, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", Path.GetFileName(path));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")
        {
            Content = form
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await HttpService.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new AuthException("上传皮肤失败：" + FriendlyError(text));

        var root = TryParse(text) as JsonObject;
        var account = LauncherApp.I.Settings.Account;
        if (root?["skins"] is JsonArray skins && skins.Count > 0)
        {
            account.SkinUrl = skins[0]?["url"]?.GetValue<string>() ?? account.SkinUrl;
            account.SkinModel = skins[0]?["metadata"]?["model"]?.GetValue<string>() ?? (slim ? "slim" : "classic");
        }
        if (root?["capes"] is JsonArray capes)
        {
            var active = capes.OfType<JsonObject>().FirstOrDefault(c => string.Equals(c["state"]?.GetValue<string>(), "ACTIVE", StringComparison.OrdinalIgnoreCase));
            account.CapeUrl = active?["url"]?.GetValue<string>() ?? account.CapeUrl;
        }
        LauncherApp.I.Settings.SkinPath = "";
        LauncherApp.I.Settings.SkinModelOverride = null;
        LauncherApp.I.Save();

        try
        {
            var cache = Path.Combine(AppPaths.CacheDir, "skins", "skin-" + new string(account.Uuid.Where(char.IsLetterOrDigit).Take(36).ToArray()) + ".png");
            if (File.Exists(cache)) File.Delete(cache);
        }
        catch { }
    }

    public static void ApplyProfile(AccountSettings account, MicrosoftProfile profile)
    {
        account.Type = "microsoft";
        account.Name = profile.Name;
        account.Uuid = profile.Uuid;
        account.Xuid = profile.Xuid;
        account.SkinUrl = profile.SkinUrl;
        account.CapeUrl = profile.CapeUrl;
        account.SkinModel = profile.SkinModel;
        account.AccessTokenProtected = Dpapi.Protect(profile.AccessToken);
        account.RefreshTokenProtected = Dpapi.Protect(profile.RefreshToken);
        account.YggdrasilServer = "";
        account.ClientTokenProtected = "";
        account.AccessTokenExpiresUtc = profile.ExpiresUtc;
        account.LastLoginUtc = DateTime.UtcNow;
    }
}
