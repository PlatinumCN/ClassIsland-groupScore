using System.Runtime.InteropServices;

namespace GroupScorePlugin.Services;

/// <summary>
/// SQLite 的原生库（e_sqlite3.dll）加载器。
///
/// **为什么需要它**：SQLite 的核心是 C 写的，托管包 Microsoft.Data.Sqlite 只是个壳，
/// 真正干活的是原生 e_sqlite3.dll，由 DllImport 在**第一次打开连接时**才去找。
/// 平时这个查找由运行时按「程序集所在目录」等位置自动完成，
/// 但 ClassIsland 是用自定义 AssemblyLoadContext 加载插件的，
/// 它未必会把插件目录交给原生库探测 —— 那样就会在老师点「+1」的瞬间
/// 抛 DllNotFoundException，编译期一点征兆都没有。
///
/// **做法**：在打开任何数据库之前，先自己用绝对路径把 e_sqlite3.dll 加载进进程。
/// Windows 的模块加载是按文件名去重的，之后 DllImport("e_sqlite3") 会直接命中
/// 这个已加载的模块，不再依赖运行时的目录探测。
///
/// **找不到时不抛异常**：只把结果记下来，由上层回落到 JSON 后端 ——
/// 插件照常能用，数据照常有地方存，绝不因为一个原生库把老师的记分功能弄挂。
/// </summary>
internal static class SqliteNativeLoader
{
    internal const string NativeFileName = "e_sqlite3.dll";

    private static readonly object Gate = new();
    private static bool _attempted;
    private static bool _loaded;
    private static string _detail = "尚未尝试加载";

    /// <summary>是否已经成功把原生库加载进进程。</summary>
    public static bool IsLoaded => _loaded;

    /// <summary>人类可读的结果说明：发给日志、显示在设置页、也供自检台断言。</summary>
    public static string Detail => _detail;

    /// <summary>一次加载尝试的结论。</summary>
    public readonly record struct LoadResult(bool Loaded, string Detail);

    /// <summary>
    /// 候选路径的**顺序**（纯字符串运算，不碰文件系统，可在自检台里直接验顺序）。
    /// 排在前面的优先：
    /// 1. 平铺在插件根目录的那份 —— 构建脚本（FlattenSqliteNativeLibrary）保证它存在，
    ///    这也是刻意设计的**主路线**，不依赖宿主怎么加载插件；
    /// 2. NuGet 默认的 runtimes 布局 —— 万一有人手工部署漏了平铺那份；
    /// 3. 宿主程序目录 —— F5 调试时插件输出目录常与宿主目录重合。
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string? pluginDirectory, string? hostDirectory)
    {
        var list = new List<string>(3);
        Add(pluginDirectory);
        if (!string.IsNullOrEmpty(pluginDirectory))
            Add(Path.Combine(pluginDirectory, "runtimes", "win-x64", "native"));
        Add(hostDirectory);
        return list;

        void Add(string? directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            var path = Path.Combine(directory, NativeFileName);
            // 去重：插件目录与宿主目录常常是同一个（自检台、F5 调试都是）
            if (list.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(path);
        }
    }

    /// <summary>加载原生库。可重复调用，真正只尝试一次。</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;
            var result = TryLoad(ResolvePluginDirectory(), AppContext.BaseDirectory);
            _loaded = result.Loaded;
            _detail = result.Detail;
        }
    }

    /// <summary>
    /// 按候选顺序逐个尝试。刻意接收两个目录参数而不是自己去猜，
    /// 这样自检台能喂任意目录进来真跑一遍（包括「目录全是空的」这种失败路径）。
    /// </summary>
    public static LoadResult TryLoad(string? pluginDirectory, string? hostDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return new LoadResult(false, "非 Windows 平台，交由运行时默认机制处理");

        var candidates = CandidatePaths(pluginDirectory, hostDirectory);
        if (candidates.Count == 0)
            return new LoadResult(false, $"没有任何候选目录，找不到 {NativeFileName}");

        var attempted = new List<string>();
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                attempted.Add($"{path}（文件不存在）");
                continue;
            }

            try
            {
                NativeLibrary.Load(path);
                return new LoadResult(true, $"已加载 {path}");
            }
            catch (Exception ex)
            {
                // 架构不匹配（BadImageFormatException）、被占用等，换下一个候选继续试
                attempted.Add($"{path}（{ex.GetType().Name}：{ex.Message}）");
            }
        }

        return new LoadResult(false, $"未能加载 {NativeFileName} —— " + string.Join("；", attempted));
    }

    /// <summary>插件程序集自己所在的目录。拿不到就退回宿主目录。</summary>
    private static string ResolvePluginDirectory()
    {
        try
        {
            var location = typeof(SqliteNativeLoader).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch
        {
            // 单文件发布 / 动态程序集等场合 Location 会抛异常，退回宿主目录即可
        }
        return AppContext.BaseDirectory;
    }
}
