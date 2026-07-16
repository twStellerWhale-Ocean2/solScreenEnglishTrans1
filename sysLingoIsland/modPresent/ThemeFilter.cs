using System.IO;
using LingoIsland.Query;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Thickness = System.Windows.Thickness;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextTrimming = System.Windows.TextTrimming;
using Stretch = System.Windows.Media.Stretch;
using BitmapImage = System.Windows.Media.Imaging.BitmapImage;
using BitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;

namespace LingoIsland.Present;

/// <summary>
/// 「依 theme 篩選」圖文下拉之共用填充（epic #145 「多媒體主題管理·B」）：供截圖清單／影片清單頂端套用。
/// 下拉項＝「All themes」＋各主題（縮圖＋名稱）；各項 <see cref="ComboBoxItem.Tag"/>＝ThemeId（All＝<see cref="AllTag"/>）。
/// 篩選比對為純函式（<see cref="Match"/>，可單元測試）；UI 填充不依賴 store 內部、以 <see cref="ThemeStore"/> 取清單與圖片路徑。
/// </summary>
internal static class ThemeFilter
{
    /// <summary>「All themes」項之 Tag 哨兵（不會與真 ThemeId〔GUID N〕相撞）。</summary>
    public const string AllTag = "\0ALL";

    /// <summary>內容區塊「所屬主題」下拉之「(No theme)」項 Tag 哨兵（#173；不會與真 ThemeId 相撞）。</summary>
    public const string NoneTag = "\0NONE";

    /// <summary>清單項是否應顯示：<paramref name="filterThemeId"/> 為 null（All）恆真，否則需與 <paramref name="itemThemeId"/> 相符。純函式。</summary>
    public static bool Match(string? filterThemeId, string? itemThemeId) =>
        filterThemeId is null || string.Equals(filterThemeId, itemThemeId, StringComparison.Ordinal);

    /// <summary>以「All themes」＋各主題（縮圖＋名稱）重填下拉；保留原選取（依 Tag，該主題已不在則回 All）。</summary>
    public static void Populate(ComboBox combo, ThemeStore store)
    {
        var prev = SelectedThemeId(combo);
        combo.Items.Clear();
        combo.Items.Add(MakeItem(AllTag, "All themes", null));
        foreach (var t in store.Load().Items)
        {
            var img = string.IsNullOrEmpty(t.Image) ? null : store.ImagePathFor(t.Image!);
            combo.Items.Add(MakeItem(t.Id, string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name, img));
        }
        SelectByTag(combo, prev ?? AllTag);
    }

    /// <summary>目前選取之 ThemeId（All／無選取＝null）。</summary>
    public static string? SelectedThemeId(ComboBox combo)
    {
        var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag is null || tag == AllTag ? null : tag;
    }

    /// <summary>
    /// 以「(No theme)」＋各主題（縮圖＋名稱）重填下拉，選中 <paramref name="selectedThemeId"/>
    /// （null／查無該主題→(No theme)）。供內容區塊「顯示/改指派所屬主題」（#173）。
    /// </summary>
    public static void PopulatePicker(ComboBox combo, ThemeStore store, string? selectedThemeId)
    {
        combo.Items.Clear();
        combo.Items.Add(MakeItem(NoneTag, "(No theme)", null));
        foreach (var t in store.Load().Items)
        {
            var img = string.IsNullOrEmpty(t.Image) ? null : store.ImagePathFor(t.Image!);
            combo.Items.Add(MakeItem(t.Id, string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name, img));
        }
        SelectByTag(combo, selectedThemeId ?? NoneTag); // 查無（主題已刪）→ 回退 (No theme)
    }

    /// <summary>下拉目前選取之 ThemeId（「(No theme)」／無選取＝null）。供內容區塊「所屬主題」指派（#173）。</summary>
    public static string? PickedThemeId(ComboBox combo)
    {
        var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag is null || tag == NoneTag ? null : tag;
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if ((combo.Items[i] as ComboBoxItem)?.Tag as string == tag) { combo.SelectedIndex = i; return; }
        }
        combo.SelectedIndex = 0; // 該主題已刪 → 回 All
    }

    private static ComboBoxItem MakeItem(string tag, string name, string? imagePath)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var thumb = LoadThumb(imagePath);
        if (thumb is not null)
        {
            sp.Children.Add(new Image
            {
                Source = thumb, Width = 20, Height = 20, Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        sp.Children.Add(new TextBlock
        {
            Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return new ComboBoxItem { Content = sp, Tag = tag };
    }

    private static BitmapImage? LoadThumb(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // 不鎖檔
            bi.DecodePixelWidth = 40;                  // 縮圖解碼、省記憶體
            bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }
}
