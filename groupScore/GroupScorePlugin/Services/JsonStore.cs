using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GroupScorePlugin.Services;

/// <summary>
/// JSON 读写小工具。两个关键点：
/// 1. 原子写 —— 先写 .tmp 再整体替换，避免写到一半断电把原文件毁掉。
/// 2. 中文不转义 —— 默认的序列化器会把中文写成 \uXXXX，人肉查看文件时很难受。
/// </summary>
internal static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 容忍手工编辑配置文件时多出来的尾逗号
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>读取。文件不存在或损坏时返回一个全新的默认实例，绝不抛异常打断插件加载。</summary>
    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception ex)
        {
            // 损坏的文件改名留档，方便事后人工抢救，然后从空白开始
            TryBackupCorrupted(path, ex);
            return new T();
        }
    }

    /// <summary>原子写入。</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
            // 覆盖式替换，同目录内的 move 在 NTFS 上是原子的
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 落盘失败不应该让 ClassIsland 崩掉；数据还在内存里，下次改动会再试一次
        }
    }

    private static void TryBackupCorrupted(string path, Exception ex)
    {
        try
        {
            if (!File.Exists(path)) return;
            var bak = $"{path}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, bak, overwrite: true);
        }
        catch
        {
            // 备份失败就算了，不要因为备份把主流程搞崩
        }
    }
}
