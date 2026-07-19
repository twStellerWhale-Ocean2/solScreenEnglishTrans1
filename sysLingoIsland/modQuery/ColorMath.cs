using System.Globalization;

namespace LingoIsland.Query;

/// <summary>
/// 色彩數學共用（#189-checklist USR：主題 12 色可編輯後，字型色用原色、筆記底色需自同色調淡）：
/// #RRGGBB 解析、RGB↔HSL、以及「調淡為白底可讀底色」。純函式、可單元測試、無 UI 依賴。
/// </summary>
public static class ColorMath
{
    /// <summary>解析 <c>#RRGGBB</c>（大小寫皆可）為 RGB；非法回 false。</summary>
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = (hex ?? "").Trim();
        if (s.Length != 7 || s[0] != '#') { return false; }
        return byte.TryParse(s.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    public static void RgbToHsl(byte R, byte G, byte B, out double h, out double s, out double l)
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        l = (max + min) / 2.0;
        if (d == 0) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
    }

    public static void HslToRgb(double h, double s, double l, out byte R, out byte G, out byte B)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
        R = (byte)Math.Round((r + m) * 255); G = (byte)Math.Round((g + m) * 255); B = (byte)Math.Round((b + m) * 255);
    }

    /// <summary>把（可能高飽和之）色調淡為**白底可讀之極淺底色**（保色相、提亮度至約 0.90、封飽和上限）；非法 hex 回空字串。</summary>
    public static string LightenForBackground(string? hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) { return ""; }
        RgbToHsl(r, g, b, out var h, out var s, out var l);
        HslToRgb(h, Math.Min(s, 0.55), 0.90, out var r2, out var g2, out var b2);
        return $"#{r2:X2}{g2:X2}{b2:X2}";
    }
}
