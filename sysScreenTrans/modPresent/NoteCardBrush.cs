using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace ScreenTrans.Present;

/// <summary>
/// 筆記卡底刷單一來源（Issue #111 通過卡星紋底）：未通過＝素色；通過（最佳分 ≥ 門檻，導出態）＝
/// 深色星紋 DrawingBrush——地＝底色（無底色退白）、星＝底色各通道 ×<see cref="DarkenFactor"/>、
/// **實心五角星、星徑約 9px、置中於 24px 磁磚**（覆蓋率 ≤10%、低密度不搶字）。
/// 刷一律 Freeze；星紋刷依底色 hex 快取重用（整夾重繪頻繁、免每卡重建）。
/// </summary>
public static class NoteCardBrush
{
    /// <summary>星色暗化係數（定本）：乘法暗化使底星對比於十色恆定 ≈2:1、任一底上同等可見。</summary>
    public const double DarkenFactor = 0.72;

    /// <summary>暗色計算（純函式可測）：各通道 ×0.72、截斷取整。</summary>
    public static (byte R, byte G, byte B) Darken(byte r, byte g, byte b) =>
        ((byte)(r * DarkenFactor), (byte)(g * DarkenFactor), (byte)(b * DarkenFactor));

    private static readonly Dictionary<string, Brush> StarCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取得卡片底刷：<paramref name="passed"/>＝false 素色、true 星紋（快取）。無效/空 hex 退白。</summary>
    public static Brush For(string? colorHex, bool passed)
    {
        var hex = string.IsNullOrWhiteSpace(colorHex) ? "#FFFFFF" : colorHex!.Trim();
        Color baseColor;
        try
        {
            baseColor = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            baseColor = Colors.White;
            hex = "#FFFFFF";
        }
        if (!passed)
        {
            var solid = new SolidColorBrush(baseColor);
            solid.Freeze();
            return solid;
        }
        var key = hex.ToUpperInvariant();
        if (StarCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var (dr, dg, db) = Darken(baseColor.R, baseColor.G, baseColor.B);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(dr, dg, db)), null, StarGeometry(12, 12, 4.5)));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 24, 24),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        StarCache[key] = brush;
        return brush;
    }

    /// <summary>實心五角星幾何（外接圓半徑 <paramref name="r"/>、內半徑 ×0.5、頂點朝上）。</summary>
    private static Geometry StarGeometry(double cx, double cy, double r)
    {
        var pts = new Point[10];
        for (var i = 0; i < 10; i++)
        {
            var rad = Math.PI * (i * 36 - 90) / 180.0;
            var rr = i % 2 == 0 ? r : r * 0.5;
            pts[i] = new Point(cx + rr * Math.Cos(rad), cy + rr * Math.Sin(rad));
        }
        var fig = new PathFigure(pts[0], pts.Skip(1).Select(p => (PathSegment)new LineSegment(p, isStroked: false)), closed: true)
        {
            IsFilled = true,
        };
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        return geo;
    }
}
