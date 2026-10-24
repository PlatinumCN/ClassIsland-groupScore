using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GroupScorePlugin.Models;

/// <summary>
/// 插件全局设置。存在插件配置目录下的 settings.json，
/// 属性一改就自动落盘（靠 SettingsService 订阅 PropertyChanged 实现）。
/// </summary>
public class PluginSettings : INotifyPropertyChanged
{
    private bool _autoShowOnClass = true;
    private bool _autoHideOnBreak = true;
    private string _corner = "BottomRight";
    private int _boardTopN = 6;
    private double _ballOpacity = 0.92;
    private int? _ballX;
    private int? _ballY;
    private int _weekStartsOn = PeriodStats.DefaultWeekStartsOn;
    private bool _restrictScoreToClassTime = true;
    private bool _ballSubjectFilter;
    private List<string> _ballSubjects = new();
    private string _scorePanelStyle = ScorePanelStyles.Default;

    /// <summary>上课时自动弹出悬浮球。</summary>
    public bool AutoShowOnClass
    {
        get => _autoShowOnClass;
        set { _autoShowOnClass = value; OnPropertyChanged(); }
    }

    /// <summary>下课 / 放学时自动收起悬浮球。</summary>
    public bool AutoHideOnBreak
    {
        get => _autoHideOnBreak;
        set { _autoHideOnBreak = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 悬浮球停靠的屏幕角落：TopLeft / TopRight / BottomLeft / BottomRight。
    /// 只有在 <see cref="HasCustomPosition"/> 为 false 时才起作用（老师没拖过球）。
    /// </summary>
    public string Corner
    {
        get => _corner;
        set { _corner = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 老师拖动后记住的悬浮球左上角 X 坐标（屏幕像素）。
    /// 为 null 表示没拖过，按 <see cref="Corner"/> 停靠。
    /// </summary>
    public int? BallX
    {
        get => _ballX;
        set { _ballX = value; OnPropertyChanged(); }
    }

    /// <summary>拖动后记住的悬浮球左上角 Y 坐标，与 <see cref="BallX"/> 同生同灭。</summary>
    public int? BallY
    {
        get => _ballY;
        set { _ballY = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否已经用拖动定过位置。
    /// 判据必须和 BallPlacement.Resolve 保持一致：**两个坐标都有**才算数，
    /// 只有半个坐标（手改配置 / 写到一半）会被当成没拖过。
    ///
    /// [JsonIgnore] 是必须的：这是只读属性，System.Text.Json 默认仍会把它序列化出去，
    /// 于是 settings.json 里会多一个永远读不回来的 HasCustomPosition 噪音字段。
    /// </summary>
    [JsonIgnore]
    public bool HasCustomPosition => BallX.HasValue && BallY.HasValue;

    /// <summary>清掉拖动记忆，恢复成按角落停靠。</summary>
    public void ClearCustomPosition()
    {
        BallX = null;
        BallY = null;
    }

    /// <summary>积分榜组件显示前几名，-1 表示全部显示。</summary>
    public int BoardTopN
    {
        get => _boardTopN;
        set { _boardTopN = value; OnPropertyChanged(); }
    }

    /// <summary>悬浮球不透明度，1.0 为完全不透明。</summary>
    public double BallOpacity
    {
        get => _ballOpacity;
        set { _ballOpacity = Math.Clamp(value, 0.3, 1.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 历史对比里「一周」从周几算起（用 DayOfWeek 的数值：0=周日、1=周一）。
    ///
    /// 它只影响**怎么切分**已经记录下来的数据，不会改动任何一条记录 ——
    /// 改它之后上周的区间会平移，历史对比里的数字跟着变，但总分一分不差。
    /// 默认周一，符合国内课表习惯。
    /// </summary>
    public int WeekStartsOn
    {
        get => _weekStartsOn;
        // 归一化后再存：手改配置文件塞进来的非法值（-1 / 9）不该让整个周期算术崩掉
        set { _weekStartsOn = PeriodStats.NormalizeWeekStart(value); OnPropertyChanged(); }
    }

    /// <summary>
    /// 只在上课时才允许加分。默认开。
    ///
    /// 「课表不可用就自动失效」这条兜底写在 <see cref="LessonRules.CanAddScore"/> 里：
    /// 老师可能压根没在 ClassIsland 里配课表，那种情况下时段永远停在「放学后」，
    /// 照着卡就等于插件变成点不动的砖头 —— 宁可放开。
    /// </summary>
    public bool RestrictScoreToClassTime
    {
        get => _restrictScoreToClassTime;
        set { _restrictScoreToClassTime = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否只在 <see cref="BallSubjects"/> 列出的科目上课时才自动弹出悬浮球。
    /// 关掉 = 老行为（任何课都弹）。
    /// </summary>
    public bool BallSubjectFilter
    {
        get => _ballSubjectFilter;
        set { _ballSubjectFilter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 允许自动弹出悬浮球的科目名，就是课表里显示的那个名字（如「语文」「数学」）。
    /// **开着筛选但这里是空的 = 谁都不弹**，这是有意为之，不是缺陷。
    ///
    /// 改这个列表的规矩是**整体替换**（赋一个新 List），不要原地 Add/Remove ——
    /// INPC 只在属性赋值那一下发通知，原地改集合的话界面收不到，
    /// 会出现「勾了框但设置没存上」这种最难查的静默失败。
    /// </summary>
    public List<string> BallSubjects
    {
        get => _ballSubjects;
        // 手改配置文件写成 null 也要能活：后面的筛选逻辑会直接遍历它
        set { _ballSubjects = value ?? new List<string>(); OnPropertyChanged(); }
    }

    /// <summary>
    /// 加分面板的样式：逐个选择 / 全部展开。见 <see cref="ScorePanelStyles"/>。
    ///
    /// 存字符串而不是枚举，是因为枚举在 JSON 里是数字：手改配置写了个 7、
    /// 或者将来枚举顺序调整过，都会静默变成另一个样式（或者直接反序列化失败）。
    /// 存字符串 + 归一化，最坏情况只是回到默认样式。
    /// </summary>
    public string ScorePanelStyle
    {
        get => _scorePanelStyle;
        set { _scorePanelStyle = ScorePanelStyles.Normalize(value); OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
