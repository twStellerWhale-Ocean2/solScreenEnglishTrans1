#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#42（Issue #104）：筆記卡片登記時間標示＋依時間正反排序——"
write-host "  (1) 卡片原文下顯示 AddedAt 本地時間小字；(2) Old→New/New→Old 依登記時間排序並即時落地 notes.json。"
write-host "* 資料保護：排序會改變使用者 notes.json 條目順序——起手備份、收尾（app 結束後）還原原始檔。"
write-host "* 前置要件：%APPDATA%\LingoIsland\notes.json 至少一夾含 >=2 條、AddedAt 相異之筆記。"
write-host "* 日期版本：2026-07-10 v1"

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  write-host "* RepoRoot： $RepoRoot"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物：$ExePath（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }

  $notesPath = Join-Path $env:APPDATA "LingoIsland\notes.json"
  if (-not (Test-Path $notesPath)) { write-host "前置不足：找不到 $notesPath" -ForegroundColor Red; exit 1 }
  $backupPath = "$notesPath.intTest42.bak"
  Copy-Item $notesPath $backupPath -Force
  write-host "* 已備份 notes.json → $backupPath"

  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 2 -and (($_.Entries.AddedAt | Sort-Object -Unique).Count -ge 2) } | Select-Object -First 1
  if (-not $folder) { Remove-Item $backupPath; write-host "前置不足：無一夾含 >=2 條 AddedAt 相異之筆記" -ForegroundColor Red; exit 1 }
  write-host "* 測試資料夾： $($folder.Name)（$($folder.Entries.Count) 條）"
  $ascExpected  = @($folder.Entries | Sort-Object { [DateTimeOffset]$_.AddedAt } | ForEach-Object { $_.Id })
  $descExpected = @($folder.Entries | Sort-Object { [DateTimeOffset]$_.AddedAt } -Descending | ForEach-Object { $_.Id })
  $firstLocal = ([DateTimeOffset]$folder.Entries[0].AddedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm", [System.Globalization.CultureInfo]::InvariantCulture)
  write-host "* 首條登記時間（預期卡上小字）： $firstLocal"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T42
  [T42.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y, [int]$Times = 1) {
    [T42.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt $Times; $i++) {
      [T42.Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
      [T42.Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
      Start-Sleep -Milliseconds 90
    }
  }

  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

  function Find-ById([Windows.Automation.AutomationElement]$Root, [string]$AutoId) {
    return $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $AutoId)))
  }

  function Invoke-Btn([Windows.Automation.AutomationElement]$Btn) {
    ($Btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
  }

  # 讀 notes.json 之測試夾條目 Id 順序
  function Read-FolderIds([string]$FolderName) {
    $n = Get-Content $notesPath -Raw | ConvertFrom-Json
    return @(($n.Folders | Where-Object Name -eq $FolderName).Entries | ForEach-Object { $_.Id })
  }

  # 以 PrintWindow 直接取目標視窗表面（不受 Z 序/其他視窗覆蓋影響——螢幕被並用時 CopyFromScreen 會截到覆蓋窗）
  function Save-WindowShot([string]$Path, [Windows.Automation.AutomationElement]$Win, [int]$Pad = 16) {
    $r = $Win.Current.BoundingRectangle
    $w = [int]$r.Width; $h = [int]$r.Height
    $raw = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($raw)
    $hdc = $g.GetHdc()
    [T42.Native]::PrintWindow([IntPtr][int64]$Win.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null  # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    # 裁 DWM 隱形邊框（左右下約 7px）並鋪淡色底
    $crop = New-Object System.Drawing.Rectangle(8, 1, ($w - 16), ($h - 9))
    $bmp = New-Object System.Drawing.Bitmap(($crop.Width + $Pad*2), ($crop.Height + $Pad*2))
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.Clear([System.Drawing.Color]::FromArgb(238, 236, 233))
    $g2.DrawImage($raw, (New-Object System.Drawing.Rectangle($Pad, $Pad, $crop.Width, $crop.Height)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $g2.Dispose(); $raw.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    write-host "* 截圖：$Path（PrintWindow 直取視窗表面）"
  }

  #endregion

#endregion

#region III.內容程序 ================================
write-host "# III.內容程序================================" -ForegroundColor Blue

$fail = 0
$startedByTest = $false
$appPid = 0

try {

  #region A.啟動與定位 --------------------------------
  write-host "## A.啟動與定位 --------------------------------" -ForegroundColor Cyan

  # 本測試改動並還原 notes.json——沿用既有實例時 app 記憶體殘留排序後順序、之後任一次 Save 會覆寫還原檔（§5 審查 #1），
  # 故一律要求以測試自啟實例執行
  if (Get-Process LingoIsland -ErrorAction SilentlyContinue) {
    Copy-Item $backupPath $notesPath -Force; Remove-Item $backupPath -Force
    write-host "偵測到執行中之 LingoIsland——請先結束後重跑（本測試需自啟實例以保證資料還原不留痕）" -ForegroundColor Red; exit 1
  }
  write-host "* 啟動受測程式（Release build）"
  $proc = Start-Process $ExePath -PassThru
  $startedByTest = $true
  Start-Sleep 4
  $appPid = $proc.Id

  $main = $null
  for ($try = 0; $try -lt 5; $try++) {
    [T42.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T42.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
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
  Invoke-ClickAt ([int]($ir.X + $ir.Width/2)) ([int]($ir.Y + $ir.Height/2)) 1
  Start-Sleep -Milliseconds 800
  write-host "* 已選取資料夾「$($folder.Name)」" -ForegroundColor Green

  foreach ($id in @("SortOldBtn","SortNewBtn","SortAscBtn","SortDescBtn")) {
    if (-not (Find-ById $main $id)) { write-host "找不到排序鈕 $id" -ForegroundColor Red; exit 1 }
  }
  write-host "* 四顆排序鈕定位 OK" -ForegroundColor Green

  #endregion

  #region B.卡片登記時間小字 --------------------------------
  write-host "## B.卡片登記時間小字 --------------------------------" -ForegroundColor Cyan

  $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
  $found = $false
  foreach ($t in $texts) { if ($t.Current.Name -eq $firstLocal) { $found = $true; break } }
  if ($found) { write-host "* PASS：卡片顯示登記時間小字（$firstLocal）" -ForegroundColor Green }
  else { write-host "* FAIL：找不到登記時間小字（$firstLocal）" -ForegroundColor Red; $fail++ }

  #endregion

  #region C.Old→New 依時間舊→新排序落地 --------------------------------
  write-host "## C.Old→New 依時間舊→新排序落地 --------------------------------" -ForegroundColor Cyan

  Invoke-Btn (Find-ById $main "SortOldBtn")
  Start-Sleep -Milliseconds 1200
  $ids = Read-FolderIds $folder.Name
  if (($ids -join ",") -eq ($ascExpected -join ",")) {
    write-host "* PASS：Old→New 後 notes.json 順序＝依 AddedAt 舊→新" -ForegroundColor Green
    [T42.Native]::SetCursorPos(5, 5) | Out-Null  # 移開游標免鈕面殘留 hover 態入鏡（§5 審查 #4）
    Start-Sleep -Milliseconds 400
    Save-WindowShot (Join-Path $ShotDir "notes-time-sort.png") $main
  } else {
    write-host "* FAIL：預期 $($ascExpected -join ',')、實得 $($ids -join ',')" -ForegroundColor Red; $fail++
  }

  #endregion

  #region D.New→Old 依時間新→舊排序落地 --------------------------------
  write-host "## D.New→Old 依時間新→舊排序落地 --------------------------------" -ForegroundColor Cyan

  Invoke-Btn (Find-ById $main "SortNewBtn")
  Start-Sleep -Milliseconds 1200
  $ids = Read-FolderIds $folder.Name
  if (($ids -join ",") -eq ($descExpected -join ",")) {
    write-host "* PASS：New→Old 後 notes.json 順序＝依 AddedAt 新→舊" -ForegroundColor Green
  } else {
    write-host "* FAIL：預期 $($descExpected -join ',')、實得 $($ids -join ',')" -ForegroundColor Red; $fail++
  }

  #endregion

  #region E.最小寬換行：560px 下五鈕全可視 --------------------------------
  write-host "## E.最小寬換行：560px 下五鈕全可視 --------------------------------" -ForegroundColor Cyan

  Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int X, int Y, int W, int H, bool r);' -Name Native2 -Namespace T42b
  [T42b.Native2]::MoveWindow((Get-Process -Id $appPid).MainWindowHandle, 100, 100, 560, 500, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $offCount = 0
  foreach ($id in @("SortAscBtn","SortDescBtn","SortOldBtn","SortNewBtn","ClearPracticeBtn")) {
    $b = Find-ById $main $id
    if (-not $b -or $b.Current.IsOffscreen) { $offCount++; write-host "* $id 不可視" -ForegroundColor Red }
  }
  if ($offCount -eq 0) { write-host "* PASS：560px（MinWidth）下五鈕換行全可視（invariant）" -ForegroundColor Green }
  else { write-host "* FAIL：$offCount 顆鈕不可視" -ForegroundColor Red; $fail++ }

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
    write-host "* 已還原 notes.json 原始內容（排序測試不留痕）" -ForegroundColor Green
  }

  #endregion
}

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

if ($fail -eq 0) { write-host "結果：PASS（intTest#42 時間標示/Old→New/New→Old 全數成立）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
