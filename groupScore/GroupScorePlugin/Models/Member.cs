namespace GroupScorePlugin.Models;

/// <summary>
/// 小组成员。对应数据表 MEMBERS。
/// 这一张表是可选的：如果你只按「小组」加减分，不追溯到具体个人，
/// 那么这张表可以一直是空的，程序其余部分不受影响。
///
/// 刻意不实现 INotifyPropertyChanged：
/// 小组需要它是因为积分榜要随分数自动刷新，而成员列表是「重建成整张表」的，
/// 改名 / 加减分都会走 ScoresChanged → 重建列表，不需要单条通知。
/// </summary>
public class Member
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>外键 → Group.Id。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>成员姓名。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 组内显示顺序，由「上移 / 下移」维护。
    /// 这个字段是第 4 步才加的：老数据里没有它，反序列化后一律为 0，
    /// 于是 ScoreStore 加载时会做一次 <see cref="MemberScores.NormalizeOrder"/>，
    /// 把「全是 0」退化成原来的列表顺序，而不是让它变成随机顺序。
    /// </summary>
    public int SortOrder { get; set; }
}
