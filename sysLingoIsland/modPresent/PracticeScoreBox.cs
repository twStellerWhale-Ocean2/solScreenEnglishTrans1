using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using TextBlock = System.Windows.Controls.TextBlock;
using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.Windows.Shapes.Path;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;
// 專案匯入 WinForms/Drawing，下列名稱與 System.Drawing／System.Windows.Forms 撞名 → 別名鎖定 WPF 型別
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace LingoIsland.Present;

/// <summary>
/// 發音練習成績框（[modPresent模組] 我的筆記檢視契約，spec#10）：卡片行尾**固定尺寸**之五態指示，取代舊燈泡之狀態面——
/// <list type="bullet">
/// <item>未練＝灰底空框顯「—」；</item>
/// <item>一般＝顯示**最佳分**（&lt;門檻＝紅色空心框、≥門檻＝綠色實心底＋✓）；</item>
/// <item>錄音中＝框內**藍色音量條由下而上**依即時麥克風音量升降（<see cref="SetLevel"/>）；</item>
/// <item>評分中＝**spinner 轉圈**與錄音態區分；</item>
/// <item>得分後＝先閃「這次分數」（依其及格與否上色）再回落顯示最佳分（<see cref="FlashScore"/>）。</item>
/// </list>
/// 達標除綠色外另加 ✓、未達為空心紅框——**非僅以顏色分辨**（色盲友善）。尺寸恆定、可容三位數與音量條／spinner、不變形。
/// </summary>
public sealed class PracticeScoreBox : Border
{
    private const double BoxW = 36, BoxH = 23;

    private readonly TextBlock _text;
    private readonly Rectangle _bar;      // 音量條（底部向上）
    private readonly Path _check;         // ✓（達標標記，非顏色線索）
    private readonly Path _spinner;       // 評分中轉圈弧
    private readonly RotateTransform _spin = new(0);
    private DispatcherTimer? _flashTimer;
    private int _best = -1;
    private int _threshold = 80;

    public PracticeScoreBox()
    {
        Width = BoxW;
        Height = BoxH;
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1.4);
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 2, 0);

        var grid = new Grid { ClipToBounds = true };

        _bar = new Rectangle
        {
            Fill = B("#8FC0FF"),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 0,
            Visibility = Visibility.Collapsed,
        };
        _text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
        };
        _check = new Path
        {
            Data = Geometry.Parse("M 0 3 L 2.4 5.4 L 7 0"),
            Stroke = B("#FFFFFF"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 3.5, 0),
            Visibility = Visibility.Collapsed,
        };
        _spinner = new Path
        {
            Width = 15,
            Height = 15,
            Data = Geometry.Parse("M 7.5 1 A 6.5 6.5 0 0 1 14 7.5"),
            Stroke = B("#6AA0E8"),
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            Fill = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _spin,
            Visibility = Visibility.Collapsed,
        };

        grid.Children.Add(_bar);
        grid.Children.Add(_text);
        grid.Children.Add(_check);
        grid.Children.Add(_spinner);
        Child = grid;

        // 卸離視覺樹（卡片重建）時停掉閃分計時器與 spinner 動畫，免孤兒框滯留（§5 #4）
        Unloaded += (_, _) => { CancelFlash(); StopSpin(); };

        RenderBest();
    }

    /// <summary>設定及格門檻（設定頁調整時全體重算恆綠/恆紅呈現）。</summary>
    public void SetThreshold(int threshold)
    {
        _threshold = threshold;
        if (_spinner.Visibility != Visibility.Visible && _bar.Visibility != Visibility.Visible)
        {
            RenderBest();
        }
    }

    /// <summary>回到閒置態並顯示最佳分（<paramref name="best"/> &lt; 0＝未練）。</summary>
    public void ShowBest(int best)
    {
        CancelFlash();
        StopSpin();
        _best = best;
        _bar.Visibility = Visibility.Collapsed;
        _spinner.Visibility = Visibility.Collapsed;
        RenderBest();
    }

    private void RenderBest()
    {
        if (_best < 0)
        {
            Background = B("#F1EEF0");
            BorderBrush = B("#C9B8C0");
            _text.Text = "—";
            _text.Foreground = B("#A899A2");
            _text.FontWeight = FontWeights.Normal;
            _check.Visibility = Visibility.Collapsed;
        }
        else if (_best >= _threshold)
        {
            Background = B("#2E9E5B");
            BorderBrush = B("#268A4F");
            _text.Text = _best.ToString();
            _text.Foreground = B("#FFFFFF");
            _text.FontWeight = FontWeights.Bold;
            _check.Visibility = Visibility.Visible;
        }
        else
        {
            Background = B("#FFFFFF");
            BorderBrush = B("#D64545");
            _text.Text = _best.ToString();
            _text.Foreground = B("#C0392B");
            _text.FontWeight = FontWeights.Bold;
            _check.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>進入錄音態：清空數字、藍框，音量條歸零待 <see cref="SetLevel"/> 更新。</summary>
    public void ShowRecording()
    {
        CancelFlash();
        StopSpin();
        Background = B("#FFFFFF");
        BorderBrush = B("#6AA0E8");
        _text.Text = "";
        _check.Visibility = Visibility.Collapsed;
        _spinner.Visibility = Visibility.Collapsed;
        _bar.Height = 0;
        _bar.Visibility = Visibility.Visible;
    }

    /// <summary>錄音期間即時音量（0–1）→ 藍色音量條高度（由下而上）。非錄音態忽略。</summary>
    public void SetLevel(double level)
    {
        if (_bar.Visibility != Visibility.Visible)
        {
            return;
        }
        var inner = Height - BorderThickness.Top - BorderThickness.Bottom;
        _bar.Height = Math.Clamp(level, 0, 1) * inner;
    }

    /// <summary>進入評分中態：spinner 轉圈（與錄音態區分）。</summary>
    public void ShowScoring()
    {
        CancelFlash();
        Background = B("#FFFFFF");
        BorderBrush = B("#6AA0E8");
        _text.Text = "";
        _check.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Collapsed;
        _spinner.Visibility = Visibility.Visible;
        StartSpin();
    }

    /// <summary>
    /// 得分：先閃「這次分數」<paramref name="score"/>（依其及格與否上色）約 1.1 秒，再回落顯示最佳分
    /// <paramref name="newBest"/>（呼叫端已將最佳分寫回並取回）。
    /// </summary>
    public void FlashScore(int score, int newBest)
    {
        StopSpin();
        _spinner.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Collapsed;
        var pass = score >= _threshold;
        Background = pass ? B("#2E9E5B") : B("#FFFFFF");
        BorderBrush = pass ? B("#268A4F") : B("#D64545");
        _text.Text = score.ToString();
        _text.Foreground = pass ? B("#FFFFFF") : B("#C0392B");
        _text.FontWeight = FontWeights.Bold;
        _check.Visibility = pass ? Visibility.Visible : Visibility.Collapsed;

        CancelFlash();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _flashTimer.Tick += (_, _) =>
        {
            CancelFlash();
            ShowBest(newBest);
        };
        _flashTimer.Start();
    }

    private void StartSpin()
    {
        var da = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(850)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _spin.BeginAnimation(RotateTransform.AngleProperty, da);
    }

    private void StopSpin()
    {
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _spin.Angle = 0;
    }

    private void CancelFlash()
    {
        if (_flashTimer is not null)
        {
            _flashTimer.Stop();
            _flashTimer = null;
        }
    }

    private static SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
