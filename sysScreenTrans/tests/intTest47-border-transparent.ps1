#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#47（Issue #118）：筆記卡未選外框＝底色×0.80 加深（白卡→#CCCCCC）；過關卡（最佳分>=門檻）"
write-host "  底色透明（帶內底色精確像素≈0、透浮水印）、未過卡素色；Clear Practice 回素色；"
write-host "  #110 選取/還原（回各自加深框、非常數）迴歸。期望色與 NoteCardBrush.Darken(×0.80) 同式鏡像。"
write-host "* 「評分達標當下就地變透明」需真麥克風/API 未自動化——判定資料源＝store 現值已定本（#111 承襲）、"
write-host "  重繪/就地共用 NoteCardBrush.For 同一判定；「門檻調升 Reload 重判」同屬重繪路徑論證。"
write-host "* 資料保護：備份 notes.json、造分；結束還原。強制自啟實例；100% DPI 前提。"
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
  $backupPath = "$notesPath.intTest47.bak"
  Copy-Item $notesPath $backupPath -Force
  write-host "* 已備份 notes.json"

  try {
    $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
    $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 2 } | Select-Object -First 1
    if (-not $folder) { throw "前置不足：無一夾含 >=2 條筆記" }
    $folder.Entries[0].PracticeScore = 100
    $folder.Entries[1].PracticeScore = -1
    $notes | ConvertTo-Json -Depth 10 | Set-Content $notesPath -Encoding UTF8
  } catch {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "造分失敗、已還原：$_" -ForegroundColor Red; exit 1
  }
  $e1 = $folder.Entries[0]; $e2 = $folder.Entries[1]
  write-host "* 測試夾「$($folder.Name)」：條1=100（過）、條2=-1（未練）"

  # 底色/框色鏡像計算（NoteCardBrush：無底色退白；框＝先拉飽和 ×1.6 再加深 ×0.62、鉗制截斷，#123）
  function Get-BaseRgb([string]$hex) {
    if ([string]::IsNullOrWhiteSpace($hex)) { $hex = "#FFFFFF" }
    return @([Convert]::ToInt32($hex.Substring(1,2),16), [Convert]::ToInt32($hex.Substring(3,2),16), [Convert]::ToInt32($hex.Substring(5,2),16))
  }
  function Get-BorderRgb([string]$hex) {
    $b = Get-BaseRgb $hex
    $mean = ($b[0] + $b[1] + $b[2]) / 3.0
    $chan = { param($c) [int][Math]::Max(0, [Math]::Min(255, [Math]::Truncate(($mean + ($c - $mean) * 1.6) * 0.62))) }
    return @((& $chan $b[0]), (& $chan $b[1]), (& $chan $b[2]))
  }
  $base1 = Get-BaseRgb $e1.Color;  $bord1 = Get-BorderRgb $e1.Color
  $base2 = Get-BaseRgb $e2.Color;  $bord2 = Get-BorderRgb $e2.Color
  write-host "* 條1 底RGB($($base1 -join ','))/框RGB($($bord1 -join ','))；條2 底RGB($($base2 -join ','))/框RGB($($bord2 -join ','))"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T47
  [T47.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y) {
    [T47.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    [T47.Native]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [T47.Native]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
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
    [T47.Native]::PrintWindow([IntPtr][int64]$Win.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
  }

  # 卡片帶內精確色像素計數（帶＝條目文字垂直範圍±16、x 自文字左 60px 起）
  function Count-ExactColor([System.Drawing.Bitmap]$Bmp, [Windows.Automation.AutomationElement]$Win, [Windows.Automation.AutomationElement]$Elem, [int[]]$Rgb) {
    $wr = $Win.Current.BoundingRectangle; $er = $Elem.Current.BoundingRectangle
    $y0 = [Math]::Max(0, [int]($er.Y - $wr.Y) - 16); $y1 = [Math]::Min($Bmp.Height - 1, [int]($er.Y - $wr.Y + $er.Height) + 16)
    $x0 = [Math]::Max(0, [int]($er.X - $wr.X) - 60); $x1 = $Bmp.Width - 1
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
    [T47.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T47.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
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

  #region B.外框＝底色×0.80 加深（兩卡皆有） --------------------------------
  write-host "## B.外框＝底色×0.80 加深（兩卡皆有） --------------------------------" -ForegroundColor Cyan

  $bmp = Get-WindowBitmap $main
  $nb1 = Count-ExactColor $bmp $main $t1 $bord1
  $nb2 = Count-ExactColor $bmp $main $t2 $bord2
  write-host "* 條1 框色像素=$nb1、條2 框色像素=$nb2"
  if ($nb1 -gt 0 -and $nb2 -gt 0) { write-host "* PASS：兩卡外框皆為各自底色×0.80 加深色" -ForegroundColor Green }
  else { write-host "* FAIL：框色像素缺（條1=$nb1 條2=$nb2）" -ForegroundColor Red; $fail++ }

  #endregion

  #region C.過關卡底透明、未過卡素色 --------------------------------
  write-host "## C.過關卡底透明、未過卡素色 --------------------------------" -ForegroundColor Cyan

  $n1 = Count-ExactColor $bmp $main $t1 $base1
  $n2 = Count-ExactColor $bmp $main $t2 $base2
  $bmp.Dispose()
  write-host "* 條1（過）底色像素=$n1、條2（未練）底色像素=$n2"
  if ($n1 -lt 30) { write-host "* PASS：過關卡底透明（底色精確像素≈0、透出浮水印複合色）" -ForegroundColor Green }
  else { write-host "* FAIL：過關卡仍有底色像素 $n1" -ForegroundColor Red; $fail++ }
  if ($n2 -gt 300) { write-host "* PASS：未練卡素色底（底色像素 $n2）" -ForegroundColor Green }
  else { write-host "* FAIL：未練卡底色像素過少 $n2" -ForegroundColor Red; $fail++ }
  [T47.Native]::SetCursorPos(5,5) | Out-Null; Start-Sleep -Milliseconds 300
  Save-WindowShot (Join-Path $ShotDir "notes-pass-pattern.png") $main

  #endregion

  #region D.#110 選取/快取還原迴歸 --------------------------------
  write-host "## D.#110 選取/快取還原迴歸 --------------------------------" -ForegroundColor Cyan

  # 拆兩獨立步驟、各自「點擊→驗末態」重試（雙擊鏈脆弱、命中率低）：
  #   步驟1 點卡1 → 卡1 深粉選取；步驟2 點卡2 → 卡2 深粉、且卡1「還原為其加深框」（快取式還原核心斷言）
  $DP = @(176,87,141) # 深粉 #B0578D
  function Sel-Click([Windows.Automation.AutomationElement]$T) {
    [T47.Native]::SetForegroundWindow([IntPtr][int64]$main.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
    $r = $T.Current.BoundingRectangle
    Invoke-ClickAt ([int]($r.X + $r.Width/2)) ([int]($r.Y + $r.Height/2))
    Start-Sleep -Milliseconds 600
  }
  $s1 = $false
  for ($k = 0; $k -lt 4 -and -not $s1; $k++) {
    Sel-Click $t1
    $bmp = Get-WindowBitmap $main; $on1 = Count-ExactColor $bmp $main $t1 $DP; $bmp.Dispose()
    $s1 = ($on1 -gt 0)
    if (-not $s1) { write-host "* 卡1 選取未見深粉（第 $($k+1) 次）on1=$on1，重試…" -ForegroundColor Yellow }
  }
  if ($s1) { write-host "* PASS：點卡1 → 卡1 深粉選取框" -ForegroundColor Green }
  else { write-host "* FAIL：點卡1 未見深粉選取" -ForegroundColor Red; $fail++ }

  $s2 = $false
  for ($k = 0; $k -lt 4 -and -not $s2; $k++) {
    Sel-Click $t2
    $bmp = Get-WindowBitmap $main
    $on2 = Count-ExactColor $bmp $main $t2 $DP        # 卡2 深粉
    $nb1r = Count-ExactColor $bmp $main $t1 $bord1    # 卡1 還原為其加深框（非常數淡粉、非深粉殘留）
    $bmp.Dispose()
    $s2 = ($on2 -gt 0 -and $nb1r -gt 0)
    if (-not $s2) { write-host "* 移轉未達預期（第 $($k+1) 次）on2=$on2 nb1r=$nb1r，重試…" -ForegroundColor Yellow }
  }
  if ($s2) { write-host "* PASS：點卡2 → 選取移轉、卡1 框還原為其加深框（快取式還原）" -ForegroundColor Green }
  else { write-host "* FAIL：移轉/快取還原異常" -ForegroundColor Red; $fail++ }

  #endregion

  #region E.Clear Practice → 過關卡回素色 --------------------------------
  write-host "## E.Clear Practice → 過關卡回素色 --------------------------------" -ForegroundColor Cyan

  $clearBtn = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, "ClearPracticeBtn")))
  ($clearBtn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Start-Sleep -Milliseconds 1000
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
  Start-Sleep -Milliseconds 1200
  $t1 = Find-TextLike $main $h1
  $bmp = Get-WindowBitmap $main
  $n1c = Count-ExactColor $bmp $main $t1 $base1
  $bmp.Dispose()
  if ($n1c -gt 300) { write-host "* PASS：清空後過關卡回素色（底色像素 $n1c）" -ForegroundColor Green }
  else { write-host "* FAIL：清空後底色像素僅 $n1c" -ForegroundColor Red; $fail++ }

  #endregion

} finally {

  #region F.清理與資料還原 --------------------------------
  write-host "## F.清理與資料還原 --------------------------------" -ForegroundColor Cyan

  if ($startedByTest -and $appPid) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    write-host "* 已結束受測程式"
  }
  if (Test-Path $backupPath) {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "* 已還原 notes.json（不留痕）" -ForegroundColor Green
  }

  #endregion
}

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

if ($fail -eq 0) { write-host "結果：PASS（intTest#47 外框加深/過關透明/清空回素/選取還原 全數成立）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
