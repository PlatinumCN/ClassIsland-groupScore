using System.Text.Json.Serialization;

namespace GroupScorePlugin.Models;

/// <summary>
/// 整个积分数据库的聚合根，包含四张表。
///
/// 设计取舍：四张表统一放在一个 JSON 文档里，而不是四个文件。
/// 因为「加一次分」会同时改动 ScoreRecords（新增一条）和 Groups（更新缓存总分），
/// 分成四个文件就要么写四次、要么写两次以上才有事务性，
/// 中途断电就会出现「记录写进去了但总分没更新」的坏数据。
/// 一个文件 = 一次原子写入 = 永远不会写坏一半。
/// 等记录量真的上万了，再考虑换成 SQLite，那时四张表自然就变成四张真表。
/// </summary>
public class ScoreDatabase
{
    /// <summary>数据结构版本号，将来字段变了靠它做迁移。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 是否已经播过初始示例数据。
    /// 用它而不是「小组数量是否为 0」来判断，是为了让老师把示例小组删光之后，
    /// 下次启动不会再被强行塞回来。
    /// </summary>
    public bool Seeded { get; set; }

    /// <summary>表 1：小组。排行榜和加减分的主体。</summary>
    public List<Group> Groups { get; set; } = new();

    /// <summary>表 2：小组成员。可选，用于把积分追溯到个人。</summary>
    public List<Member> Members { get; set; } = new();

    /// <summary>表 3：积分变动记录。列表顺序即时间顺序，最后一条是最新的。</summary>
    public List<ScoreRecord> ScoreRecords { get; set; } = new();

    /// <summary>表 4：预设积分项，对应悬浮球面板和设置页上的快捷按钮。</summary>
    public List<ScorePreset> Presets { get; set; } = new();

    // ---------- 便于查询的派生集合（不参与序列化） ----------

    /// <summary>按显示顺序排列的小组。</summary>
    [JsonIgnore]
    public IEnumerable<Group> OrderedGroups =>
        Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name);
}
