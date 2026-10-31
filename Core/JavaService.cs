using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vermilune.Core;

public sealed class JavaInfo
{
    public string Path { get; init; } = "";
    public string Version { get; init; } = "";
    public int Major { get; init; }
    public string Arch { get; init; } = "";
    public string Source { get; init; } = "";
    public bool Is64Bit => string.IsNullOrEmpty(Arch) || Arch.Contains("64", StringComparison.OrdinalIgnoreCase) || Arch.Contains("x86_64", StringComparison.OrdinalIgnoreCase);
    public string Display => $"Java {Major}" + (string.IsNullOrEmpty(Version) ? "" : $" ({Version})") + " · " + Path;
    public string ShortDisplay => $"Java {Major}" + (string.IsNullOrEmpty(Version) ? "" : $"  ·  {Version}");
}

public sealed class JavaService
{
    private readonly object _lock = new();
    private List<JavaInfo> _all = new();
    private bool _scanned;

    public string MinecraftDir { get; set; } = "";

    public IReadOnlyList<JavaInfo> All
    {
        get { lock (_lock) return _all.ToList(); }
    }

    public JavaInfo? Pick(int requiredMajor, string preferredPath = "")
    {
        var all = All.Where(j => j.Is64Bit || !Environment.Is64BitOperatingSystem).ToList();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var exact = all.FirstOrDefault(j => string.Equals(Path.GetFullPath(j.Path), SafeFull(preferredPath), StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            if (File.Exists(preferredPath))
            {
                var info = Inspect(preferredPath, "自定义");
                if (info is not null) return info;
            }
        }
        var exactMajor = all.Where(j => j.Major == requiredMajor).OrderByDescending(j => j.Major).FirstOrDefault();
        if (exactMajor is not null) return exactMajor;
        var above = all.Where(j => j.Major > requiredMajor).OrderBy(j => j.Major).FirstOrDefault();
        if (above is not null) return above;
        return all.OrderByDescending(j => j.Major).FirstOrDefault();
    }

    public async Task<List<JavaInfo>> ScanAsync(string mcDir, string preferredPath = "")
    {
        MinecraftDir = mcDir;
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, string Source)>();

        void Add(string? home, string source)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            try
            {
                var exe = Path.Combine(home, "bin", "java.exe");
                if (File.Exists(exe)) candidates.Add((exe, source));
            }
            catch { }
        }

        // JAVA_HOME
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");
        Add(Environment.GetEnvironmentVariable("JDK_HOME"), "JDK_HOME");

        // 注册表
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in new[]
            {
                @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE",
                @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment",
                @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Foundation\JDK",
                @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\Azul Systems\Zulu",
                @"SOFTWARE\BellSoft\LibericaJDK", @"SOFTWARE\Amazon Corretto",
                @"SOFTWARE\Semeru\JDK", @"SOFTWARE\IBM\Semeru"
            })
            {
                AddFromRegistry(hive, sub, candidates);
                AddFromRegistry(hive, @"SOFTWARE\WOW6432Node\" + sub, candidates);
            }
        }

        // 常见安装目录
        var roots = new List<string>();
        void AddRoot(string? p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p); }
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Foundation"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Semeru"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft"));
        AddRoot(Path.Combine(AppPaths.ExeDir, "jdk"));
        AddRoot(Path.Combine(AppPaths.ExeDir, "jre"));
        var parent = Path.GetDirectoryName(AppPaths.ExeDir);
        if (!string.IsNullOrEmpty(parent))
        {
            AddRoot(Path.Combine(parent, "jdk"));
            AddRoot(Path.Combine(parent, "jre"));
        }

        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe)) candidates.Add((exe, "常见目录"));
                    foreach (var nestedName in new[] { "jbr", "jre", "runtime", Path.Combine("Contents", "Home") })
                    {
                        var nested = Path.Combine(dir, nestedName, "bin", "java.exe");
                        if (File.Exists(nested)) candidates.Add((nested, "内嵌 JRE"));
                    }
                }
                var direct = Path.Combine(root, "bin", "java.exe");
                if (File.Exists(direct)) candidates.Add((direct, "常见目录"));
            }
            catch { }
        }

        // JetBrains IDE 自带的 JBR（PyCharm / IDEA / CLion / Rider 等），很多机器上只有这一份 Java 21
        try
        {
            var drive = Path.GetPathRoot(AppPaths.ExeDir);
            if (!string.IsNullOrEmpty(drive))
            {
                foreach (var dir in Directory.EnumerateDirectories(drive, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(dir);
                    if (!(name.Contains("PyCharm", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("IntelliJ", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("IDEA", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("CLion", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("Rider", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("WebStorm", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("Android Studio", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    foreach (var sub in new[] { "jbr", "jre", "runtime" })
                    {
                        var exe = Path.Combine(dir, sub, "bin", "java.exe");
                        if (File.Exists(exe)) candidates.Add((exe, "JetBrains JBR"));
                    }
                }
            }
        }
        catch { }

        // Minecraft 自带 runtime 与版本内 runtime
        foreach (var runtimeRoot in new[]
        {
            Path.Combine(mcDir, "runtime"),
            Path.Combine(mcDir, "versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime")
        })
        {
            if (!Directory.Exists(runtimeRoot)) continue;
            try
            {
                foreach (var exe in Directory.EnumerateFiles(runtimeRoot, "java.exe", SearchOption.AllDirectories)
                             .Take(400))
                {
                    if (!exe.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add((exe, "Minecraft Runtime"));
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            candidates.Add((preferredPath, "自定义"));

        await Task.Run(() =>
        {
            foreach (var (path, source) in candidates)
            {
                string full;
                try { full = Path.GetFullPath(path); } catch { continue; }
                if (found.ContainsKey(full)) continue;
                var info = Inspect(full, source);
                if (info is not null) found[full] = info;
            }
        });

        var list = found.Values
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_lock)
        {
            _all = list;
            _scanned = true;
        }
        Log.Info($"Java 扫描完成，共 {list.Count} 个：" + string.Join("；", list.Select(j => $"{j.Major}@{(File.Exists(j.Path) ? j.Path : "?")}")));
        return list;
    }

    public bool HasScanned { get { lock (_lock) return _scanned; } }

    private static void AddFromRegistry(RegistryKey hive, string sub, List<(string, string)> list)
    {
        try
        {
            using var key = hive.OpenSubKey(sub);
            if (key is null) return;
            foreach (var name in key.GetSubKeyNames())
            {
                using var ver = key.OpenSubKey(name);
                var home = ver?.GetValue("JavaHome") as string;
                if (string.IsNullOrWhiteSpace(home)) home = ver?.GetValue("InstallationPath") as string;
                if (!string.IsNullOrWhiteSpace(home))
                {
                    var exe = Path.Combine(home, "bin", "java.exe");
                    if (File.Exists(exe)) list.Add((exe, "注册表"));
                }
            }
        }
        catch { }
    }

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    public static JavaInfo? Inspect(string javaExe, string source)
    {
        try
        {
            if (!File.Exists(javaExe)) return null;
            var home = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(javaExe)!, ".."));
            string version = "", arch = "";
            var release = Path.Combine(home, "release");
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    var t = line.Trim();
                    if (t.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        version = t["JAVA_VERSION=".Length..].Trim('"');
                    else if (t.StartsWith("OS_ARCH=", StringComparison.Ordinal))
                        arch = t["OS_ARCH=".Length..].Trim('"');
                }
            }

            var major = ParseMajor(version);
            if (major <= 0)
            {
                var output = RunJavaVersion(javaExe);
                if (!string.IsNullOrEmpty(output))
                {
                    var m = Regex.Match(output, "version \"([^\"]+)\"");
                    if (m.Success) version = m.Groups[1].Value;
                    if (output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)) arch = "x86_64";
                    major = ParseMajor(version);
                }
            }
            if (major <= 0) major = 8;

            return new JavaInfo
            {
                Path = javaExe,
                Version = version,
                Major = major,
                Arch = arch,
                Source = source
            };
        }
        catch { return null; }
    }

    private static string RunJavaVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); } catch { }
                return text;
            }
            return text;
        }
        catch { return ""; }
    }

    public static int ParseMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var m = Regex.Match(version, @"^(\d+)");
        if (!m.Success) return 0;
        var major = int.Parse(m.Groups[1].Value);
        if (major == 1)
        {
            var m2 = Regex.Match(version, @"^1\.(\d+)");
            if (m2.Success) return int.Parse(m2.Groups[1].Value);
        }
        return major;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    public static long TotalMemoryMb()
    {
        try
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status)) return (long)(status.ullTotalPhys / 1024 / 1024);
        }
        catch { }
        return 8192;
    }

    public static int RecommendMemoryMb(long totalMb)
    {
        // 保守推荐：不超过物理内存 60%，留出系统与整合包空间
        var recommend = (int)(totalMb * 0.55 / 512) * 512;
        if (recommend < 2048) recommend = 2048;
        if (recommend > 16384) recommend = 16384;
        return recommend;
    }
}
