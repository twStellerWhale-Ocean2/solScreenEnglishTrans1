#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#46（Issue #111）：發音練習通過卡星紋底——通過卡（最佳分>=門檻）出現星色精確像素、"
write-host "  未通過卡無；Clear Practice 後花紋消失。星色＝底色各通道 ×0.72（截斷），與 NoteCardBrush.Darken 同式鏡像。"
write-host "* 「評分達標當下就地點亮」需真麥克風/API，不在本腳本自動化——資料源已定本 store 現值（程式碼審視），"
write-host "  重繪路徑（本腳本）與就地路徑共用 NoteCardBrush.For 同一判定。"
write-host "* 資料保護：起手備份 notes.json、造測試分數；結束（app 關閉後）還原。強制自啟實例。"
write-host "* 日期版本：2026-07-10 v1"

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysScreenTrans\bin\Release\net9.0-windows10.0.19041.0\ScreenTrans.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }

  $notesPath = Join-Path $env:APPDATA "ScreenTrans\notes.json"
  $backupPath = "$notesPath.intTest46.bak"
  Copy-Item $notesPath $backupPath -Force
  write-host "* 已備份 notes.json"

  # 造測試分數：夾內第 1 條＝100（任何門檻皆通過）、第 2 條＝-1（未練）
  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 2 } | Select-Object -First 1
  if (-not $folder) { Remove-Item $backupPath; write-host "前置不足：無一夾含 >=2 條筆記" -ForegroundColor Red; exit 1 }
  $folder.Entries[0].PracticeScore = 100
  $folder.Entries[1].PracticeScore = -1
  $notes | ConvertTo-Json -Depth 10 | Set-Content $notesPath -Encoding UTF8
  $e1 = $folder.Entries[0]; $e2 = $folder.Entries[1]
  write-host "* 測試夾「$($folder.Name)」：條1 分數=100（通過）、條2 =-1（未練）"

  # 星色鏡像計算（同 NoteCardBrush.Darken：各通道 ×0.72 截斷；無底色退白）
  function Get-StarColor([string]$hex) {
    if ([string]::IsNullOrWhiteSpace($hex)) { $hex = "#FFFFFF" }
    $r = [Convert]::ToInt32($hex.Substring(1,2),16); $g = [Convert]::ToInt32($hex.Substring(3,2),16); $b = [Convert]::ToInt32($hex.Substring(5,2),16)
    return @([int][Math]::Truncate($r*0.72), [int][Math]::Truncate($g*0.72), [int][Math]::Truncate($b*0.72))
  }
  $star1 = Get-StarColor $e1.Color
  $star2 = Get-StarColor $e2.Color
  write-host "* 條1 星色預期 RGB($($star1 -join ','))、條2 星色（不應出現）RGB($($star2 -join ','))"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T46
  [T46.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y) {
    [T46.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    [T46.Native]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [T46.Native]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
  }

  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

  function Find-TextLike([Windows.Automation.AutomationElement]$Root, [string]$Prefix) {
    $texts = $Root.FindAll([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -like "$Prefix*") { return $t } }
    return $null
  }

  function Get-WindowBitmap([Windows.Automation.AutomationElement]$Win) {
    $r = $Win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [T46.Native]::PrintWindow([IntPtr][int64]$Win.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
  }

  # 卡片帶內「星色精確值」像素計數（設計定本：精確 RGB、非容差——防圓角反鋸齒誤中；帶高涵蓋整卡 >=24px 磁磚週期）
  function Count-ExactColor([System.Drawing.Bitmap]$Bmp, [Windows.Automation.AutomationElement]$Win, [Windows.Automation.AutomationElement]$Elem, [int[]]$Rgb) {
    $wr = $Win.Current.BoundingRectangle; $er = $Elem.Current.BoundingRectangle
    $y0 = [Math]::Max(0, [int]($er.Y - $wr.Y) - 16); $y1 = [Math]::Min($Bmp.Height - 1, [int]($er.Y - $wr.Y + $er.Height) + 16)
    $x0 = [Math]::Max(0, [int]($er.X - $wr.X) - 40); $x1 = $Bmp.Width - 1
    $n = 0
    for ($y = $y0; $y -le $y1; $y++) {
      for ($x = $x0; $x -le $x1; $x++) {
        $c = $Bmp.GetPixel($x, $y)
        if ($c.R -eq $Rgb[0] -and $c.G -eq $Rgb[1] -and $c.B -eq $Rgb[2]) { $n++ }
      }
    }
    return $n
  }

  function Save-WindowShot([string]$Path, [Windows.Automation.AutomationElement]$Win) {
    $raw = Get-WindowBitmap $Win
    $cw = $raw.Width - 16; $ch = $raw.Height - 9
    $bmp = New-Object System.Drawing.Bitmap($cw, $ch)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.DrawImage($raw, (New-Object System.Drawing.Rectangle(0,0,$cw,$ch)), (New-Object System.Drawing.Rectangle(8,1,$cw,$ch)), [System.Drawing.GraphicsUnit]::Pixel)
    $g2.Dispose(); $raw.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    write-host "* 截圖：$Path（PrintWindow）"
  }

  #endregion

#endregion

#region III.內容程序 ================================
write-host "# III.內容程序================================" -ForegroundColor Blue

$fail = 0
$startedByTest = $false
$appPid = 0

try {

  #region A.啟動與定位（強制自啟實例） --------------------------------
  write-host "## A.啟動與定位（強制自啟實例） --------------------------------" -ForegroundColor Cyan

  if (Get-Process ScreenTrans -ErrorAction SilentlyContinue) {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "偵測到執行中之 ScreenTrans——請先結束後重跑" -ForegroundColor Red; exit 1
  }
  $proc = Start-Process $ExePath -PassThru; $startedByTest = $true; $appPid = $proc.Id
  Start-Sleep 4
  $main = $null
  for ($try = 0; $try -lt 5; $try++) {
    [T46.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T46.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
    Start-Sleep 1
    $main = Find-AppWindow $appPid "ScreenTrans"
    if ($main) {
      try { ($main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal) } catch {}
      Start-Sleep -Milliseconds 400
      if ($main.Current.BoundingRectangle.X -gt -10000 -and -not $main.Current.IsOffscreen) { break }
    }
  }
  if (-not $main -or $main.Current.BoundingRectangle.X -le -10000) { write-host "主視窗還原失敗" -ForegroundColor Red; exit 1 }

  $item = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $folder.Name)))
  $ir = $item.Current.BoundingRectangle
  Invoke-ClickAt ([int]($ir.X + $ir.Width/2)) ([int]($ir.Y + $ir.Height/2))
  Start-Sleep -Milliseconds 800
  $h1 = $e1.Original.Substring(0, [Math]::Min(18, $e1.Original.Length))
  $h2 = $e2.Original.Substring(0, [Math]::Min(18, $e2.Original.Length))
  $t1 = Find-TextLike $main $h1; $t2 = Find-TextLike $main $h2
  if (-not $t1 -or -not $t2) { write-host "找不到兩條卡片" -ForegroundColor Red; exit 1 }
  write-host "* 定位 OK" -ForegroundColor Green

  #endregion

  #region B.通過卡有星色精確像素、未通過卡無 --------------------------------
  write-host "## B.通過卡有星色精確像素、未通過卡無 --------------------------------" -ForegroundColor Cyan

  $bmp = Get-WindowBitmap $main
  $n1 = Count-ExactColor $bmp $main $t1 $star1
  $n2 = Count-ExactColor $bmp $main $t2 $star2
  $bmp.Dispose()
  write-host "* 條1 星色像素=$n1、條2 星色像素=$n2"
  if ($n1 -gt 0) { write-host "* PASS：通過卡出現星紋（星色精確像素 $n1 顆）" -ForegroundColor Green }
  else { write-host "* FAIL：通過卡無星色像素" -ForegroundColor Red; $fail++ }
  if ($n2 -eq 0) { write-host "* PASS：未練卡素色（無星色像素）" -ForegroundColor Green }
  else { write-host "* FAIL：未練卡出現星色像素 $n2 顆" -ForegroundColor Red; $fail++ }
  [T46.Native]::SetCursorPos(5,5) | Out-Null; Start-Sleep -Milliseconds 300
  Save-WindowShot (Join-Path $ShotDir "notes-pass-pattern.png") $main

  #endregion

  #region C.Clear Practice → 花紋消失 --------------------------------
  write-host "## C.Clear Practice → 花紋消失 --------------------------------" -ForegroundColor Cyan

  $clearBtn = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, "ClearPracticeBtn")))
  ($clearBtn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Start-Sleep -Milliseconds 1000
  # 確認對話（Win32 MessageBox）：找同 pid 頂層窗中的 OK/Yes 鈕按下
  $confirmed = $false
  $wins = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $appPid)))
  foreach ($w in $wins) {
    foreach ($name in @("OK","Yes","確定","是")) {
      $btn = $w.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.AndCondition(
          (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)),
          (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))))
      if ($btn) { ($btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke(); $confirmed = $true; break }
    }
    if ($confirmed) { break }
  }
  if (-not $confirmed) { write-host "* 警示：未見確認對話（可能無確認即執行）" -ForegroundColor Yellow }
  Start-Sleep -Milliseconds 1200
  $t1 = Find-TextLike $main $h1
  $bmp = Get-WindowBitmap $main
  $n1b = Count-ExactColor $bmp $main $t1 $star1
  $bmp.Dispose()
  if ($n1b -eq 0) { write-host "* PASS：Clear Practice 後花紋消失（星色像素 0）" -ForegroundColor Green }
  else { write-host "* FAIL：清空後仍有星色像素 $n1b 顆" -ForegroundColor Red; $fail++ }

  #endregion

} finally {

  #region D.清理與資料還原 --------------------------------
  write-host "## D.清理與資料還原 --------------------------------" -ForegroundColor Cyan

  if ($startedByTest -and $appPid) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    write-host "* 已結束受測程式"
  }
  if (Test-Path $backupPath) {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "* 已還原 notes.json（造分/清空不留痕）" -ForegroundColor Green
  }

  #endregion
}

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

write-host "* 「評分達標當下就地點亮」未自動化（需真麥克風/API）——資料源＝store 現值已為 design 定本、"
write-host "  由程式碼審視與 ＜5節＞ 把關；重繪/就地兩路徑共用 NoteCardBrush.For 同一判定。"
if ($fail -eq 0) { write-host "結果：PASS（intTest#46 星紋出現/未過素色/清空消失 全數成立）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
