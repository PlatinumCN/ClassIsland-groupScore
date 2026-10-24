using System.Text.Json.Serialization;

namespace GroupScorePlugin.Triggers;

/// <summary>
/// 阈值的比较方向。
///
/// 用枚举而不是 bool，是因为「达到」和「跌破」在老师心里是两个完全不同的场景，
/// 界面上摆一个下拉框比摆一个「反向」复选框好懂得多。
/// </summary>
public enum ThresholdComparison
{
    /// <summary>总分 <c>&gt;=</c> 阈值时满足（达标了）。</summary>
    AtLeast = 0,

    /// <summary>总分 <c>&lt;=</c> 阈值时满足（掉下来了）。</summary>
    AtMost = 1
}

/// <summary>
/// 「小组积分达到阈值」触发器的设置。
///
/// 跟 <c>AddScoreActionSettings</c> 一样，这个类会被 ClassIsland 序列化进自动化配置，
/// 所以必须是能被 System.Text.Json 往返的普通类：公开无参构造 + 公开可读写属性。
///
/// 也刻意不引用任何 ClassIsland 类型 —— 这样 _SelfTest 那个独立自检台
/// 能把它直接编进去测默认值与序列化往返（触发器本体做不到这一点，
/// 它必须继承 TriggerBase）。
/// </summary>
public class GroupScoreThresholdTriggerSettings
{
    /// <summary>
    /// 只盯着哪个小组看。null 或空字符串表示「任意小组」——
    /// 只要有一个组满足条件就算满足。
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>阈值分数线。</summary>
    public int Threshold { get; set; } = 10;

    /// <summary>比较方向，默认「达到或超过」。</summary>
    public ThresholdComparison Comparison { get; set; } = ThresholdComparison.AtLeast;

    /// <summary>界面下拉框里的「任意小组」那一项（等价于 GroupId 为空）。</summary>
    [JsonIgnore]
    public bool AnyGroup => string.IsNullOrEmpty(GroupId);

    /// <summary>
    /// 把设置渲染成一句人话，给设置界面的提示区用。
    /// </summary>
    public string Describe(string? groupName)
    {
        var who = AnyGroup ? "任意小组" : (groupName ?? "已删除的小组");
        var op = Comparison == ThresholdComparison.AtLeast ? "达到或超过" : "掉到" ;
        var tail = Comparison == ThresholdComparison.AtLeast ? "分" : "分及以下";
        return $"{who}的积分{op} {Threshold} {tail}";
    }
}
