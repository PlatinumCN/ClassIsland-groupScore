using Avalonia.Media;

namespace GroupScorePlugin.Models;

/// <summary>
/// 排行榜里的一行（显示模型，不落盘）。
/// 名次并列在这里就被算好，界面直接拿来用。
/// </summary>
public class RankItem
{
    private IBrush? _brush;

    /// <summary>名次。同分并列，如 1、2、2、4。</summary>
    public int Rank { get; set; }

    /// <summary>小组 Id，点击行时用来定位。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>小组名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>总分。</summary>
    public int Total { get; set; }

    /// <summary>今天的净变化量。</summary>
    public int TodayDelta { get; set; }

    /// <summary>今日变化的显示文本，为 0 时留空以免干扰阅读。</summary>
    public string TodayText =>
        TodayDelta > 0 ? $"+{TodayDelta}" :
        TodayDelta < 0 ? TodayDelta.ToString() : "";

    /// <summary>
    /// 今日变化的颜色。按国内习惯：加分用红、扣分用绿。
    /// </summary>
    public IBrush TodayBrush =>
        TodayDelta > 0 ? new SolidColorBrush(Color.Parse("#E24B4A"))
        : TodayDelta < 0 ? new SolidColorBrush(Color.Parse("#1D9E75"))
        : new SolidColorBrush(Colors.Transparent);

    /// <summary>小组颜色的十六进制字符串。</summary>
    public string ColorHex { get; set; } = "#378ADD";

    /// <summary>
    /// 供 XAML 绑定的画刷。这里直接给出 IBrush 而不是颜色字符串，
    /// 是因为绑定时自动做 string→IBrush 的类型转换并不可靠，自己转最稳。
    /// </summary>
    public IBrush Brush
    {
        get
        {
            if (_brush != null) return _brush;
            try
            {
                _brush = new SolidColorBrush(Color.Parse(ColorHex));
            }
            catch
            {
                _brush = new SolidColorBrush(Colors.Gray);
            }
            return _brush;
        }
    }
}
