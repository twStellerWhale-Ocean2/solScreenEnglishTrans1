namespace ScreenTrans.Query;

/// <summary>
/// 筆記底色粉彩盤單一來源（Issue #44 原置於 NotesPage，#55 抽出集中）：名稱↔hex 對照，
/// 供筆記分頁右鍵選單、結果視窗快速選色、以及智能配色（AI 回傳色名 → hex）共用，不各寫一份。
/// </summary>
public static class NoteColors
{
    /// <summary>
    /// 粉彩盤（名稱＋hex）；空 hex／空名＝無底色（預設白）。Issue #75：去粉紫（同質性高）改淺灰。
    /// Issue #109 擴為十色——新五色依量化驗收選定：任一涉新色配對 ΔE(Lab) ≥ 9（高於 #75 剔除案 7.58）、
    /// L*≈89–90 同粉彩帶、對 #333 文字對比 ≥9.5:1、英文名首字母不撞（選單鍵盤跳選）。
    /// </summary>
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Pink", "#FBE4EC"),
        ("Blue", "#E1EFFB"),
        ("Green", "#E4F5E9"),
        ("Yellow", "#FBF3D9"),
        ("Gray", "#DCE0E3"),
        ("Violet", "#EEDBFF"),
        ("Sky", "#B4EBFF"),
        ("Mint", "#B8EDDE"),
        ("Lime", "#D1EAC7"),
        ("Orange", "#FFD9B8"),
    };

    /// <summary>色名 → hex；未知名回空字串（＝無底色）。用於智能配色把 AI 回傳之色名對應到盤上色。</summary>
    public static string HexOfName(string? name)
    {
        var n = (name ?? "").Trim();
        foreach (var (pn, hex) in Palette)
        {
            if (string.Equals(pn, n, StringComparison.Ordinal))
            {
                return hex;
            }
        }
        return "";
    }

    /// <summary>是否為盤上已知 hex（大小寫不敏感）；供結果視窗判定 AI 建議色是否可套用。</summary>
    public static bool IsPaletteHex(string? hex)
    {
        var h = (hex ?? "").Trim();
        foreach (var (_, ph) in Palette)
        {
            if (string.Equals(ph, h, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把 AI 回傳之建議色（可能是色名如「粉紅」或 hex）正規化為盤上**正典** hex；無法對應回空字串（不套色）。
    /// 供智能配色容錯：模型回色名走 <see cref="HexOfName"/>，回 hex 則對應回 Palette 定本寫法——
    /// 落地一律正典大小寫，下游（筆記選單打勾之字面比對、色塊列高亮）判準一致（#109 §5 審查）。
    /// </summary>
    public static string NormalizeSuggested(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
        {
            return "";
        }
        var byName = HexOfName(s);
        if (byName.Length > 0)
        {
            return byName;
        }
        foreach (var (_, ph) in Palette)
        {
            if (string.Equals(ph, s, StringComparison.OrdinalIgnoreCase))
            {
                return ph; // 盤上 hex → 正典寫法
            }
        }
        return "";
    }
}
