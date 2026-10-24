using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// 插件全局设置的读写。只负责 settings.json 这一个文件。
///
/// 注意：它刻意不碰积分数据。
/// 如果这里顺手 new 一个 ScoreStore，就会和 DI 容器里那个单例 ScoreStore
/// 形成两份互不相知的内存副本，一边加分另一边不知道，最后互相覆盖 —— 数据就废了。
/// 需要积分数据的地方请一律从容器取同一个 ScoreStore 单例。
/// </summary>
public class SettingsService
{
    private readonly string _settingsPath;
    private readonly object _gate = new();

    /// <summary>当前设置对象。直接改属性即可，会自动保存。</summary>
    public PluginSettings Settings { get; }

    public SettingsService(string pluginConfigFolder)
    {
        Directory.CreateDirectory(pluginConfigFolder);
        _settingsPath = Path.Combine(pluginConfigFolder, "settings.json");
        Settings = JsonStore.Load<PluginSettings>(_settingsPath);

        // 属性一变就存，省得每个调用点都要记得手动保存
        Settings.PropertyChanged += (_, _) => Save();
    }

    /// <summary>立刻保存当前设置。</summary>
    public void Save()
    {
        lock (_gate)
        {
            JsonStore.Save(_settingsPath, Settings);
        }
    }
}
