using System.Text.Json.Serialization;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 「小组加减分」行动的设置。
///
/// 这个类会被 ClassIsland 存进自动化配置里（<c>ActionItem.Settings</c>），
/// 所以它是**跨重启、跨行动实例**的持久状态。两条硬要求：
/// <list type="number">
/// <item>必须是能被 System.Text.Json 往返的普通类：公开无参构造 + 公开可读写属性。
/// 框架在读取时是这么重建它的：<c>Activator.CreateInstance(settingsType)</c> + <c>json.Deserialize</c>。</item>
/// <item>只读的计算属性要标 <see cref="JsonIgnoreAttribute"/>，否则会被写进配置文件当噪声。</item>
/// </list>
///
/// 顺便：这个文件刻意不引用任何 ClassIsland 类型，
/// 所以它可以被 _SelfTest 那个独立自检台编译进去，直接测序列化往返。
/// </summary>
public class AddScoreActionSettings
{
    /// <summary>
    /// 目标小组 Id。为 null 或空字符串表示「全部小组」。
    ///
    /// 存字符串而不是整个小组对象：小组被删掉之后这个 Id 会指向不存在的组，
    /// 执行时按「跳过」处理 —— 教室里的自动化不该因为一个被删掉的组就整天报错。
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>变动量。正数加分、负数扣分，0 表示什么都不做。</summary>
    public int Delta { get; set; } = 1;

    /// <summary>写进积分记录的原因。空白时由行动回退成「自动化」。</summary>
    public string Reason { get; set; } = "自动化";

    /// <summary>
    /// 上一次「加分」真正动过哪些小组。
    ///
    /// 这个字段存在的唯一理由是**撤销必须精确**：
    /// ClassIsland 每次运行行动都从 DI 取一个**新的**行动实例（按瞬态注册），
    /// 「执行」和之后的「恢复」很可能根本不是同一个对象 ——
    /// 用 C# 字段记状态，恢复时读到的会是默认值。
    /// 但 Settings 背后就是 <c>ActionItem.Settings</c>，会被持久化，
    /// 所以要把「这次动了谁」写在这里，恢复时才读得到。
    ///
    /// 直接后果：中途新建的小组不会被误扣分。
    /// </summary>
    public List<string> AffectedGroupIds { get; set; } = new();

    /// <summary>是否作用于全部小组（界面下拉框里的「全部小组」那一项）。</summary>
    [JsonIgnore]
    public bool ApplyToAllGroups => string.IsNullOrEmpty(GroupId);
}
