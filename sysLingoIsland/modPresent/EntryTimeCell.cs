using System;
using System.Globalization;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Thickness = System.Windows.Thickness;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextAlignment = System.Windows.TextAlignment;

namespace LingoIsland.Present;

/// <summary>
/// 筆記/歷史條目登記時間之共用顯示（#123：統一兩頁版型、日期字形縮小、改兩行避免過寬）——
/// 第一行 <c>yyyy-MM-dd</c>（年月日、小字），第二行 <c>HH:mm</c>（時間）；右對齊、靠右欄。
/// tooltip 顯完整含秒時區。單一來源，NotesPage/HistoryPage 共用、不各寫一份。
/// </summary>
internal static class EntryTimeCell
{
    private static readonly Brush Fg =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A6A82"));

    static EntryTimeCell() => Fg.Freeze();

    /// <summary>建立兩行時間格（date 上、time 下）；<paramref name="local"/> 為本地時間。</summary>
    public static StackPanel Build(DateTimeOffset local)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        };
        panel.Children.Add(new TextBlock
        {
            Text = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FontSize = 10, // 日期字形縮小（#123）
            Foreground = Fg,
            TextAlignment = TextAlignment.Right,
        });
        panel.Children.Add(new TextBlock
        {
            Text = local.ToString("HH:mm", CultureInfo.InvariantCulture),
            FontSize = 11.5,
            Foreground = Fg,
            TextAlignment = TextAlignment.Right,
        });
        return panel;
    }
}
