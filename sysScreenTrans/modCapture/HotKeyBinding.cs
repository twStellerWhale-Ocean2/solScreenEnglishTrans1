using System.Text;

namespace ScreenTrans.Capture;

/// <summary>喚起快捷鍵綁定的類別：鍵盤組合（走 RegisterHotKey）或滑鼠鍵（走 WH_MOUSE_LL）。</summary>
public enum HotKeyKind
{
    Keyboard,
    Mouse,
}

/// <summary>滑鼠鍵綁定型別（無法以 RegisterHotKey 表達、須低階滑鼠 hook）。</summary>
public enum MouseTrigger
{
    Middle,
    XButton1,
    XButton2,
    LeftRight, // 左右鍵同時按
}

/// <summary>
/// 喚起快捷鍵綁定（spec#1 可自訂喚起）：鍵盤＝修飾鍵集合＋主鍵（`RegisterHotKey` 表達），
/// 滑鼠＝中鍵／側鍵／左右同按（`WH_MOUSE_LL` 表達）。以人類可讀字串存 appsettings `paramHotkey`
/// （如 <c>Alt+L</c>／<c>Ctrl+Shift+F</c>／<c>Mouse:Middle</c>／<c>Mouse:LeftRight</c>）。
/// 本型別只負責綁定的表達、序列化與顯示——純資料、可單元測試；實際註冊／hook 於 <see cref="HotKeyService"/>。
/// </summary>
public sealed record HotKeyBinding
{
    // RegisterHotKey 修飾鍵位元（與 Win32 一致）
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public HotKeyKind Kind { get; init; }

    /// <summary>鍵盤：修飾鍵位元 OR 組合（Kind=Keyboard 時有效）。</summary>
    public uint Modifiers { get; init; }

    /// <summary>鍵盤：主鍵虛擬鍵碼（Kind=Keyboard 時有效）。</summary>
    public uint VirtualKey { get; init; }

    /// <summary>滑鼠：觸發鍵（Kind=Mouse 時有效）。</summary>
    public MouseTrigger Mouse { get; init; }

    /// <summary>預設綁定：Alt+L（沿用原硬編碼喚起鍵）。</summary>
    public static HotKeyBinding Default { get; } = new()
    {
        Kind = HotKeyKind.Keyboard,
        Modifiers = ModAlt,
        VirtualKey = 0x4C, // VK_L
    };

    public static HotKeyBinding Keyboard(uint modifiers, uint virtualKey) => new()
    {
        Kind = HotKeyKind.Keyboard,
        Modifiers = modifiers,
        VirtualKey = virtualKey,
    };

    public static HotKeyBinding OfMouse(MouseTrigger trigger) => new()
    {
        Kind = HotKeyKind.Mouse,
        Mouse = trigger,
    };

    /// <summary>人類可讀顯示名稱（亦為序列化字串）。</summary>
    public string DisplayName => Serialize();

    /// <summary>序列化為 appsettings `paramHotkey` 字串。無效鍵盤主鍵時退回預設，確保可往返。</summary>
    public string Serialize()
    {
        if (Kind == HotKeyKind.Mouse)
        {
            return "Mouse:" + Mouse switch
            {
                MouseTrigger.Middle => "Middle",
                MouseTrigger.XButton1 => "X1",
                MouseTrigger.XButton2 => "X2",
                MouseTrigger.LeftRight => "LeftRight",
                _ => "Middle",
            };
        }

        var sb = new StringBuilder();
        if ((Modifiers & ModControl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((Modifiers & ModShift) != 0) sb.Append("Shift+");
        if ((Modifiers & ModWin) != 0) sb.Append("Win+");
        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    /// <summary>由 appsettings 字串反序列化；格式不符或空白時退回預設（絕不拋例外）。</summary>
    public static HotKeyBinding Parse(string? s) => TryParse(s, out var b) ? b : Default;

    public static bool TryParse(string? s, out HotKeyBinding binding)
    {
        binding = Default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        s = s.Trim();
        if (s.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            var m = s["Mouse:".Length..].Trim();
            MouseTrigger? trig = m.ToUpperInvariant() switch
            {
                "MIDDLE" => MouseTrigger.Middle,
                "X1" or "XBUTTON1" => MouseTrigger.XButton1,
                "X2" or "XBUTTON2" => MouseTrigger.XButton2,
                "LEFTRIGHT" => MouseTrigger.LeftRight,
                _ => null,
            };
            if (trig is null)
            {
                return false;
            }
            binding = OfMouse(trig.Value);
            return true;
        }

        uint mods = 0;
        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= ModControl; break;
                case "ALT": mods |= ModAlt; break;
                case "SHIFT": mods |= ModShift; break;
                case "WIN" or "WINDOWS" or "META": mods |= ModWin; break;
                default: return false; // 不認得的修飾鍵
            }
        }

        var vk = KeyCode(parts[^1]);
        if (vk is null)
        {
            return false;
        }
        binding = Keyboard(mods, vk.Value);
        return true;
    }

    /// <summary>虛擬鍵碼 → 名稱（A-Z／0-9／F1-F24；未知回 U+十六進位）。</summary>
    internal static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();          // A-Z
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();          // 0-9
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);          // F1-F24
        return "U" + vk.ToString("X2");
    }

    /// <summary>名稱 → 虛擬鍵碼（A-Z／0-9／F1-F24）；不認得回 null。</summary>
    internal static uint? KeyCode(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length == 1)
        {
            var c = name[0];
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }
        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name[1..], out var n) && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + n - 1);
        }
        return null;
    }
}
