#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#43（Issue #106）：筆記右鍵選單收斂——單層色塊平鋪（No Color＋粉彩盤、目前色打勾）"
write-host "  ＋分隔線＋Delete；選單無 Play/View；點色套底色落地、Delete 刪該筆。"
write-host "* 資料保護：套色與刪除會改動 notes.json——起手備份、收尾（app 結束後）還原；強制自啟實例。"
write-host "* 前置要件：%APPDATA%\LingoIsland\notes.json 至少一夾含 >=1 條筆記。"
write-host "* 日期版本：2026-07-10 v1"

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物：$ExePath（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }

  $notesPath = Join-Path $env:APPDATA "LingoIsland\notes.json"
  if (-not (Test-Path $notesPath)) { write-host "前置不足：找不到 $notesPath" -ForegroundColor Red; exit 1 }
  $backupPath = "$notesPath.intTest43.bak"
  Copy-Item $notesPath $backupPath -Force
  write-host "* 已備份 notes.json"

  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 1 } | Select-Object -First 1
  if (-not $folder) { Remove-Item $backupPath; write-host "前置不足：無筆記" -ForegroundColor Red; exit 1 }
  $entry = $folder.Entries[0]
  write-host "* 測試夾「$($folder.Name)」、首條：$($entry.Original.Substring(0,[Math]::Min(24,$entry.Original.Length)))…"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T43
  [T43.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y, [string]$Btn = "L") {
    [T43.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    if ($Btn -eq "R") { [T43.Native]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero); [T43.Native]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero) }
    else { [T43.Native]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [T43.Native]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero) }
  }

  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

  # 取目前開啟之 WPF ContextMenu 的 MenuItem 名稱清單與元素（Popup 為同 pid 之頂層視窗）
  function Get-OpenMenuItems([int]$AppPid) {
    $wins = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)))
    foreach ($w in $wins) {
      $items = $w.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)))
      if ($items.Count -gt 0) { return @{ Window = $w; Items = $items } }
    }
    return $null
  }

  # 主視窗＝PrintWindow 直取（不受他窗覆蓋）；選單 Popup＝CopyFromScreen（WPF layered window 對 PrintWindow
  # 常回透明；選單為最上層小區域、被覆蓋風險低）。畫布嚴格＝主視窗裁切區（無邊緣留白、杜絕畫布外滲入），
  # 彈窗超出主視窗部分裁掉（選單在窗內、實務不裁）。
  function Save-ComposedShot([string]$Path, [Windows.Automation.AutomationElement]$Main, [Windows.Automation.AutomationElement]$Popup) {
    $mr = $Main.Current.BoundingRectangle; $pr = $Popup.Current.BoundingRectangle
    $w = [int]$mr.Width; $h = [int]$mr.Height
    $raw = New-Object System.Drawing.Bitmap($w, $h)
    $gg = [System.Drawing.Graphics]::FromImage($raw); $hdc = $gg.GetHdc()
    [T43.Native]::PrintWindow([IntPtr][int64]$Main.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null
    $gg.ReleaseHdc($hdc); $gg.Dispose()
    $cw = $w - 16; $ch = $h - 9   # 去 DWM 隱形邊框（左右各 8、下 8、上 1）
    $bmp = New-Object System.Drawing.Bitmap($cw, $ch)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.DrawImage($raw, (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)),
      (New-Object System.Drawing.Rectangle(8, 1, $cw, $ch)), [System.Drawing.GraphicsUnit]::Pixel)
    $raw.Dispose()
    $g.SetClip((New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)))
    $g.CopyFromScreen([int]$pr.X, [int]$pr.Y, [int]($pr.X - $mr.X - 8), [int]($pr.Y - $mr.Y - 1),
      (New-Object System.Drawing.Size([Math]::Min([int]$pr.Width, $cw), [Math]::Min([int]$pr.Height, $ch))))
    $g.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    write-host "* 截圖：$Path（PrintWindow 合成主視窗＋選單）"
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

  if (Get-Process LingoIsland -ErrorAction SilentlyContinue) {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "偵測到執行中之 LingoIsland——請先結束後重跑（需自啟實例以保證資料還原不留痕）" -ForegroundColor Red; exit 1
  }
  $proc = Start-Process $ExePath -PassThru
  $startedByTest = $true
  $appPid = $proc.Id
  Start-Sleep 4

  $main = $null
  for ($try = 0; $try -lt 5; $try++) {
    [T43.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T43.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
    Start-Sleep 1
    $main = Find-AppWindow $appPid "LingoIsland"
    if ($main) {
      try { ($main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal) } catch {}
      Start-Sleep -Milliseconds 400
      if ($main.Current.BoundingRectangle.X -gt -10000 -and -not $main.Current.IsOffscreen) { break }
    }
    write-host "* 主視窗尚未還原（第 $($try+1) 次），重試…" -ForegroundColor Yellow
  }
  if (-not $main -or $main.Current.BoundingRectangle.X -le -10000) { write-host "主視窗還原失敗" -ForegroundColor Red; exit 1 }

  $item = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $folder.Name)))
  if (-not $item) { write-host "筆記樹找不到資料夾「$($folder.Name)」" -ForegroundColor Red; exit 1 }
  $ir = $item.Current.BoundingRectangle
  Invoke-ClickAt ([int]($ir.X + $ir.Width/2)) ([int]($ir.Y + $ir.Height/2))
  Start-Sleep -Milliseconds 800

  $head = $entry.Original.Substring(0, [Math]::Min(20, $entry.Original.Length))
  $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
  $target = $null
  foreach ($t in $texts) { if ($t.Current.Name -like "$head*") { $target = $t; break } }
  if (-not $target) { write-host "找不到首條卡片文字" -ForegroundColor Red; exit 1 }
  write-host "* 定位首條卡片 OK" -ForegroundColor Green

  #endregion

  #region B.右鍵選單結構：色塊平鋪＋分隔線＋Delete、無 Play/View --------------------------------
  write-host "## B.右鍵選單結構：色塊平鋪＋分隔線＋Delete、無 Play/View --------------------------------" -ForegroundColor Cyan

  $tr = $target.Current.BoundingRectangle
  Invoke-ClickAt ([int]($tr.X + $tr.Width/2)) ([int]($tr.Y + $tr.Height/2)) "R"
  Start-Sleep -Milliseconds 1000
  $menu = Get-OpenMenuItems $appPid
  if (-not $menu) { write-host "* FAIL：右鍵未開出選單" -ForegroundColor Red; $fail++ }
  else {
    $names = @($menu.Items | ForEach-Object { $_.Current.Name })
    write-host "* 選單項：$($names -join ' | ')"
    $expect = @("No Color","Pink","Blue","Green","Yellow","Gray","Delete")
    $missing = @($expect | Where-Object { $n = $_; -not ($names | Where-Object { $_ -like "$n*" }) })
    $banned  = @($names | Where-Object { $_ -match "Play|View" })
    # 排除視窗系統選單雜項（UIA 會多回一個「System」MenuItem）後驗項數與 Delete 居末
    $real = @($names | Where-Object { $_ -ne "System" })
    $countOk = ($real.Count -eq 7) -and ($real[-1] -eq "Delete")
    if ($missing.Count -eq 0 -and $banned.Count -eq 0 -and $countOk) {
      write-host "* PASS：色塊七項齊備（No Color＋五色＋Delete 居末）、無 Play/View" -ForegroundColor Green
      Save-ComposedShot (Join-Path $ShotDir "notes-menu-colors.png") $main $menu.Window
    } else {
      write-host "* FAIL：缺項=$($missing -join ',')、違禁項=$($banned -join ',')、項數/居末=$countOk" -ForegroundColor Red; $fail++
    }
  }

  #endregion

  #region C.點色套底色落地 --------------------------------
  write-host "## C.點色套底色落地 --------------------------------" -ForegroundColor Cyan

  if (-not $menu) { write-host "* SKIP：§B 未開出選單，本節略過" -ForegroundColor Yellow; $fail++; $blue = $null }
  else { $blue = $menu.Items | Where-Object { $_.Current.Name -like "Blue*" } | Select-Object -First 1 }
  if ($blue) {
    ($blue.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 1000
    $n2 = Get-Content $notesPath -Raw | ConvertFrom-Json
    $c = (($n2.Folders | Where-Object Name -eq $folder.Name).Entries | Where-Object Id -eq $entry.Id).Color
    if ($c -eq "#E1EFFB") { write-host "* PASS：點 Blue → notes.json Color=#E1EFFB 落地" -ForegroundColor Green }
    else { write-host "* FAIL：Color=$c（預期 #E1EFFB）" -ForegroundColor Red; $fail++ }
    # 套色後重開選單：打勾應移至新色（§5 審查 B-4）
    $texts2 = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
    $t2 = $null
    foreach ($t in $texts2) { if ($t.Current.Name -like "$head*") { $t2 = $t; break } }
    $r2 = $t2.Current.BoundingRectangle
    Invoke-ClickAt ([int]($r2.X + $r2.Width/2)) ([int]($r2.Y + $r2.Height/2)) "R"
    Start-Sleep -Milliseconds 1000
    $menu2 = Get-OpenMenuItems $appPid
    $blueChecked = $menu2 -and (@($menu2.Items | Where-Object { $_.Current.Name -like "Blue*✓*" }).Count -eq 1)
    if ($blueChecked) { write-host "* PASS：重開選單後打勾移至 Blue" -ForegroundColor Green }
    else { write-host "* FAIL：重開選單 Blue 未帶勾" -ForegroundColor Red; $fail++ }
    [T43.Native]::SetCursorPos(5,5) | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}") # 關閉選單再進下一節
    Start-Sleep -Milliseconds 500
  } else { write-host "* FAIL：選單無 Blue 項" -ForegroundColor Red; $fail++ }

  #endregion

  #region D.Delete 於分隔線後、點擊刪該筆 --------------------------------
  write-host "## D.Delete 於分隔線後、點擊刪該筆 --------------------------------" -ForegroundColor Cyan

  # 重新右鍵開選單（重繪後元素已換新）
  $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
  $target = $null
  foreach ($t in $texts) { if ($t.Current.Name -like "$head*") { $target = $t; break } }
  $tr = $target.Current.BoundingRectangle
  Invoke-ClickAt ([int]($tr.X + $tr.Width/2)) ([int]($tr.Y + $tr.Height/2)) "R"
  Start-Sleep -Milliseconds 1000
  $menu = Get-OpenMenuItems $appPid
  $del = $menu.Items | Where-Object { $_.Current.Name -eq "Delete" } | Select-Object -First 1
  if ($del) {
    ($del.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 1000
    $n3 = Get-Content $notesPath -Raw | ConvertFrom-Json
    $left = @(($n3.Folders | Where-Object Name -eq $folder.Name).Entries | Where-Object Id -eq $entry.Id)
    if ($left.Count -eq 0) { write-host "* PASS：Delete 後該筆自 notes.json 移除" -ForegroundColor Green }
    else { write-host "* FAIL：該筆仍在" -ForegroundColor Red; $fail++ }
  } else { write-host "* FAIL：選單無 Delete 項" -ForegroundColor Red; $fail++ }

  #endregion

} finally {

  #region E.清理與資料還原 --------------------------------
  write-host "## E.清理與資料還原 --------------------------------" -ForegroundColor Cyan

  if ($startedByTest -and $appPid) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    write-host "* 已結束由本測試啟動之受測程式"
  }
  if (Test-Path $backupPath) {
    Copy-Item $backupPath $notesPath -Force
    Remove-Item $backupPath -Force
    write-host "* 已還原 notes.json 原始內容（套色/刪除測試不留痕）" -ForegroundColor Green
  }

  #endregion
}

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

if ($fail -eq 0) { write-host "結果：PASS（intTest#43 選單結構/套色/刪除 全數成立）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
