using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace ScreenTrans.Present;

/// <summary>
/// 條目卡單擊選取之共用單選器（Issue #110；筆記/歷史兩頁共用、免各寫一份——#110 §5 審查 #1）：
/// 每頁一實例、僅視覺回饋（換 <see cref="Border.BorderBrush"/>，框厚恆定由卡片建構保證）；
/// 重繪/切夾時呼叫 <see cref="Clear"/>（選取不持久化）。
/// </summary>
public sealed class CardSelector
{
    /// <summary>未選卡框色（兩頁統一淡粉、恆定 2px）。</summary>
    public const string IdleBorder = "#F4C2D0";

    /// <summary>選中卡框色（深粉 accent；十色底任一上對比 ≥3:1）。</summary>
    public const string SelectedBorder = "#B0578D";

    private static readonly Brush IdleBrush = new SolidColorBrush(
        (Color)ColorConverter.ConvertFromString(IdleBorder));
    private static readonly Brush SelectedBrush = new SolidColorBrush(
        (Color)ColorConverter.ConvertFromString(SelectedBorder));

    private Border? _selected;

    /// <summary>選取指定卡：前卡框色還原、新卡轉深粉；同卡早退。</summary>
    public void Select(Border card)
    {
        if (ReferenceEquals(_selected, card))
        {
            return;
        }
        if (_selected is not null)
        {
            _selected.BorderBrush = IdleBrush;
        }
        _selected = card;
        card.BorderBrush = SelectedBrush;
    }

    /// <summary>清空選取（重繪/切夾/切日時呼叫；不還原前卡框色——舊卡即將被卸離重建）。</summary>
    public void Clear() => _selected = null;
}
