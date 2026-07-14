using System.IO;
using System.Reflection;
using Window = System.Windows.Window;

namespace LingoIsland.Present;

/// <summary>
/// 更新紀錄視窗（#159）：由 About 頁「Change Log」按鈕跳出（modal、CenterOwner）；顯示嵌入之 CHANGELOG.md。
/// 沿用 About 原邏輯——略去檔首「# Changelog」H1 與 SemVer 註解，自首個版本條目（行首「## 」）起顯示；
/// 缺失/失敗回提示、不致命。唯讀可捲動可選取。
/// </summary>
public partial class ChangeLogWindow : Window
{
    public ChangeLogWindow()
    {
        InitializeComponent();
        ChangeLogBox.Text = LoadChangeLog();
        CloseBtn.Click += (_, _) => Close();
    }

    /// <summary>讀嵌入之 CHANGELOG.md（Issue #79）；自首個版本條目起、去 H1 與註解。</summary>
    private static string LoadChangeLog()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s is null)
            {
                return "(No change log found)";
            }
            using var r = new StreamReader(s);
            var text = r.ReadToEnd().TrimStart();
            if (text.StartsWith("## ", StringComparison.Ordinal))
            {
                return text; // 檔首已是版本條目（無 H1）
            }
            var idx = text.IndexOf("\n## ", StringComparison.Ordinal);
            return idx >= 0 ? text[(idx + 1)..] : text;
        }
        catch
        {
            return "(Couldn't load change log)";
        }
    }
}
