using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// JSON 后端。就是把原来那套 JsonStore 包一层，套上 <see cref="IScorePersistence"/> 的壳。
///
/// 它的新职责不只是「备选方案」：现在它同时是 **SQLite 的回退位**。
/// 一旦 SQLite 那侧有任何一环不可用（原生库加载失败、库文件损坏、目录不可写），
/// ScoreStore 就会静默换到这条路上来 —— 这也是为什么这个类必须保持原样可用、
/// 不能被当成「过时的旧代码」删掉。
/// </summary>
internal sealed class JsonScorePersistence : IScorePersistence
{
    private readonly string _filePath;

    public JsonScorePersistence(string filePath) => _filePath = filePath;

    public string BackendName => "JSON";

    public string Location => _filePath;

    public ScoreDatabase Load() => JsonStore.Load<ScoreDatabase>(_filePath);

    // JsonStore.Save 本身就是原子写（先写 .tmp 再整体替换），
    // 满足 IScorePersistence 对「原子」的要求，不用再包一层。
    public void Save(ScoreDatabase data) => JsonStore.Save(_filePath, data);
}
