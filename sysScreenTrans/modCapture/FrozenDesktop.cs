using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScreenTrans.Capture;

/// <summary>
/// 凍結畫格（Issue #90）：喚起當下截取之整個虛擬桌面快照。遮罩以其為**不透明背景**（畫面靜止），
/// 選區框選（<see cref="CropToResult"/>）與雙擊點選（<see cref="MarkToResult"/>）皆自本快照裁切／繪製，
/// 不再對實時螢幕擷取——故點擊／雙擊落在靜止快照上、不干擾背後遊戲。座標為 physical pixels（PerMonitorV2）。
/// </summary>
public sealed class FrozenDesktop : IDisposable
{
    private readonly Bitmap _bmp;

    /// <summary>快照左上角於虛擬桌面之 physical-pixel 原點（多螢幕可為負）。</summary>
    public int OriginX { get; }

    /// <summary>快照左上角於虛擬桌面之 physical-pixel 原點（多螢幕可為負）。</summary>
    public int OriginY { get; }

    public int Width => _bmp.Width;
    public int Height => _bmp.Height;

    internal FrozenDesktop(Bitmap bmp, int originX, int originY)
    {
        _bmp = bmp;
        OriginX = originX;
        OriginY = originY;
    }

    /// <summary>轉為凍結、可跨執行緒之 WPF 影像來源，供遮罩不透明背景顯示。</summary>
    public BitmapSource ToImageSource()
    {
        IntPtr hbmp = _bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hbmp);
        }
    }

    /// <summary>
    /// 自快照裁切選區（physical-pixel 矩形，含虛擬桌面原點）→ PNG。寬高 ≤0、或與快照無交集時回 null；
    /// 部分越界則裁切至交集（防超大 Bitmap／越界索引）。
    /// </summary>
    public CaptureResult? CropToResult(int px, int py, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var rect = Rectangle.Intersect(
            new Rectangle(px - OriginX, py - OriginY, width, height),
            new Rectangle(0, 0, _bmp.Width, _bmp.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        using var crop = _bmp.Clone(rect, PixelFormat.Format32bppArgb);
        return ToResult(crop, isPointMode: false);
    }

    /// <summary>於快照游標處（physical-pixel）畫紅色標記、回整張快照（<c>IsPointMode=true</c>）→ PNG。</summary>
    public CaptureResult MarkToResult(int px, int py)
    {
        using var copy = new Bitmap(_bmp);
        using (var g = Graphics.FromImage(copy))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawMarker(g, px - OriginX, py - OriginY);
        }
        return ToResult(copy, isPointMode: true);
    }

    private static CaptureResult ToResult(Bitmap bmp, bool isPointMode)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new CaptureResult(ms.ToArray(), bmp.Width, bmp.Height, isPointMode);
    }

    /// <summary>畫游標處標記：紅色空心圓＋穿過之十字（醒目、不完全遮住底下文字，供 vision 定位）。</summary>
    private static void DrawMarker(Graphics g, int cx, int cy)
    {
        const int r = 16;
        // 白色描邊墊底提升對比（避免落在紅/暗底時看不清）
        using var halo = new Pen(Color.FromArgb(200, 255, 255, 255), 5f);
        using var red = new Pen(Color.FromArgb(235, 255, 30, 30), 2.5f);
        foreach (var pen in new[] { halo, red })
        {
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            g.DrawLine(pen, cx - r - 6, cy, cx - 4, cy);   // 左
            g.DrawLine(pen, cx + 4, cy, cx + r + 6, cy);   // 右
            g.DrawLine(pen, cx, cy - r - 6, cx, cy - 4);   // 上
            g.DrawLine(pen, cx, cy + 4, cx, cy + r + 6);   // 下
        }
    }

    public void Dispose() => _bmp.Dispose();

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
