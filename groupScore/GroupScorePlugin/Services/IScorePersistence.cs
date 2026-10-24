using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// 数据落盘后端的统一接口。
///
/// 存在的理由有两个：
/// 1. 让 JSON 与 SQLite 两种后端可以互换 —— 换后端不必动 ScoreStore 一行调用代码；
/// 2. **让 SQLite 出问题时能静默回落** —— 原生库加载不了、数据库文件损坏、
///    目录权限不对，任何一种情况插件都退回去用 JSON 继续跑，
///    而不是让老师记了一节课的分在启动那一刻全部消失。
///
/// 契约：<see cref="Load"/> 返回的对象就是运行时的唯一真相（ScoreStore 会直接改它），
/// <see cref="Save"/> 必须把整份数据**原子地**写下去 —— 要么全是新的，要么全是旧的，
/// 不允许出现「记录写进去了但小组总分还是老的」这种中间态。
/// </summary>
internal interface IScorePersistence
{
    /// <summary>后端名字（"SQLite" / "JSON"），显示在设置页上，便于确认当前用的是哪一种。</summary>
    string BackendName { get; }

    /// <summary>数据落盘位置（文件路径）。设置页的「打开数据目录」用它。</summary>
    string Location { get; }

    /// <summary>读出整份数据。文件不存在返回空库；损坏时尽可能自愈而不是抛异常打断加载。</summary>
    ScoreDatabase Load();

    /// <summary>把整份数据写下去。事务性与增量策略由实现自己负责。</summary>
    void Save(ScoreDatabase data);
}
