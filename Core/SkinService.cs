using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace Vermilune.Core;

public sealed class SkinService
{
    public string SkinDir => Path.Combine(AppPaths.DataDir, "Skins");

    private string ModelFile => Path.Combine(SkinDir, "models.json");
    private bool _detectedSlim;

    public bool IsSlim => LauncherApp.I.Settings.SkinModelOverride ?? _detectedSlim;

    public string ActiveSkinName
    {
        get
        {
            var path = LauncherApp.I.Settings.SkinPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(LauncherApp.I.Settings.Account.SkinUrl) ? "默认皮肤" : LauncherApp.I.Settings.Account.Name + " 的皮肤";
        }
    }

    public string ActiveCapeName =>
        string.IsNullOrEmpty(LauncherApp.I.Settings.Account.CapeUrl) ? "无披风" : LauncherApp.I.Settings.Account.Name + " 的披风";

    public async Task<BitmapSource?> LoadSkinAsync(CancellationToken ct = default)
    {
        try
        {
            var path = LauncherApp.I.Settings.SkinPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var local = LoadBitmap(path);
                var saved = GetSavedModel(path);
                _detectedSlim = saved ?? DetectSlim(local);
                return local;
            }

            var url = LauncherApp.I.Settings.Account.SkinUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                var cache = AccountCachePath("skin");
                if (!File.Exists(cache) || new FileInfo(cache).Length < 64)
                {
                    var bytes = await GetTextureBytesAsync(url, ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    await File.WriteAllBytesAsync(cache, bytes, ct);
                }
                var account = LoadBitmap(cache);
                _detectedSlim = string.Equals(LauncherApp.I.Settings.Account.SkinModel, "slim", StringComparison.OrdinalIgnoreCase)
                                || DetectSlim(account);
                return account;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("皮肤读取失败：" + ex.Message);
        }
        return null;
    }

    public async Task<BitmapSource?> LoadCapeAsync(CancellationToken ct = default)
    {
        try
        {
            var url = LauncherApp.I.Settings.Account.CapeUrl;
            if (string.IsNullOrWhiteSpace(url)) return null;
            var cache = AccountCachePath("cape");
            if (!File.Exists(cache) || new FileInfo(cache).Length < 64)
            {
                var bytes = await GetTextureBytesAsync(url, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllBytesAsync(cache, bytes, ct);
            }
            return LoadBitmap(cache);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("读取披风失败：" + ex.Message);
        }
        return null;
    }

    public IReadOnlyList<string> ListSkins()
    {
        try
        {
            Directory.CreateDirectory(SkinDir);
            return Directory.EnumerateFiles(SkinDir)
                .Where(f => new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public string? ImportSkin(string source, bool activate = true)
    {
        if (!ValidateSkin(source, out var error))
        {
            Log.Warn("皮肤文件无效：" + error);
            return null;
        }
        var target = CopyToLibrary(source);
        if (target is null) return null;
        var bitmap = LoadBitmap(target);
        var slim = DetectSlim(bitmap);
        SaveModel(target, slim);
        _detectedSlim = slim;
        if (activate) UseSkin(target);
        Log.Info("已导入皮肤：" + target + "（" + (slim ? "Alex" : "Steve") + "）");
        return target;
    }

    public (int Copied, int Skipped) ImportSkinFolder(string folder)
    {
        var copied = 0;
        var skipped = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file);
                if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ValidateSkin(file, out _)) { skipped++; continue; }
                var target = CopyToLibrary(file);
                if (target is null) { skipped++; continue; }
                SaveModel(target, DetectSlim(LoadBitmap(target)));
                copied++;
            }
        }
        catch (Exception ex) { Log.Warn("导入皮肤文件夹失败：" + ex.Message); }
        return (copied, skipped);
    }

    public bool GetSlim(string path)
    {
        try { return GetSavedModel(path) ?? DetectSlim(LoadBitmap(path)); }
        catch { return false; }
    }

    public void UseSkin(string path, bool? slim = null)
    {
        if (!File.Exists(path)) return;
        LauncherApp.I.Settings.SkinPath = path;
        LauncherApp.I.Settings.SkinModelOverride = slim;
        if (slim.HasValue) SaveModel(path, slim.Value);
        LauncherApp.I.Save();
    }

    public void SetSkinModel(string path, bool slim)
    {
        if (!File.Exists(path)) return;
        SaveModel(path, slim);
        if (string.Equals(LauncherApp.I.Settings.SkinPath, path, StringComparison.OrdinalIgnoreCase))
        {
            LauncherApp.I.Settings.SkinModelOverride = slim;
            LauncherApp.I.Save();
        }
    }

    private string? CopyToLibrary(string source)
    {
        try
        {
            Directory.CreateDirectory(SkinDir);
            var target = Path.Combine(SkinDir, Path.GetFileName(source));
            var n = 2;
            while (File.Exists(target) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(SkinDir, Path.GetFileNameWithoutExtension(source) + "-" + n++ + Path.GetExtension(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, true);
            return target;
        }
        catch (Exception ex) { Log.Warn("复制皮肤失败：" + ex.Message); return null; }
    }

    public void UseAccountSkin()
    {
        LauncherApp.I.Settings.SkinPath = "";
        LauncherApp.I.Settings.SkinModelOverride = null;
        LauncherApp.I.Save();
    }

    public void SetSlimOverride(bool slim)
    {
        LauncherApp.I.Settings.SkinModelOverride = slim;
        LauncherApp.I.Save();
    }

    public bool HasCustomSkin =>
        !string.IsNullOrEmpty(LauncherApp.I.Settings.SkinPath) && File.Exists(LauncherApp.I.Settings.SkinPath);

    public void ApplyOfflinePack(string gameDir, string mcVersion)
    {
        var skinPath = LauncherApp.I.Settings.SkinPath;
        if (string.IsNullOrEmpty(skinPath) || !File.Exists(skinPath))
        {
            RemoveOfflinePack(gameDir);
            return;
        }

        try
        {
            var packDir = Path.Combine(gameDir, "resourcepacks");
            Directory.CreateDirectory(packDir);
            var zipPath = Path.Combine(packDir, "Vermilune Skin.zip");
            var slim = IsSlim;
            var modern = mcVersion.StartsWith("1.19.3", StringComparison.OrdinalIgnoreCase) ||
                         mcVersion.StartsWith("1.20", StringComparison.OrdinalIgnoreCase) ||
                         mcVersion.StartsWith("1.21", StringComparison.OrdinalIgnoreCase) ||
                         mcVersion.StartsWith("1.22", StringComparison.OrdinalIgnoreCase) ||
                         mcVersion.StartsWith("26.", StringComparison.OrdinalIgnoreCase);
            var packFormat = PackFormat(mcVersion);

            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            WriteEntry(zip, "pack.mcmeta", System.Text.Encoding.UTF8.GetBytes(
                "{\"pack\":{\"pack_format\":" + packFormat + ",\"description\":\"Vermilune 皮肤\"}}"));
            var skinBytes = File.ReadAllBytes(skinPath);
            if (modern)
            {
                foreach (var name in new[] { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" })
                    WriteEntry(zip, "assets/minecraft/textures/entity/player/" + (slim ? "slim/" : "wide/") + name + ".png", skinBytes);
            }
            else
            {
                WriteEntry(zip, "assets/minecraft/textures/entity/" + (slim ? "alex.png" : "steve.png"), skinBytes);
            }
            EnablePack(gameDir, "file/Vermilune Skin.zip");
            Log.Info("离线皮肤资源包已更新：" + zipPath);
        }
        catch (Exception ex)
        {
            Log.Warn("构建离线皮肤资源包失败：" + ex.Message);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static void RemoveOfflinePack(string gameDir)
    {
        var zipPath = Path.Combine(gameDir, "resourcepacks", "Vermilune Skin.zip");
        if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }
        var optionsPath = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(optionsPath)) return;
        try
        {
            var lines = File.ReadAllLines(optionsPath).ToList();
            var index = lines.FindIndex(l => l.StartsWith("resourcePacks:", StringComparison.Ordinal));
            if (index < 0) return;
            var packs = new List<string>();
            if (JsonNode.Parse(lines[index]["resourcePacks:".Length..].Trim()) is JsonArray arr)
                packs.AddRange(arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
            packs.Remove("file/Vermilune Skin.zip");
            if (packs.Count == 0) packs.Add("vanilla");
            lines[index] = "resourcePacks:" + new JsonArray(packs.Select(x => JsonValue.Create(x)).ToArray()).ToJsonString();
            File.WriteAllLines(optionsPath, lines);
        }
        catch { }
    }

    private static void EnablePack(string gameDir, string pack)
    {
        var optionsPath = Path.Combine(gameDir, "options.txt");
        var lines = File.Exists(optionsPath) ? File.ReadAllLines(optionsPath).ToList() : new List<string>();
        var index = lines.FindIndex(l => l.StartsWith("resourcePacks:", StringComparison.Ordinal));
        var packs = new List<string>();
        if (index >= 0)
        {
            var raw = lines[index]["resourcePacks:".Length..].Trim();
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                    packs.AddRange(arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
            }
            catch { }
            if (packs.Count == 0) packs.Add("vanilla");
            lines.RemoveAt(index);
        }
        else packs.Add("vanilla");
        if (!packs.Contains(pack)) packs.Add(pack);
        lines.Insert(Math.Min(index < 0 ? lines.Count : index, lines.Count), "resourcePacks:" + new JsonArray(packs.Select(x => JsonValue.Create(x)).ToArray()).ToJsonString());
        File.WriteAllLines(optionsPath, lines);
    }

    private static int PackFormat(string mc)
    {
        if (mc.StartsWith("26.", StringComparison.OrdinalIgnoreCase)) return 64;
        if (mc.StartsWith("1.21.4", StringComparison.OrdinalIgnoreCase)) return 46;
        if (mc.StartsWith("1.21", StringComparison.OrdinalIgnoreCase)) return 34;
        if (mc.StartsWith("1.20.5", StringComparison.OrdinalIgnoreCase)) return 32;
        if (mc.StartsWith("1.20.2", StringComparison.OrdinalIgnoreCase)) return 18;
        if (mc.StartsWith("1.20", StringComparison.OrdinalIgnoreCase)) return 15;
        if (mc.StartsWith("1.19.3", StringComparison.OrdinalIgnoreCase)) return 12;
        if (mc.StartsWith("1.19", StringComparison.OrdinalIgnoreCase)) return 9;
        if (mc.StartsWith("1.18", StringComparison.OrdinalIgnoreCase)) return 8;
        if (mc.StartsWith("1.17", StringComparison.OrdinalIgnoreCase)) return 7;
        if (mc.StartsWith("1.16.2", StringComparison.OrdinalIgnoreCase)) return 6;
        if (mc.StartsWith("1.16", StringComparison.OrdinalIgnoreCase)) return 5;
        if (mc.StartsWith("1.15", StringComparison.OrdinalIgnoreCase)) return 5;
        if (mc.StartsWith("1.13", StringComparison.OrdinalIgnoreCase) || mc.StartsWith("1.14", StringComparison.OrdinalIgnoreCase)) return 4;
        if (mc.StartsWith("1.11", StringComparison.OrdinalIgnoreCase) || mc.StartsWith("1.12", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }

    public async Task<BitmapSource?> LoadTextureAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var bytes = await GetTextureBytesAsync(url, ct);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("读取纹理失败：" + ex.Message);
            return null;
        }
    }

    private static async Task<byte[]> GetTextureBytesAsync(string url, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (url.Contains("textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(url);
                candidates.Add("http://" + url[8..]);
            }
            else
            {
                candidates.Add(url);
            }
        }
        else
        {
            candidates.Add(url);
        }

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try { return await HttpService.GetBytesAsync(candidate, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw last ?? new InvalidOperationException("无法下载皮肤纹理。");
    }

    public void InvalidateAccountTextureCache()
    {
        try
        {
            var skin = AccountCachePath("skin");
            var cape = AccountCachePath("cape");
            if (File.Exists(skin)) File.Delete(skin);
            if (File.Exists(cape)) File.Delete(cape);
        }
        catch (Exception ex)
        {
            Log.Warn("清理账号皮肤缓存失败：" + ex.Message);
        }
    }

    private static string AccountCachePath(string kind)
    {
        var key = LauncherApp.I.Settings.Account.Uuid;
        if (string.IsNullOrWhiteSpace(key)) key = "default";
        var safe = new string(key.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(36).ToArray());
        return Path.Combine(AppPaths.CacheDir, "skins", kind + "-" + safe + ".png");
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static bool ValidateSkin(string path, out string error)
    {
        error = "";
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != 64 || (frame.PixelHeight != 64 && frame.PixelHeight != 32))
            {
                error = "皮肤图片尺寸应为 64×64 或 64×32。";
                return false;
            }
            if (new FileInfo(path).Length > 24 * 1024)
            {
                error = "皮肤文件应小于 24 KB。";
                return false;
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // 独立启发式：细手臂（Alex）右臂背面只占 3 像素，最外一列通常完全透明。
    private static bool DetectSlim(BitmapSource source)
    {
        if (source.PixelWidth != 64 || source.PixelHeight < 64) return false;
        try
        {
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            for (var y = 20; y <= 31; y++)
                if (pixels[(y * width + 55) * 4 + 3] > 16) return false;
            return true;
        }
        catch { return false; }
    }

    private bool? GetSavedModel(string path)
    {
        try
        {
            if (!File.Exists(ModelFile)) return null;
            var root = JsonNode.Parse(File.ReadAllText(ModelFile)) as JsonObject;
            var token = root?["models"]?[Path.GetFileName(path)];
            if (token is null) return null;
            return string.Equals(token.GetValue<string>(), "slim", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private void SaveModel(string path, bool slim)
    {
        try
        {
            JsonObject root;
            if (File.Exists(ModelFile)) root = JsonNode.Parse(File.ReadAllText(ModelFile)) as JsonObject ?? new JsonObject();
            else root = new JsonObject();
            if (root["models"] is not JsonObject models) root["models"] = models = new JsonObject();
            models[Path.GetFileName(path)] = slim ? "slim" : "classic";
            Directory.CreateDirectory(SkinDir);
            File.WriteAllText(ModelFile, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
