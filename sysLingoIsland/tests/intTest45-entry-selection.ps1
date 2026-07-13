#region I.主旨目的 ================================
write-host "# I.主旨目的 ================================" -ForegroundColor Blue

write-host "* intTest#45（Issue #110）：筆記/歷史條目單擊選取回饋——選中卡框轉深粉 #B0578D、單選移轉、"
write-host "  右鍵亦選取、既有互動（雙擊檢視）迴歸；字級加大由截圖視覺驗。"
write-host "* 機判工法：PrintWindow 位圖上以條目所在掃描帶搜尋 #B0578D 像素（Border 無 UIA peer、以像素驗視覺態）。"
write-host "* 唯讀動線（選取不落地）；仍強制自啟實例以固定基線。"
write-host "* 日期版本：2026-07-10 v1"

#endregion

#region II.參考準備 ================================
write-host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  write-host "## A.參數準備 --------------------------------" -ForegroundColor Cyan

  $RepoRoot = if ($PSScriptRoot) { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent } else { (Get-Location).Path }
  $ExePath  = Join-Path $RepoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe"
  $ShotDir  = Join-Path $RepoRoot "docs\manual-assets"
  if (-not (Test-Path $ExePath)) { write-host "缺少建置產物（先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }

  $notesPath = Join-Path $env:APPDATA "LingoIsland\notes.json"
  $notes = Get-Content $notesPath -Raw | ConvertFrom-Json
  $folder = $notes.Folders | Where-Object { $_.Entries.Count -ge 2 } | Select-Object -First 1
  if (-not $folder) { write-host "前置不足：無一夾含 >=2 條筆記" -ForegroundColor Red; exit 1 }
  $e1 = $folder.Entries[0]; $e2 = $folder.Entries[1]

  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
  Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
'@ -Name Native -Namespace T45
  [T45.Native]::SetProcessDPIAware() | Out-Null

  #endregion

  #region B.函數準備 --------------------------------
  write-host "## B.函數準備 --------------------------------" -ForegroundColor Cyan

  function Invoke-ClickAt([int]$X, [int]$Y, [string]$Btn = "L") {
    [T45.Native]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 150
    if ($Btn -eq "R") { [T45.Native]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero); [T45.Native]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero) }
    else { [T45.Native]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [T45.Native]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero) }
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
    [T45.Native]::PrintWindow([IntPtr][int64]$Win.Current.NativeWindowHandle, $hdc, 0x2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
  }

  # 掃描帶（元素垂直範圍±pad）內是否出現選取色 #B0578D（容差 ±10/通道）
  function Band-HasSelColor([System.Drawing.Bitmap]$Bmp, [Windows.Automation.AutomationElement]$Win, [Windows.Automation.AutomationElement]$Elem, [int]$Pad = 14) {
    $wr = $Win.Current.BoundingRectangle; $er = $Elem.Current.BoundingRectangle
    $y0 = [Math]::Max(0, [int]($er.Y - $wr.Y) - $Pad); $y1 = [Math]::Min($Bmp.Height - 1, [int]($er.Y - $wr.Y + $er.Height) + $Pad)
    for ($y = $y0; $y -le $y1; $y += 2) {
      for ($x = 0; $x -lt $Bmp.Width; $x += 2) {
        $c = $Bmp.GetPixel($x, $y)
        if ([Math]::Abs($c.R - 176) -le 10 -and [Math]::Abs($c.G - 87) -le 10 -and [Math]::Abs($c.B - 141) -le 10) { return $true }
      }
    }
    return $false
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

  if (Get-Process LingoIsland -ErrorAction SilentlyContinue) {
    write-host "偵測到執行中之 LingoIsland——請先結束後重跑" -ForegroundColor Red; exit 1
  }
  $proc = Start-Process $ExePath -PassThru; $startedByTest = $true; $appPid = $proc.Id
  Start-Sleep 4
  $main = $null
  for ($try = 0; $try -lt 5; $try++) {
    [T45.Native]::ShowWindow((Get-Process -Id $appPid).MainWindowHandle, 9) | Out-Null
    [T45.Native]::SetForegroundWindow((Get-Process -Id $appPid).MainWindowHandle) | Out-Null
    Start-Sleep 1
    $main = Find-AppWindow $appPid "LingoIsland"
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
  if (-not $t1 -or -not $t2) { write-host "找不到兩條卡片文字" -ForegroundColor Red; exit 1 }
  write-host "* 定位 OK" -ForegroundColor Green

  #endregion

  #region B.基線：未選取時無深粉框 --------------------------------
  write-host "## B.基線：未選取時無深粉框 --------------------------------" -ForegroundColor Cyan

  $bmp = Get-WindowBitmap $main
  $pre1 = Band-HasSelColor $bmp $main $t1; $pre2 = Band-HasSelColor $bmp $main $t2
  $bmp.Dispose()
  if (-not $pre1 -and -not $pre2) { write-host "* PASS：基線兩卡皆無選取色" -ForegroundColor Green }
  else { write-host "* FAIL：基線即出現選取色（卡1=$pre1 卡2=$pre2）" -ForegroundColor Red; $fail++ }

  #endregion

  #region C.單擊選取→卡1 深粉框；點卡2→移轉 --------------------------------
  write-host "## C.單擊選取→卡1 深粉框；點卡2→移轉 --------------------------------" -ForegroundColor Cyan

  # 實體點擊在使用者並用滑鼠時偶發落空——各斷言以「點擊→驗末態」重試至多 3 次（末態驗證、重試安全）
  $r1 = $t1.Current.BoundingRectangle
  $ok = $false
  for ($k = 0; $k -lt 3 -and -not $ok; $k++) {
    Invoke-ClickAt ([int]($r1.X + $r1.Width/2)) ([int]($r1.Y + $r1.Height/2))
    Start-Sleep -Milliseconds 600
    $bmp = Get-WindowBitmap $main
    $ok = (Band-HasSelColor $bmp $main $t1) -and -not (Band-HasSelColor $bmp $main $t2)
    $bmp.Dispose()
    if (-not $ok) { write-host "* 點卡1 未達預期態（第 $($k+1) 次），重試…" -ForegroundColor Yellow }
  }
  if ($ok) {
    write-host "* PASS：點卡1 → 卡1 深粉框、卡2 無" -ForegroundColor Green
    [T45.Native]::SetCursorPos(5,5) | Out-Null; Start-Sleep -Milliseconds 300
    Save-WindowShot (Join-Path $ShotDir "notes-entry-selected.png") $main
  } else { write-host "* FAIL：點卡1 未見單選態" -ForegroundColor Red; $fail++ }

  $r2 = $t2.Current.BoundingRectangle
  $ok = $false
  for ($k = 0; $k -lt 3 -and -not $ok; $k++) {
    Invoke-ClickAt ([int]($r2.X + $r2.Width/2)) ([int]($r2.Y + $r2.Height/2))
    Start-Sleep -Milliseconds 600
    $bmp = Get-WindowBitmap $main
    $ok = (-not (Band-HasSelColor $bmp $main $t1)) -and (Band-HasSelColor $bmp $main $t2)
    $bmp.Dispose()
    if (-not $ok) { write-host "* 點卡2 未達預期態（第 $($k+1) 次），重試…" -ForegroundColor Yellow }
  }
  if ($ok) { write-host "* PASS：點卡2 → 選取移轉（單選）" -ForegroundColor Green }
  else { write-host "* FAIL：點卡2 未見移轉" -ForegroundColor Red; $fail++ }

  #endregion

  #region D.右鍵亦選取＋雙擊檢視迴歸 --------------------------------
  write-host "## D.右鍵亦選取＋雙擊檢視迴歸 --------------------------------" -ForegroundColor Cyan

  $sel1 = $false
  for ($k = 0; $k -lt 3 -and -not $sel1; $k++) {   # 實體右鍵偶發落空（並用滑鼠干擾）→ 重試至多 3 次
    Invoke-ClickAt ([int]($r1.X + $r1.Width/2)) ([int]($r1.Y + $r1.Height/2)) "R"
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")  # 關選單再驗框色
    Start-Sleep -Milliseconds 500
    $bmp = Get-WindowBitmap $main
    $sel1 = Band-HasSelColor $bmp $main $t1
    $bmp.Dispose()
    if (-not $sel1) { write-host "* 右鍵後未見選取（第 $($k+1) 次），重試…" -ForegroundColor Yellow }
  }
  if ($sel1) { write-host "* PASS：右鍵卡1 → 選取移至卡1" -ForegroundColor Green }
  else { write-host "* FAIL：右鍵未設選取" -ForegroundColor Red; $fail++ }

  $res = $null
  for ($k = 0; $k -lt 3 -and -not $res; $k++) {
    Invoke-ClickAt ([int]($r1.X + $r1.Width/2)) ([int]($r1.Y + $r1.Height/2)); Start-Sleep -Milliseconds 80
    Invoke-ClickAt ([int]($r1.X + $r1.Width/2)) ([int]($r1.Y + $r1.Height/2))
    Start-Sleep 2
    $res = Find-AppWindow $appPid "Query Result"
    if (-not $res) { write-host "* 雙擊未開卡（第 $($k+1) 次），重試…" -ForegroundColor Yellow }
  }
  if ($res) {
    write-host "* PASS：雙擊仍開檢視（迴歸）" -ForegroundColor Green
    ($res.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close(); Start-Sleep -Milliseconds 400
  } else { write-host "* FAIL：雙擊未開檢視" -ForegroundColor Red; $fail++ }

  #endregion

  #region E.歷史分頁同款選取 --------------------------------
  write-host "## E.歷史分頁同款選取 --------------------------------" -ForegroundColor Cyan

  $histTab = $main.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, "History")))
  $hr = $histTab.Current.BoundingRectangle
  Invoke-ClickAt ([int]($hr.X + $hr.Width/2)) ([int]($hr.Y + $hr.Height/2))
  Start-Sleep -Milliseconds 1200
  $hist = Get-Content (Join-Path $env:APPDATA "LingoIsland\history.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
  if ($hist -and $hist.Count -ge 1) {
    $hh = $hist[0].Original.Substring(0, [Math]::Min(18, $hist[0].Original.Length))
    $ht = Find-TextLike $main $hh
    if ($ht) {
      $hrr = $ht.Current.BoundingRectangle
      $selH = $false
      for ($k = 0; $k -lt 3 -and -not $selH; $k++) {
        Invoke-ClickAt ([int]($hrr.X + $hrr.Width/2)) ([int]($hrr.Y + $hrr.Height/2))
        Start-Sleep -Milliseconds 600
        $bmp = Get-WindowBitmap $main
        $selH = Band-HasSelColor $bmp $main $ht 6  # 歷史卡距小、掃描帶收斂免越入鄰卡（§5 審查 #5）
        $bmp.Dispose()
        if (-not $selH) { write-host "* 歷史點擊未達預期態（第 $($k+1) 次），重試…" -ForegroundColor Yellow }
      }
      if ($selH) { write-host "* PASS：歷史條目單擊選取（深粉框）" -ForegroundColor Green }
      else { write-host "* FAIL：歷史條目未見選取色" -ForegroundColor Red; $fail++ }
    } else { write-host "* SKIP：歷史最新條目不在當前日清單頂（定位不到）" -ForegroundColor Yellow; $script:eSkipped = $true }
  } else { write-host "* SKIP：無查詢歷史（前置不足）" -ForegroundColor Yellow; $script:eSkipped = $true }

  #endregion

} finally {

  #region F.清理 --------------------------------
  write-host "## F.清理 --------------------------------" -ForegroundColor Cyan

  if ($startedByTest -and $appPid) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    write-host "* 已結束受測程式（選取不落地、無資料還原需求）"
  }

  #endregion
}

#endregion

#region IV.備註紀錄 ================================
write-host "# IV.備註紀錄 ================================" -ForegroundColor Blue

if ($fail -eq 0) {
  $note = if ($script:eSkipped) { "（E 歷史段前置不足 SKIP、未驗）" } else { "" }
  write-host "結果：PASS（intTest#45 選取回饋/移轉/右鍵/迴歸 成立$note）" -ForegroundColor Green; exit 0
}
else { write-host "結果：FAIL（$fail 項斷言未過）" -ForegroundColor Red; exit 1 }

#endregion
