#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#44（Issue #109）：底色盤擴為十色之流佈——"
write-host "  (1) 筆記右鍵選單 11 色項（No Color＋十色）＋分隔線＋Delete 居末；點新色 Violet 落地 #EEDBFF；"
write-host "  (2) 結果卡（檢視開啟）底部色塊列 11 塊截圖存證（視覺驗全可視）；(3) 情境分頁配色規則十色各一格。"
write-host "* 資料保護：套色會改 notes.json——備份還原、強制自啟實例。"
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
  if (-not (Test-Path $notesPath)) { write-host "前置不足：找不到 $notesPath" -ForegroundColor Red; exit 1 }
  $backupPath = "$notesPath.intTest44.bak"
  Copy-Item $notesPath $backupPath -Force
  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 1 } | Select-Object -First 1
  if (-not $folder) { Remove-Item $backupPath; write-host "前置不足：無筆記" -ForegroundColor Red; exit 1 }
  $entry = $folder.Entries[0]
  $TenNames = @("Pink","Blue","Green","Yellow","Gray","Violet","Sky","Mint","Lime","Orange")

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T44
  [T44.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y, [string]$Btn = "L") {
    [T44.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    if ($Btn -eq "R") { [T44.Native]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero); [T44.Native]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero) }
    else { [T44.Native]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [T44.Native]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero) }
  }

  function Invoke-DoubleClickAt([int]$X, [int]$Y) {
    Invoke-ClickAt $X $Y; Start-Sleep -Milliseconds 80; Invoke-ClickAt $X $Y
  }

  function Find-AppWindow([int]$AppPid, [string]$Title) {
    $cond = New-Object Windows.Automation.AndCondition(
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid)),
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Title)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
  }

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

  function Save-WindowShot([string]$Path, [Windows.Automation.AutomationElement]$Win) {
    $r = $Win.Current.BoundingRectangle
    $w = [int]$r.Width; $h = [int]$r.Height
    $raw = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($raw); $hdc = $g.GetHdc()
    [T44.Native]::PrintWindow([IntPtr][int64]$Win.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    $cw = $w - 16; $ch = $h - 9
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
    [T44.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T44.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
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
  $head = $entry.Original.Substring(0, [Math]::Min(20, $entry.Original.Length))
  $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
  $target = $null
  foreach ($t in $texts) { if ($t.Current.Name -like "$head*") { $target = $t; break } }
  if (-not $target) { write-host "找不到首條卡片" -ForegroundColor Red; exit 1 }
  write-host "* 定位 OK" -ForegroundColor Green

  #endregion

  #region B.右鍵選單 11 色項＋Delete 居末、點 Violet 落地 --------------------------------
  write-host "## B.右鍵選單 11 色項＋Delete 居末、點 Violet 落地 --------------------------------" -ForegroundColor Cyan

  $tr = $target.Current.BoundingRectangle
  Invoke-ClickAt ([int]($tr.X + $tr.Width/2)) ([int]($tr.Y + $tr.Height/2)) "R"
  Start-Sleep -Milliseconds 1000
  $menu = Get-OpenMenuItems $appPid
  if (-not $menu) { write-host "* FAIL：未開出選單" -ForegroundColor Red; $fail++ }
  else {
    $real = @($menu.Items | ForEach-Object { $_.Current.Name } | Where-Object { $_ -ne "System" })
    write-host "* 選單項：$($real -join ' | ')"
    $missing = @(($TenNames + @("No Color")) | Where-Object { $n = $_; -not ($real | Where-Object { $_ -like "$n*" }) })
    $ok = ($real.Count -eq 12) -and ($real[-1] -eq "Delete") -and ($missing.Count -eq 0)
    if ($ok) {
      write-host "* PASS：11 色項（No Color＋十色）＋Delete 居末（共 12 可點項）" -ForegroundColor Green
      $mw = $menu.Window.Current.BoundingRectangle
      # 合成截圖：主視窗 PrintWindow＋選單 CopyFromScreen（畫布嚴格＝主視窗，工法同 intTest43）
      $mr = $main.Current.BoundingRectangle
      $w=[int]$mr.Width; $h=[int]$mr.Height
      $raw = New-Object System.Drawing.Bitmap($w,$h)
      $g=[System.Drawing.Graphics]::FromImage($raw); $hdc=$g.GetHdc()
      [T44.Native]::PrintWindow([IntPtr][int64]$main.Current.NativeWindowHandle,$hdc,0x2)|Out-Null
      $g.ReleaseHdc($hdc); $g.Dispose()
      $cw=$w-16; $ch=$h-9
      $bmp=New-Object System.Drawing.Bitmap($cw,$ch)
      $g2=[System.Drawing.Graphics]::FromImage($bmp)
      $g2.DrawImage($raw,(New-Object System.Drawing.Rectangle(0,0,$cw,$ch)),(New-Object System.Drawing.Rectangle(8,1,$cw,$ch)),[System.Drawing.GraphicsUnit]::Pixel)
      $raw.Dispose()
      $g2.SetClip((New-Object System.Drawing.Rectangle(0,0,$cw,$ch)))
      $g2.CopyFromScreen([int]$mw.X,[int]$mw.Y,[int]($mw.X-$mr.X-8),[int]($mw.Y-$mr.Y-1),(New-Object System.Drawing.Size([Math]::Min([int]$mw.Width,$cw),[Math]::Min([int]$mw.Height,$ch))))
      $g2.Dispose()
      $shot = Join-Path $ShotDir "notes-menu-ten-colors.png"
      $bmp.Save($shot,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
      write-host "* 截圖：$shot"
    } else {
      write-host "* FAIL：項數=$($real.Count)、末項=$($real[-1])、缺=$($missing -join ',')" -ForegroundColor Red; $fail++
    }
    $violet = $menu.Items | Where-Object { $_.Current.Name -like "Violet*" } | Select-Object -First 1
    if ($violet) {
      ($violet.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
      Start-Sleep -Milliseconds 1000
      $n2 = Get-Content $notesPath -Raw | ConvertFrom-Json
      $c = (($n2.Folders | Where-Object Name -eq $folder.Name).Entries | Where-Object Id -eq $entry.Id).Color
      if ($c -eq "#EEDBFF") { write-host "* PASS：點 Violet → Color=#EEDBFF 落地" -ForegroundColor Green }
      else { write-host "* FAIL：Color=$c（預期 #EEDBFF）" -ForegroundColor Red; $fail++ }
    } else { write-host "* FAIL：無 Violet 項" -ForegroundColor Red; $fail++ }
  }

  #endregion

  #region C.結果卡色塊列 11 塊（檢視開卡截圖存證） --------------------------------
  write-host "## C.結果卡色塊列 11 塊（檢視開卡截圖存證） --------------------------------" -ForegroundColor Cyan

  $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
  $target = $null
  foreach ($t in $texts) { if ($t.Current.Name -like "$head*") { $target = $t; break } }
  $tr = $target.Current.BoundingRectangle
  Invoke-DoubleClickAt ([int]($tr.X + $tr.Width/2)) ([int]($tr.Y + $tr.Height/2))
  Start-Sleep 2
  $res = Find-AppWindow $appPid "Query Result"
  if ($res) {
    write-host "* PASS：檢視開卡" -ForegroundColor Green
    Save-WindowShot (Join-Path $ShotDir "result-swatches-ten.png") $res
    # 窄窗換行證據（§5 審查必修）：縮至 MinWidth=320 → 色塊列應換行、11 塊全可視（DockPanel+WrapPanel）
    Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int X, int Y, int W, int H, bool r);' -Name N2 -Namespace T44b
    $rr = $res.Current.BoundingRectangle
    [T44b.N2]::MoveWindow([IntPtr][int64]$res.Current.NativeWindowHandle, [int]$rr.X, [int]$rr.Y, 320, [int]$rr.Height, $true) | Out-Null
    Start-Sleep -Milliseconds 800
    Save-WindowShot (Join-Path $ShotDir "result-swatches-narrow.png") $res
    write-host "* 窄窗（320px）截圖存證——換行全可視由截圖視覺驗（色塊 Border 無 UIA peer、無法機判）"
    ($res.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
    Start-Sleep -Milliseconds 400
  } else { write-host "* FAIL：檢視未開卡（色塊列證據缺）" -ForegroundColor Red; $fail++ }

  #endregion

  #region D.情境分頁配色規則十色各一格 --------------------------------
  write-host "## D.情境分頁配色規則十色各一格 --------------------------------" -ForegroundColor Cyan

  $ctxTab = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, "Context")))
  if ($ctxTab) {
    $ctr = $ctxTab.Current.BoundingRectangle
    Invoke-ClickAt ([int]($ctr.X + $ctr.Width/2)) ([int]($ctr.Y + $ctr.Height/2))
    Start-Sleep -Milliseconds 1200
    $texts = $main.FindAll([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
    $names = @($texts | ForEach-Object { $_.Current.Name })
    $missing = @($TenNames | Where-Object { $n = $_; -not ($names -contains $n) })
    if ($missing.Count -eq 0) { write-host "* PASS：情境頁配色規則十色標籤齊備" -ForegroundColor Green }
    else {
      $hasCtx = ((Get-Content (Join-Path $env:APPDATA "ScreenTrans\contexts.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).Items.Count -gt 0)
      if ($hasCtx) { write-host "* FAIL：缺色格標籤：$($missing -join ',')" -ForegroundColor Red; $fail++ }
      else { write-host "* SKIP：無情境項、規則格不渲染（前置不足，本節未驗——結果不得宣稱含 D 節）" -ForegroundColor Yellow; $script:dSkipped = $true }
    }
  } else { write-host "* FAIL：找不到 Context 分頁鈕" -ForegroundColor Red; $fail++ }

  #endregion

} finally {

  #region E.清理與資料還原 --------------------------------
  write-host "## E.清理與資料還原 --------------------------------" -ForegroundColor Cyan

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

if ($fail -eq 0) {
  $note = if ($script:dSkipped) { "（D 節因無情境前置 SKIP、未驗）" } else { "" }
  write-host "結果：PASS（intTest#44 十色流佈成立$note）" -ForegroundColor Green; exit 0
}
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
