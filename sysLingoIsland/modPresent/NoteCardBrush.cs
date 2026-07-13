using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace LingoIsland.Present;

/// <summary>
/// 筆記卡底刷/框刷單一來源（Issue #111 → #118 改制，USR 實測回饋）：
/// 底刷——未通過＝素色（無底色退白）、通過（最佳分 ≥ 門檻，導出態）＝ <see cref="Brushes.Transparent"/>
/// （非 null、保命中測試）透出主視窗小公主浮水印作為成就獎勵；
/// 框刷——**該卡底色各通道 ×<see cref="DarkenFactor"/> 略加深**（色彩身份延伸到框；白卡→淺灰 `#CCCCCC`），
/// 與 passed 態無關（過關只換底刷不換框——CardSelector 快取還原正確性前提）。刷一律 Freeze、依 hex 快取。
/// </summary>
public static class NoteCardBrush
{
    /// <summary>外框明度係數（#123：#118 之 0.80「識別性太淺」→ 0.62 更明顯；白 ×0.62＝`#9E9E9E`）。</summary>
    public const double DarkenFactor = 0.62;

    /// <summary>外框飽和度加乘（#123 回饋：粉彩底色偏灰→先拉高飽和再加深，色框更鮮明；白/灰無彩不受影響）。</summary>
    public const double SaturationBoost = 1.6;

    /// <summary>
    /// 框色計算（純函式可測）：先以各通道相對灰軸（三通道均值）拉高飽和（×<see cref="SaturationBoost"/>）、
    /// 再乘明度係數（×<see cref="DarkenFactor"/>）加深；截斷取整、鉗制 0–255。無彩（灰階）拉飽和為無作用、僅加深。
    /// </summary>
    public static (byte R, byte G, byte B) BorderRgb(byte r, byte g, byte b)
    {
        var mean = (r + g + b) / 3.0;
        return (Chan(r, mean), Chan(g, mean), Chan(b, mean));
    }

    private static byte Chan(byte c, double mean)
    {
        var v = (mean + (c - mean) * SaturationBoost) * DarkenFactor;
        return (byte)Math.Clamp((int)v, 0, 255);
    }

    private static readonly Dictionary<string, Brush> BaseCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Brush> BorderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 卡片底刷（v1.0.1 改制，USR 回饋）：以 <paramref name="opacityPercent"/>（0–100，筆記/歷史共用之可調透明度）
    /// 套為底色 alpha——筆記＝該筆記色、歷史＝白（無效/空 hex 退白）。取代原「過關→透明」二值行為（過關改由綠色成績框表示）。
    /// alpha 0（完全透明）仍為非 null <see cref="SolidColorBrush"/>、卡片可點選/拖曳命中。刷依「色:alpha」快取並 Freeze。
    /// </summary>
    public static Brush For(string? colorHex, int opacityPercent)
    {
        var (key, color) = Normalize(colorHex);
        byte a = (byte)Math.Clamp((int)Math.Round(opacityPercent * 255.0 / 100.0), 0, 255);
        var cacheKey = key + ":" + a;
        if (BaseCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        var solid = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B));
        solid.Freeze();
        BaseCache[cacheKey] = solid;
        return solid;
    }

    /// <summary>卡片未選框刷（#118）：底色 ×0.80 加深；與 passed 態無關。</summary>
    public static Brush BorderFor(string? colorHex)
    {
        var (key, color) = Normalize(colorHex);
        if (BorderCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var (dr, dg, db) = BorderRgb(color.R, color.G, color.B);
        var brush = new SolidColorBrush(Color.FromRgb(dr, dg, db));
        brush.Freeze();
        BorderCache[key] = brush;
        return brush;
    }

    private static (string Key, Color Color) Normalize(string? colorHex)
    {
        var hex = string.IsNullOrWhiteSpace(colorHex) ? "#FFFFFF" : colorHex!.Trim();
        try
        {
            return (hex.ToUpperInvariant(), (Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return ("#FFFFFF", Colors.White);
        }
    }
}
