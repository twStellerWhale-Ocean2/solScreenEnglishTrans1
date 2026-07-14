using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DependencyObject = System.Windows.DependencyObject;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace LingoIsland.Present;

/// <summary>
/// 清單「右鍵 Delete＋Delete 鍵」刪除之共用支援（#167，取代逐頁底部 Delete 按鈕）：
/// 供截圖清單／影片清單統一以右鍵選單與 Delete 鍵刪除選取項。
/// </summary>
internal static class ListDeleteSupport
{
    /// <summary>建含單一「Delete」項之右鍵選單，Click→<paramref name="delete"/>（作用於清單目前選取項）。</summary>
    public static ContextMenu DeleteMenu(Action delete)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Delete" };
        item.Click += (_, _) => delete();
        menu.Items.Add(item);
        return menu;
    }

    /// <summary>右鍵按下時選取游標下之清單項（使右鍵選單/刪除作用於被右鍵的那項，而非既有選取）。</summary>
    public static void SelectItemUnderMouse(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not ListBoxItem)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }
        if (dep is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }
}
