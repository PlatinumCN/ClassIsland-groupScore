using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace GroupScorePlugin.Models;

/// <summary>
/// 小组。对应数据表 GROUPS。
/// 实现 INotifyPropertyChanged 是为了让积分变化时界面能自动刷新，不必手动重建列表。
/// </summary>
public class Group : INotifyPropertyChanged
{
    private string _name = "";
    private string _color = "#378ADD";
    private int _cachedTotal;

    /// <summary>主键。用 32 位无横线 GUID，避免和别的小组撞车。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>小组名称，如「第一小组」。</summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>小组颜色（十六进制），用于积分榜上的色块标识。</summary>
    public string Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    /// <summary>显示顺序，由「上移 / 下移」维护。</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 总分缓存。唯一合法的修改者是 ScoreService，它会在写入记录时同步维护此字段。
    /// 之所以不每次现算，是因为排行榜要频繁渲染，逐条求和会随记录数增长越来越慢。
    /// </summary>
    public int CachedTotal
    {
        get => _cachedTotal;
        set { _cachedTotal = value; OnPropertyChanged(); }
    }

    /// <summary>给界面用的显示文本。</summary>
    [JsonIgnore]
    public string DisplayText => $"{Name}　({CachedTotal} 分)";

    /// <summary>
    /// 供 XAML 直接绑定的色块画刷。
    /// 做成 IBrush 而不是颜色字符串，是因为绑定时自动做 string→IBrush 转换并不可靠。
    /// </summary>
    [JsonIgnore]
    public IBrush Brush
    {
        get
        {
            if (_brush != null) return _brush;
            try
            {
                // 全限定写 Avalonia.Media.Color：本类的 Color 属性会遮蔽同名类型
                _brush = new SolidColorBrush(Avalonia.Media.Color.Parse(Color));
            }
            catch
            {
                _brush = new SolidColorBrush(Colors.Gray);
            }
            return _brush;
        }
    }

    private IBrush? _brush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
