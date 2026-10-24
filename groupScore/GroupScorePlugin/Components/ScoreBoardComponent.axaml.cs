using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Components;

/// <summary>
/// 积分榜组件：摆在 ClassIsland 主界面上的实时排行榜。
///
/// 关于「GUID」：ComponentInfo 第一个参数是组件的唯一身份。
/// ClassIsland 保存主界面布局时记的是这个 GUID，
/// 所以一旦发布过、老师摆好过位置，就千万别再改它，
/// 否则所有人的摆位都会失效。真要改必须加 [MigrateFrom] 做迁移。
///
/// 关于「为什么不做组件级设置」：
/// ClassIsland 的组件设置是「每个摆放实例各自独立」的，
/// 如果让每个积分榜组件各自配显示条数，主界面上放两个就会互相矛盾。
/// 因此显示条数统一由插件设置页控制，组件只负责展示。
/// </summary>
[ComponentInfo(
    "6F2A5C81-3D74-4E19-9B02-A7C4E8D15F36",
    "小组积分榜",
    "\uf391",                     // ClassIsland.Core.Icons.FluentIconKind.TrophyRegular
    "显示各小组的当前积分与今日变化，实时刷新。")]
public partial class ScoreBoardComponent : ComponentBase
{
    private ScoreService? _score;
    private SettingsService? _settings;
    private bool _subscribed;

    public ScoreBoardComponent()
    {
        InitializeComponent();

        // 用 Loaded / Unloaded 而不是构造函数来订阅：
        // 组件被摆到主界面上时才需要刷新，被移除时必须退订，否则会内存泄漏。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_subscribed) return;

        // 用服务定位器而不是构造函数注入：组件的构造时机由 ClassIsland 控制，
        // 这样可以确定拿到的一定是容器里那个单例，也避免构造期访问容器带来的顺序问题。
        _score = IAppHost.TryGetService<ScoreService>();
        _settings = IAppHost.TryGetService<SettingsService>();
        if (_score == null) return;

        _score.ScoresChanged += OnScoresChanged;
        if (_settings != null)
            _settings.Settings.PropertyChanged += OnSettingsChanged;

        _subscribed = true;
        Refresh();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;

        if (_score != null) _score.ScoresChanged -= OnScoresChanged;
        if (_settings != null)
            _settings.Settings.PropertyChanged -= OnSettingsChanged;

        _subscribed = false;
    }

    private void OnScoresChanged(object? sender, EventArgs e) => Refresh();

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    /// <summary>
    /// 重新拉取排行榜。
    /// 注意是整表重绑而不是增量更新 —— 名次会因为一次加分而整体变化，
    /// 增量更新的复杂度远高于收益，课堂规模的数据量重绑一次几乎无感。
    /// </summary>
    private void Refresh()
    {
        if (_score == null) return;

        var topN = _settings?.Settings.BoardTopN ?? -1;
        var items = _score.GetRanking(topN);

        RankList.ItemsSource = items;
        EmptyHint.IsVisible = _score.Data.Groups.Count == 0;
    }
}
