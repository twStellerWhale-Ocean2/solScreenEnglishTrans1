#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#40（Issue #105）：查詢結果視窗與主視窗共存——點選主視窗不自動關閉結果視窗；"
write-host "  歷史/筆記「檢視」開卡走單一取代守衛（同時至多一張、無孤兒卡）。"
write-host "* 工法：UI Automation（UIA）驅動實際執行中之 ScreenTrans（WPF），SendInput 實體滑鼠事件，"
write-host "  關鍵狀態以 CopyFromScreen 截圖存證（docs/manual-assets/）。"
write-host "* 前置要件：%APPDATA%\ScreenTrans\notes.json 至少一個資料夾含 >=2 條筆記（檢視動線唯讀、不改使用者資料）。"
write-host "* 日期版本：2026-07-10 v1"

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysScreenTrans\bin\Release\net9.0-windows10.0.19041.0\ScreenTrans.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  $ResultTitle = "Query Result"
  write-host "* RepoRoot   ： $RepoRoot"
  write-host "* ExePath    ： $ExePath"
  write-host "* ShotDir    ： $ShotDir"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物：$ExePath（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Force $ShotDir | Out-Null }

  $notesPath = Join-Path $env:APPDATA "ScreenTrans\notes.json"
  if (-not (Test-Path $notesPath)) { write-host "前置不足：找不到 $notesPath（需至少一夾 >=2 條筆記）" -ForegroundColor Red; exit 1 }
  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 2 } | Select-Object -First 1
  if (-not $folder) { write-host "前置不足：無任一資料夾含 >=2 條筆記" -ForegroundColor Red; exit 1 }
  write-host "* 測試資料夾： $($folder.Name)（$($folder.Entries.Count) 條）"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@ -Name Native -Namespace T40
  [T40.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  # 於實體座標按左鍵（down+up）；$Times=2 即雙擊
  function Invoke-ClickAt([int]$X, [int]$Y, [int]$Times = 1) {
    [T40.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt $Times; $i++) {
      [T40.Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)  # LEFTDOWN
      [T40.Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)  # LEFTUP
      Start-Sleep -Milliseconds 90
    }
  }

  # 依標題與 pid 找頂層視窗（UIA），$null＝找不到
  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

  # 計數同 pid 同標題之頂層視窗
  function Count-AppWindows([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond).Count
  }

  # 區域截圖存檔：底色填滿聯集框，僅拷貝受測視窗矩形——確實不入使用者桌面其他內容
  function Save-RegionShot([string]$Path, [System.Windows.Rect[]]$Rects, [int]$Margin = 16) {
    $x0 = [int](($Rects | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum) - $Margin
    $y0 = [int](($Rects | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum) - $Margin
    $x1 = [int](($Rects | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum) + $Margin
    $y1 = [int](($Rects | ForEach-Object { $_.Y + $_.Height } | Measure-Object -Maximum).Maximum) + $Margin
    $bmp = New-Object System.Drawing.Bitmap(($x1 - $x0), ($y1 - $y0))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(238, 236, 233))
    foreach ($r in $Rects) {
      # UIA 矩形含 DWM 隱形縮放邊框（左右下約 7px），內縮避免滲入背景
      $ix = [int]$r.X + 8; $iy = [int]$r.Y + 1
      $iw = [int]$r.Width - 16; $ih = [int]$r.Height - 9
      $g.CopyFromScreen($ix, $iy, ($ix - $x0), ($iy - $y0), (New-Object System.Drawing.Size($iw, $ih)))
    }
    $g.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    write-host "* 截圖：$Path（$x0,$y0 → $x1,$y1，背景已遮蔽）"
  }

  #endregion

#endregion

#region III.內容程序 ================================
write-host "# III.內容程序================================" -ForegroundColor Blue

$fail = 0
$startedByTest = $false

  #region A.啟動與定位主視窗 --------------------------------
  write-host "## A.啟動與定位主視窗 --------------------------------" -ForegroundColor Cyan

  $proc = Get-Process ScreenTrans -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) {
    write-host "* 啟動受測程式（Release build）"
    $proc = Start-Process $ExePath -PassThru
    $startedByTest = $true
    Start-Sleep 4
  } else { write-host "* 沿用執行中之 ScreenTrans（pid $($proc.Id)）" }
  $appPid = $proc.Id

  [T40.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
  [T40.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
  Start-Sleep 1
  $main = Find-AppWindow $appPid "ScreenTrans"
  if (-not $main) { write-host "找不到主視窗（UIA）" -ForegroundColor Red; exit 1 }
  $mr = $main.Current.BoundingRectangle
  write-host "* 主視窗定位 OK（$([int]$mr.X),$([int]$mr.Y) $([int]$mr.Width)x$([int]$mr.Height)）" -ForegroundColor Green

  #endregion

  #region B.選資料夾並「檢視」開結果卡 --------------------------------
  write-host "## B.選資料夾並「檢視」開結果卡 --------------------------------" -ForegroundColor Cyan

  $condTree = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $folder.Name)
  $item = $main.FindFirst([Windows.Automation.TreeScope]::Descendants, $condTree)
  if (-not $item) { write-host "筆記樹找不到資料夾節點「$($folder.Name)」（確認預設分頁為筆記）" -ForegroundColor Red; exit 1 }
  $ir = $item.Current.BoundingRectangle
  Invoke-ClickAt ([int]($ir.X + $ir.Width * 0.5)) ([int]($ir.Y + $ir.Height * 0.5)) 1
  Start-Sleep -Milliseconds 800
  write-host "* 已選取資料夾「$($folder.Name)」"

  # 條目卡片之 StackPanel/Border 無 automation peer，改以卡片內文字（Original）定位
  function Find-EntryText([Windows.Automation.AutomationElement]$Root, [string]$Original) {
    $texts = $Root.FindAll([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -like "$($Original.Substring(0, [Math]::Min(20, $Original.Length)))*") { return $t } }
    return $null
  }
  $e1 = Find-EntryText $main $folder.Entries[0].Original
  $e2 = Find-EntryText $main $folder.Entries[1].Original
  if (-not $e1 -or -not $e2) { write-host "找不到條目文字（第1條=$([bool]$e1)、第2條=$([bool]$e2)）" -ForegroundColor Red; exit 1 }
  $r1 = $e1.Current.BoundingRectangle
  write-host "* 雙擊第 1 條（檢視）：$($folder.Entries[0].Original.Substring(0,20))…"
  Invoke-ClickAt ([int]($r1.X + $r1.Width * 0.5)) ([int]($r1.Y + $r1.Height * 0.5)) 2
  Start-Sleep 2
  if (-not (Find-AppWindow $appPid $ResultTitle)) { write-host "檢視未開出結果視窗（$ResultTitle）" -ForegroundColor Red; exit 1 }
  write-host "* 結果視窗已開啟" -ForegroundColor Green

  #endregion

  #region C.點主視窗→結果卡須保留（Issue #105 主斷言） --------------------------------
  write-host "## C.點主視窗→結果卡須保留（Issue #105 主斷言） --------------------------------" -ForegroundColor Cyan

  $mr = $main.Current.BoundingRectangle
  $rw = (Find-AppWindow $appPid $ResultTitle).Current.BoundingRectangle
  # 取主視窗內、且不被 Topmost 結果卡覆蓋之點（候選：狀態列左端→左緣中段→標題列左端）
  $pts = @(
    @([int]($mr.X + 150), [int]($mr.Y + $mr.Height - 15)),
    @([int]($mr.X + 20),  [int]($mr.Y + $mr.Height * 0.5)),
    @([int]($mr.X + 120), [int]($mr.Y + 14)))
  $pt = $pts | Where-Object { $_[0] -lt $rw.X -or $_[0] -gt ($rw.X + $rw.Width) -or $_[1] -lt $rw.Y -or $_[1] -gt ($rw.Y + $rw.Height) } | Select-Object -First 1
  if (-not $pt) { write-host "找不到未被結果卡覆蓋之主視窗點擊點（請挪動視窗後重跑）" -ForegroundColor Red; exit 1 }
  write-host "* 點選主視窗（$($pt[0]),$($pt[1])）使其 Activated"
  Invoke-ClickAt $pt[0] $pt[1] 1
  Start-Sleep -Milliseconds 1200
  $fg = [T40.Native]::GetForegroundWindow()
  if ([int64]$fg -ne [int64]$main.Current.NativeWindowHandle) {
    write-host "前置失真：點擊後前景視窗非主視窗（測試無效，重跑或檢查視窗位置）" -ForegroundColor Red; exit 1
  }
  write-host "* 已確認主視窗為前景（Activated 確實發生）"
  $resWin = Find-AppWindow $appPid $ResultTitle
  if ($resWin) {
    write-host "* PASS：點主視窗後結果視窗保留、未被自動關閉" -ForegroundColor Green
    Save-RegionShot (Join-Path $ShotDir "result-coexist-main.png") @($main.Current.BoundingRectangle, $resWin.Current.BoundingRectangle)
  } else {
    write-host "* FAIL：點主視窗後結果視窗被關閉（回歸 Issue #105）" -ForegroundColor Red; $fail++
  }

  #endregion

  #region D.再「檢視」第 2 條→單一取代守衛 --------------------------------
  write-host "## D.再「檢視」第 2 條→單一取代守衛 --------------------------------" -ForegroundColor Cyan

  $r2 = $e2.Current.BoundingRectangle
  write-host "* 雙擊第 2 條（檢視）：$($folder.Entries[1].Original.Substring(0,20))…"
  Invoke-ClickAt ([int]($r2.X + $r2.Width * 0.5)) ([int]($r2.Y + $r2.Height * 0.5)) 2
  Start-Sleep 2
  $n = Count-AppWindows $appPid $ResultTitle
  if ($n -eq 1) {
    write-host "* PASS：前卡被取代、同時僅一張結果視窗（無孤兒卡）" -ForegroundColor Green
  } else {
    write-host "* FAIL：結果視窗數=$n（預期 1；0=未開、>=2=孤兒卡）" -ForegroundColor Red; $fail++
  }

  #endregion

  #region E.清理 --------------------------------
  write-host "## E.清理 --------------------------------" -ForegroundColor Cyan

  $res = Find-AppWindow $appPid $ResultTitle
  if ($res) { ($res.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close(); Start-Sleep -Milliseconds 400; write-host "* 已關閉結果視窗" }
  if ($startedByTest) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    write-host "* 已結束由本測試啟動之受測程式"
  } else { write-host "* 保留原執行中之程式（非本測試啟動）" -ForegroundColor Yellow }

  #endregion

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

if ($fail -eq 0) { write-host "結果：PASS（intTest#40 全數成立）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
