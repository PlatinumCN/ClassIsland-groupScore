namespace GroupScorePlugin.Models;

/// <summary>
/// 成员积分列表里的一行（显示模型，不落盘）。
///
/// 注意它和 <see cref="Member"/> 的分工：Member 只存「这个人是谁」，
/// 个人总分是从积分记录现算出来的，不写在成员身上。
/// 原因是撤销的实现是「删掉最后一条记录」，如果成员也缓存一份总分，
/// 撤销时就得同时维护两份缓存，多一条能不同步的路。
/// 小组总分之所以敢缓存，是因为排行榜要频繁渲染；
/// 成员分只在打开成员页和悬浮球面板时算一次，现算完全够快。
/// </summary>
public class MemberScoreItem
{
    /// <summary>组内个人名次。同分并列，规则与小组排行榜一致（1、2、2、4）。</summary>
    public int Rank { get; set; }

    /// <summary>外键 → Member.Id。</summary>
    public string MemberId { get; set; } = "";

    /// <summary>所属小组 Id。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>成员姓名。</summary>
    public string Name { get; set; } = "";

    /// <summary>个人净得分（只统计指名到这个人的那些记录）。</summary>
    public int Total { get; set; }

    /// <summary>下拉框 / 列表用的显示文本。</summary>
    public string DisplayText => $"{Name}　({Total} 分)";
}
