using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LingoIsland.Capture;

/// <summary>
/// 螢幕擷取（[modCapture模組] 選區對位契約，spec#2）。進程為 PerMonitorV2 DPI aware（app.manifest），
/// 故螢幕座標即實際像素、無需縮放換算。**Issue #90 起**：喚起當下以 <see cref="CaptureVirtualDesktop"/>
/// 截整個虛擬桌面為**凍結畫格**（<see cref="FrozenDesktop"/>）——遮罩以其為不透明背景（畫面靜止），
/// 選區框選與雙擊標記皆自凍結快照裁切／繪製，不再對實時螢幕擷取（點擊／雙擊落在靜止快照上、不干擾背後遊戲）。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// 截取整個虛擬桌面為凍結畫格（Issue #90）：於顯示遮罩前呼叫，之後遮罩以其為靜止背景、
    /// 選區／標記自快照取得（不再對實時螢幕 <c>CopyFromScreen</c>）。虛擬桌面尺寸異常回 null。
    /// </summary>
    public static FrozenDesktop? CaptureVirtualDesktop()
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0)
        {
            return null;
        }

        var bmp = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(vx, vy, 0, 0, new Size(vw, vh), CopyPixelOperation.SourceCopy);
        }
        return new FrozenDesktop(bmp, vx, vy);
    }
}
