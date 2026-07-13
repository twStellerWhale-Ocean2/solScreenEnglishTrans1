---
name: techStackDotnetWin
date: 2026-07-03
description: 【候選契約·待家規裁決】技術選型·平台層(techStack) Profile —— Windows 桌面類：.NET 8 + WPF；產物為 self-contained 單一 exe，手動放置部署。techStack 為封閉家規枚舉（現行 4 選一），本檔為新增選型之候選草案，經 USR 核准後方入契約庫正本並通知全 repo 群。
---

# I. 主旨目的

定義「Windows 原生桌面應用」建置單元的平台選型 Profile：.NET 8 + WPF，封裝資料夾慣例、建置／測試／部署指令、產物型態與部署方法。適用於系統匣常駐工具、桌面 GUI 應用等**不以瀏覽器為宿主、需 Windows 原生 API（全域熱鍵、螢幕擷取、系統匣）**之建置單元——此類需求為現行四選一（StaticWeb／ReactWeb／NodeSys／PythonSys）皆無法承載者。

# II. 參考準備

* **平台組成**：.NET 8（LTS）＋ WPF（XAML UI）＋ Win32 P/Invoke（`RegisterHotKey`、螢幕擷取等原生能力）。
* **選用界線**：僅當建置單元必須為 Windows 原生桌面程式時採用；凡可用 web 承載者仍優先走既有四選一。
* **開發依賴**：.NET 8 SDK（`dotnet` CLI）；不需 Visual Studio。

# III. 內容程序

## A. 資料夾慣例

* 建置單元資料夾＝`sysXXX/`，內含 `modXXX/` 子資料夾對應模組（單一 csproj、以資料夾＋namespace 分模組）；測試專案 `sysXXX.Tests/`。
* 進入點 `App.xaml`／`App.xaml.cs`；組態 `appsettings.json`（不含機密；機密一律環境變數）。

## B. 建置／測試／部署指令

* **建置指令**：`dotnet build -c Release`；發佈 `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`。
* **測試指令**：`dotnet test`（xUnit）。
* **產物型態**：self-contained 單一 `exe`（免安裝 runtime）。
* **部署方法**：手動放置（複製 exe 至任意資料夾執行）；選配隨開機啟動（使用者自行加入「啟動」資料夾或工作排程器）；不走 Docker／Helm。

## C. 品質基線

* UI 執行緒不得阻塞（外部 I/O 一律 async）；全域熱鍵採 `RegisterHotKey`，禁低階鍵盤 hook（除非 design 明確要求並經 USR 核准）。
* 機密（API 金鑰等）一律取自環境變數，程式檔與 repo 不落地。

# IV. 備註紀錄

* 2026-07-03：候選草案建立（solLingoIsland 增量 #1）。techStack 為封閉家規枚舉，本檔尚未入契約庫正本；待 USR 於增量 #1 Draft PR 裁決核准後，由 USR 或後續作業將本檔轉入 [2tech-incrSet-shared/契約庫] 並通知全 repo 群。
