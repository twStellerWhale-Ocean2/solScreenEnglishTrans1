using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace LingoIsland.Present;

/// <summary>
/// 條目卡單擊選取之共用單選器（Issue #110；筆記/歷史兩頁共用、免各寫一份——#110 §5 審查 #1）：
/// 每頁一實例、僅視覺回饋（換 <see cref="Border.BorderBrush"/>，框厚恆定由卡片建構保證）；
/// 重繪/切夾時呼叫 <see cref="Clear"/>（選取不持久化）。
/// #118 改**快取式還原**：未選框每卡各異（筆記＝底色×0.80 加深、歷史＝淡粉常數）——選取當下快取該卡
/// 現行框刷、還原用快取。**invariant**：框刷與 passed 態無關（過關只換底刷）＝快取還原正確性前提；
/// 同卡重複 Select 早退防「把深粉快取成 idle」之自污染。
/// </summary>
public sealed class CardSelector
{
    /// <summary>歷史頁未選卡框色（淡粉常數；筆記頁未選框改 NoteCardBrush.BorderFor，#118）。</summary>
    public const string IdleBorder = "#F4C2D0";

    /// <summary>選中卡框色（深粉 accent；十色底任一上對比 ≥3:1）。</summary>
    public const string SelectedBorder = "#B0578D";

    private static readonly Brush SelectedBrush = new SolidColorBrush(
        (Color)ColorConverter.ConvertFromString(SelectedBorder));

    private Border? _selected;
    private Brush? _selectedIdle; // 選中卡之原框刷（快取式還原；與 _selected 同生同滅）

    /// <summary>選取指定卡：前卡框刷還原為其快取、新卡快取現行框刷後轉深粉；同卡早退。</summary>
    public void Select(Border card)
    {
        if (ReferenceEquals(_selected, card))
        {
            return;
        }
        if (_selected is not null && _selectedIdle is not null)
        {
            _selected.BorderBrush = _selectedIdle;
        }
        _selected = card;
        _selectedIdle = card.BorderBrush;
        card.BorderBrush = SelectedBrush;
    }

    /// <summary>清空選取（重繪/切夾/切日時呼叫；同時棄快取刷——不還原前卡框色，舊卡即將被卸離重建）。</summary>
    public void Clear()
    {
        _selected = null;
        _selectedIdle = null;
    }
}
