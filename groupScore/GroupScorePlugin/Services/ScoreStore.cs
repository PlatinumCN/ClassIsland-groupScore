using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// 数据层：负责积分数据库的加载与保存。
///
/// 核心策略是「内存为主、落盘为辅 + 防抖」：
/// 内存里的 ScoreDatabase 才是运行时唯一被读写的对象，
/// 标记脏数据后延迟一小会儿才真正写盘。
/// 这样连点十次「+1」只会产生一次磁盘写入，而不是十次。
///
/// **落盘交给哪个后端，由 <see cref="ScorePersistenceSelector"/> 在构造时决定**：
/// 正常情况是 SQLite，任何一环不可用都会静默回落到 JSON。
/// 本类刻意对后端一无所知 —— 它只认 <see cref="IScorePersistence"/> 这个接口，
/// 所以「换存储格式」这件事的影响范围被死死限制在这一个文件里。
/// </summary>
public class ScoreStore : IDisposable
{
    private readonly IScorePersistence _persistence;
    private readonly object _gate = new();
    private readonly Timer _debounce;

    private volatile bool _dirty;
    private bool _disposed;

    /// <summary>内存中的数据库。所有读写都针对它。</summary>
    public ScoreDatabase Data { get; private set; }

    /// <summary>
    /// 数据文件路径，设置页上「打开数据目录」会用到。
    /// 注意它跟着当前后端走：SQLite 后端是 .db，回落 JSON 时是 .json。
    /// </summary>
    public string FilePath => _persistence.Location;

    /// <summary>当前实际生效的存储后端名字（"SQLite" / "JSON"）。</summary>
    public string BackendName => _persistence.BackendName;

    /// <summary>
    /// 存储层的说明：迁移了什么、或因为什么退回了 JSON。正常情况为空串。
    /// 设置页会把它显示出来 —— 存储格式这种事不该让老师去猜。
    /// </summary>
    public string StorageNote { get; }

    /// <summary>
    /// 最近一次落盘失败的原因；成功落盘后清空。
    /// 落盘失败不抛异常（那会让 ClassIsland 整个崩掉），但也不能悄悄咽下去。
    /// </summary>
    public string? LastSaveError { get; private set; }

    public ScoreStore(string pluginConfigFolder)
    {
        Directory.CreateDirectory(pluginConfigFolder);

        var selection = ScorePersistenceSelector.SelectAndLoad(pluginConfigFolder);
        _persistence = selection.Persistence;
        StorageNote = selection.Note;
        Data = selection.Data;

        // 防御性修正：撤销和「最近记录」都依赖「列表顺序 = 时间顺序」这个契约。
        // 正常运行时记录只会往后追加，顺序天然是对的；
        // 但如果有人手工编辑过数据把顺序弄乱了，这里按时间稳定排序一次把它拉回来。
        // OrderBy 是稳定排序，同一毫秒内的相对先后不会被改变。
        //
        // 顺带一提，这个重排在增量落盘那边也是安全的：一旦顺序真的变了，
        // 尾部那条记录的 Id 就对不上，RecordWriteDecision 会自己退到「整表重写」。
        if (Data.ScoreRecords.Count > 1)
            Data.ScoreRecords = Data.ScoreRecords.OrderBy(r => r.CreatedAt).ToList();

        // 同理：成员的 SortOrder 是第 4 步才加的字段，老数据反序列化后全是 0。
        // 归一化一次，把这种「顺序未定」退化成文件里的原始顺序，
        // 否则所有成员并列 0，排序结果虽然稳定但和老师看到的旧顺序无关。
        foreach (var groupId in Data.Members.Select(m => m.GroupId).Distinct().ToList())
            MemberScores.NormalizeOrder(Data.Members.Where(m => m.GroupId == groupId).ToList());

        // 一秒内没有新的改动才落盘
        _debounce = new Timer(_ => FlushIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 标记数据已改变，安排一次延迟保存。
    /// 任何修改 Data 的代码都必须调用它，否则改动只存在于内存中。
    /// </summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        _dirty = true;
        try
        {
            _debounce.Change(1000, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 已在关闭流程中，忽略
        }
    }

    /// <summary>立刻落盘。关闭插件或应用退出前调用，保证最后一次改动不丢。</summary>
    public void SaveNow()
    {
        _dirty = true;
        FlushIfDirty();
    }

    /// <summary>数据是否还在内存里没写盘。</summary>
    public bool HasUnsavedChanges => _dirty;

    private void FlushIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty || _disposed) return;
            try
            {
                _persistence.Save(Data);
                _dirty = false;
                LastSaveError = null;
            }
            catch (Exception ex)
            {
                // 这里最常见的触发路径是防抖定时器的线程 —— 异常一旦逃出去，
                // 崩掉的是整个 ClassIsland，不是这个插件。所以必须在这里拦住。
                // 数据还在内存里，脏标记保持置位，下次改动会再试一遍。
                LastSaveError = $"{ex.GetType().Name}：{ex.Message}";
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SaveNow();
        _disposed = true;
        _debounce.Dispose();
    }
}
