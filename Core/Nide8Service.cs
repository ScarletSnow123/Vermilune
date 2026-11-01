using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

/// <summary>
/// 统一通行证（Nide8）登录。复用 Yggdrasil 协议，游戏侧使用 nide8auth.jar。
/// </summary>
public static class Nide8Service
{
    private const string Root = "https://auth.mc-user.com:233";
    private const string JarUrl = "https://login.mc-user.com:233/index/jar";

    public static string NormalizeServerId(string input)
    {
        var id = (input ?? "").Trim().Trim('/');
        if (id.Length == 0) throw new AuthException("请填写统一通行证服务器 ID。");
        if (id.Contains('/') || id.Contains(' ') || id.Contains('\\'))
            throw new AuthException("统一通行证服务器 ID 格式不正确。");
        return id;
    }

    public static string ServerBase(string serverId) => Root + "/" + NormalizeServerId(serverId);

    public static Task<YggdrasilProfile> AuthenticateAsync(string serverId, string username, string password,
        CancellationToken ct = default)
        => YggdrasilService.AuthenticateAsync(ServerBase(serverId), username, password, ct);

    public static void Apply(AccountSettings account, YggdrasilProfile profile, string serverId)
    {
        YggdrasilService.Apply(account, profile);
        account.Type = "nide";
        account.NideServer = NormalizeServerId(serverId);
        account.NideLoginName = profile.LoginName;
    }

    public static string JarPath => Path.Combine(AppPaths.DataDir, "libraries", "nide8auth.jar");

    public static async Task<string> EnsureJarAsync(string serverId, CancellationToken ct = default)
    {
        var path = JarPath;
        if (File.Exists(path) && new FileInfo(path).Length > 100_000) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var metadata = await HttpService.GetStringAsync(ServerBase(serverId), ct);
        var hash = (JsonNode.Parse(metadata) as JsonObject)?["jarHash"]?.GetValue<string>() ?? "";
        var bytes = await HttpService.GetBytesAsync(JarUrl, ct);
        if (bytes.Length < 100_000) throw new AuthException("nide8auth.jar 下载不完整。");
        if (!string.IsNullOrEmpty(hash))
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            if (hash.Length == 64 && !sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new AuthException("nide8auth.jar 校验失败，请重试。");
            if (hash.Length == 40 && !sha1.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new AuthException("nide8auth.jar 校验失败，请重试。");
        }
        await File.WriteAllBytesAsync(path, bytes, ct);
        Log.Info("nide8auth.jar 已下载：" + path);
        return path;
    }
}
