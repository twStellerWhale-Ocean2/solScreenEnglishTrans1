#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#41（Issue #107）：主視窗功能列 Result 鈕喚回查詢結果視窗——"
write-host "  態1 開啟中→帶前景不新開；態2 最小化中→還原帶前景；態3 已關閉有歷史→以最新一筆重開（單一守衛）。"
write-host "* 態4（無任何歷史→toast 不開卡）不在本腳本自動化：不得清除使用者真實 history.json；"
write-host "  該態由組合根 SummonResult 之 Load() 空集分支與既有 ToastNotifier 機制論證，並列 design intTest#41 供乾淨環境人工驗。"
write-host "* 前置要件：%APPDATA%\LingoIsland\history.json 至少 1 筆查詢歷史。"
write-host "* 日期版本：2026-07-10 v1"
write-host "* 【已淘汰 OBSOLETE（#135）】查詢結果併入主視窗 Dictionary 分頁、浮動結果視窗與功能列『Result』喚回鈕已移除——本案所測之浮窗喚回三態已不存在。" -ForegroundColor Yellow
write-host "  對應新驗收見 design intTest#48（Dictionary 分頁；tray『Result』改為切至分頁），待發車環（release train）對成品以 UIA 重寫。本腳本停用、不執行。" -ForegroundColor Yellow
exit 0

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  $ResultTitle = "Query Result"
  write-host "* RepoRoot： $RepoRoot"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物：$ExePath（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $ShotDir)) { New-Item -ItemType Directory -Force $ShotDir | Out-Null }

  $histPath = Join-Path $env:APPDATA "LingoIsland\history.json"
  if (-not (Test-Path $histPath)) { write-host "前置不足：找不到 $histPath（需至少 1 筆歷史）" -ForegroundColor Red; exit 1 }
  $hist = Get-Content $histPath -Raw | ConvertFrom-Json
  if (-not $hist -or $hist.Count -lt 1) { write-host "前置不足：查詢歷史為空" -ForegroundColor Red; exit 1 }
  $latest = $hist[0]
  write-host "* 歷史最新一筆： $($latest.Original.Substring(0, [Math]::Min(30, $latest.Original.Length)))…"

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
'@ -Name Native -Namespace T41
  [T41.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y, [int]$Times = 1) {
    [T41.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt $Times; $i++) {
      [T41.Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
      [T41.Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
      Start-Sleep -Milliseconds 90
    }
  }

  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

  function Count-AppWindows([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $cond).Count
  }

  # 找主視窗功能列 Result 鈕（AutomationId=ResultBtn）
  function Find-ResultBtn([Windows.Automation.AutomationElement]$Main) {
    return $Main.FindFirst([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, "ResultBtn")))
  }

  function Save-RegionShot([string]$Path, [System.Windows.Rect[]]$Rects, [int]$Margin = 16) {
    $x0 = [int](($Rects | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum) - $Margin
    $y0 = [int](($Rects | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum) - $Margin
    $x1 = [int](($Rects | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum) + $Margin
    $y1 = [int](($Rects | ForEach-Object { $_.Y + $_.Height } | Measure-Object -Maximum).Maximum) + $Margin
    $bmp = New-Object System.Drawing.Bitmap(($x1 - $x0), ($y1 - $y0))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(238, 236, 233))
    foreach ($r in $Rects) {
      $ix = [int]$r.X + 8; $iy = [int]$r.Y + 1
      $iw = [int]$r.Width - 16; $ih = [int]$r.Height - 9
      $g.CopyFromScreen($ix, $iy, ($ix - $x0), ($iy - $y0), (New-Object System.Drawing.Size($iw, $ih)))
    }
    $g.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    write-host "* 截圖：$Path（背景已遮蔽）"
  }

  #endregion

#endregion

#region III.內容程序 ================================
write-host "# III.內容程序================================" -ForegroundColor Blue

$fail = 0
$startedByTest = $false

  #region A.啟動與定位 --------------------------------
  write-host "## A.啟動與定位 --------------------------------" -ForegroundColor Cyan

  $proc = Get-Process LingoIsland -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) {
    write-host "* 啟動受測程式（Release build）"
    $proc = Start-Process $ExePath -PassThru
    $startedByTest = $true
    Start-Sleep 4
  } else { write-host "* 沿用執行中之 LingoIsland（pid $($proc.Id)）" }
  $appPid = $proc.Id

  # 還原＋前景確保（重試至多 5 次）：主視窗預設最小化，rect 在 -32000 時所有點擊都會落空
  $main = $null
  for ($try = 0; $try -lt 5; $try++) {
    [T41.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T41.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
    Start-Sleep 1
    $main = Find-AppWindow $appPid "LingoIsland"
    if ($main) {
      try { ($main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal) } catch {}
      Start-Sleep -Milliseconds 400
      $r0 = $main.Current.BoundingRectangle
      if ($r0.X -gt -10000 -and -not $main.Current.IsOffscreen) { break }
    }
    write-host "* 主視窗尚未還原（第 $($try+1) 次），重試…" -ForegroundColor Yellow
  }
  if (-not $main) { write-host "找不到主視窗（UIA）" -ForegroundColor Red; exit 1 }
  if ($main.Current.BoundingRectangle.X -le -10000) { write-host "主視窗還原失敗（rect 仍在螢幕外）" -ForegroundColor Red; exit 1 }
  $btn = Find-ResultBtn $main
  if (-not $btn) { write-host "找不到功能列 Result 鈕（AutomationId=ResultBtn）" -ForegroundColor Red; exit 1 }
  $br = $btn.Current.BoundingRectangle
  $mr = $main.Current.BoundingRectangle
  write-host "* 主視窗與 Result 鈕定位 OK" -ForegroundColor Green
  Save-RegionShot (Join-Path $ShotDir "result-summon-button.png") @($main.Current.BoundingRectangle)  # 手冊證據：功能列右端 Result 鈕可見

  #endregion

  #region B.態3：無卡有歷史→按 Result 以最新一筆重開 --------------------------------
  write-host "## B.態3：無卡有歷史→按 Result 以最新一筆重開 --------------------------------" -ForegroundColor Cyan

  if (Find-AppWindow $appPid $ResultTitle) { write-host "前置異常：起手不應有結果卡" -ForegroundColor Red; exit 1 }
  # 先點主視窗（狀態列）確保前景，再按 Result 鈕；開卡以輪詢等待（至多 6 秒）
  Invoke-ClickAt ([int]($mr.X + 150)) ([int]($mr.Y + $mr.Height - 15)) 1
  Start-Sleep -Milliseconds 500
  Invoke-ClickAt ([int]($br.X + $br.Width/2)) ([int]($br.Y + $br.Height/2)) 1
  $res = $null
  for ($w = 0; $w -lt 12 -and -not $res; $w++) { Start-Sleep -Milliseconds 500; $res = Find-AppWindow $appPid $ResultTitle }
  if ($res) {
    $head = $latest.Original.Substring(0, [Math]::Min(15, $latest.Original.Length))
    $txt = $res.FindFirst([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
    write-host "* PASS：結果卡以歷史最新一筆重開（含文字元素檢出）" -ForegroundColor Green
    $found = $false
    foreach ($t in $res.FindAll([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))) {
      if ($t.Current.Name -like "$head*") { $found = $true; break }
    }
    if ($found) { write-host "* PASS：卡上內容與歷史最新一筆相符（$head…）" -ForegroundColor Green }
    else { write-host "* FAIL：卡上找不到歷史最新一筆之原文開頭（$head…）" -ForegroundColor Red; $fail++ }
  } else { write-host "* FAIL：按 Result 未開出結果卡" -ForegroundColor Red; $fail++ }

  # 卡記憶位置可能覆蓋主視窗功能列（含 Result 鈕）——移卡至主視窗右側不重疊處，
  # 確保後續態1/態2 的按鈕點擊確實落在鈕上（防「點到卡」假通過），截圖亦兩窗並列清晰
  if ($res) {
    $rr = $res.Current.BoundingRectangle
    [T41.Native]::MoveWindow([IntPtr][int64]$res.Current.NativeWindowHandle,
      ([int]($mr.X + $mr.Width + 24)), ([int]$mr.Y), ([int]$rr.Width), ([int]$rr.Height), $true) | Out-Null
    Start-Sleep -Milliseconds 500
    write-host "* 已將結果卡移至主視窗右側（不覆蓋功能列）"
  }

  #endregion

  #region C.態1：卡開啟中→按 Result 帶前景不新開 --------------------------------
  write-host "## C.態1：卡開啟中→按 Result 帶前景不新開 --------------------------------" -ForegroundColor Cyan

  # 先實體點主視窗使其為前景（#105 已保證不關卡）；按鈕改以 UIA InvokePattern 觸發——
  # B 態已以實體點擊證明按鈕接線，C/D 驗「喚回行為」本身，Invoke 觸發可靠（避免 SendInput 偶發落空之假失敗）
  $mr = $main.Current.BoundingRectangle
  Invoke-ClickAt ([int]($mr.X + 150)) ([int]($mr.Y + $mr.Height - 15)) 1
  Start-Sleep -Milliseconds 800
  ($btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Start-Sleep -Milliseconds 1200
  $res = Find-AppWindow $appPid $ResultTitle
  $n = Count-AppWindows $appPid $ResultTitle
  $fg = [T41.Native]::GetForegroundWindow()
  if ($res -and $n -eq 1 -and ([int64]$fg -eq [int64]$res.Current.NativeWindowHandle)) {
    write-host "* PASS：現有卡帶至前景、仍僅一張" -ForegroundColor Green
    Save-RegionShot (Join-Path $ShotDir "result-summon-coexist.png") @($main.Current.BoundingRectangle, $res.Current.BoundingRectangle)
  } else {
    $fgIsCard = if ($res) { [int64]$fg -eq [int64]$res.Current.NativeWindowHandle } else { "(無卡)" }
    write-host "* FAIL：卡數=$n、前景是否結果卡=$fgIsCard" -ForegroundColor Red; $fail++
  }

  #endregion

  #region D.態2：卡最小化中→按 Result 還原帶前景 --------------------------------
  write-host "## D.態2：卡最小化中→按 Result 還原帶前景 --------------------------------" -ForegroundColor Cyan

  $res = Find-AppWindow $appPid $ResultTitle
  [T41.Native]::ShowWindow([IntPtr][int64]$res.Current.NativeWindowHandle, 6) | Out-Null  # SW_MINIMIZE
  Start-Sleep -Milliseconds 800
  # 最小化後前景未必回到主視窗——先實體點主視窗（狀態列）取得前景，再以 InvokePattern 觸發 Result 鈕（同 C 態理由）
  Invoke-ClickAt ([int]($mr.X + 150)) ([int]($mr.Y + $mr.Height - 15)) 1
  Start-Sleep -Milliseconds 500
  ($btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
  Start-Sleep -Milliseconds 1500
  $res = Find-AppWindow $appPid $ResultTitle
  $state = ($res.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Current.WindowVisualState
  $fg = [T41.Native]::GetForegroundWindow()
  if ($state -ne [Windows.Automation.WindowVisualState]::Minimized -and [int64]$fg -eq [int64]$res.Current.NativeWindowHandle) {
    write-host "* PASS：最小化卡已還原並帶前景（不無聲）" -ForegroundColor Green
  } else {
    write-host "* FAIL：state=$state、前景是否結果卡=$([int64]$fg -eq [int64]$res.Current.NativeWindowHandle)" -ForegroundColor Red; $fail++
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

if ($fail -eq 0) { write-host "結果：PASS（intTest#41 態1/態2/態3 全數成立；態4 見 I 節說明）" -ForegroundColor Green; exit 0 }
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
