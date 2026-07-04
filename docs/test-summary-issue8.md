# test-summary — Issue #8：paramQueryTimeoutSec 非正值防呆——套用安全下限

> 本增量為**組態讀取邊界防呆、未動 UI**。依 GATE §5「無畫面→獨立報告」，本報告即該獨立報告（非 A5 逐頁 UI PDF）；鏡頭 C 逐頁 UI/UX 審查 **N/A**（本增量僅 `AppConfig.cs` 讀取路徑，無 XAML／視窗變更）。

## 範圍

* `AppConfig.Load`（`sysScreenTrans/AppConfig.cs`）讀 `paramQueryTimeoutSec` 時，對 `0` 或負值於**組態讀取邊界**套用安全下限 `15`；合法正值原樣保留。
* 根因：非正值一路傳至 `QueryService.SendOnceAsync` 之 `cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec))`（`QueryService.cs:96`），`CancelAfter(0/負)` 即刻取消、每次查詢立即逾時、永遠查不到結果。
* **單點防呆**：驗證集中於組態邊界，`QueryService` 維持信任其建構子輸入、不重複驗證。
* **檢整校正**：原議題引用之對照「`OpenAiSpeechService` 已對 `timeoutSec <= 0` 退回 20 秒」已過期——`OpenAiSpeechService.cs` 於 #9 移除 OpenAI TTS 時刪除；改以現存之 `QueryService` `maxRetries` clamp（`QueryService.cs:38`）為一致性依據。

## 1. 品管測試（元件層，GATE §1）

| 判定 | 指令 | 結果 |
|---|---|---|
| 建置 0 錯 | `dotnet build -c Release`（含於 `dotnet test`） | ✅ 0 錯 |
| 單元測試全過 | `dotnet test` | ✅ **35 passed / 0 failed**（本增量新增 4：非正值 Theory 3＋正值保留 1） |
| 依賴安全 | `dotnet list package --vulnerable` | ✅ 0 漏洞（ScreenTrans／ScreenTrans.Tests） |
| 版號已釘選 | 本地 `VERSION` 相對 `origin/main` 進位 | ✅ `0.4.0 → 0.4.1`（fix→patch）、CHANGELOG 同步 |
| 結構合規 | repoLint／docLint | N/A：本 repo（repoStructVersion 2.0）無 lint 腳本；design.md 依 3.2 骨架局部研改、章節結構未破壞 |
| HMI 結構合規 | uiLint | N/A：無 React 建置單元 |

**新增／異動單元測試（元件層，`sysScreenTrans.Tests/AppConfigTests`）**：

* `Load_NonPositiveTimeout_AppliesSafeFloor`（Theory：`0`／`-1`／`-30`）：非正值 → `TimeoutSec == 15`。
* `Load_PositiveTimeout_KeptAsIs`：合法正值 `45` → 原樣保留、不受防呆影響。
* 既有 `AppConfigTests` 案例（往返、缺欄相容、缺檔／壞 JSON 預設、舊鍵忽略）續全綠、未回歸。

## 2. 使用案例測試（案例層，GATE §2；按本增量範圍縮放）

本增量受影響之驗收單元：`runWi自訂Sys辨識翻譯選區`（intTest#05／#06，屬 orgSop#1-畫面選區查詢）——組態逾時值為其前置條件。

### orgSop/teamSop 驗收矩陣

| 來源 | orgSop/teamSop | 系統類型 | prsnSop/wi | 設計責任鏈 | 實際責任鏈 | 實作入口 | 測試檔案 | 狀態 | 證據 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|---|
| design ＜III.C.(B)＞ 關鍵參數 | teamSop#1.2 | 組態／AI 外部服務整合 | runWi自訂Sys辨識翻譯選區（逾時前置） | AppConfig 讀取邊界淨化 → QueryService `CancelAfter` 恆正逾時 | 同設計，clamp 於 `AppConfig.Load`、`QueryService` 不變 | `AppConfig.Load` | `AppConfigTests` | 已測通過 | 4 項邊界單元測試 exit 0（非正值 → 15、正值保留） | 全系統實機查詢（真實逾時）留發車環對成品驗 |

> 實機查詢逾時行為依 GATE §2「全系統 int/e2e 留發車環對成品做」，於發佈列車對 source-free 成品驗；本層以組態邊界單元測試涵蓋淨化邏輯。

### 企業常規完整性審查

* 防呆置於單一組態邊界（`AppConfig.Load`），下游消費端（`QueryService`／`SettingsWindow`）直接信任，無疊床架屋。
* 缺欄、解析失敗、非正值三路徑統一退回同一安全下限 `15`，行為一致可預期。
* 合法正值零行為改變，衝擊面極小。

### 重大缺口與是否可宣稱完成

* **1/1 已測通過，0 未實作，0 責任邊界不符**。
* 結論：**可宣稱完成**（本增量範圍）。

## 5. 業界水準審查（GATE §5，本增量範圍）

### 鏡頭 A — 系統類型能力盤點（組態參數驗證）

依「組態參數驗證」最低能力（邊界值淨化、預設回退、行為一致、下游不被不當值污染）逐項對照：

| 能力 | 落地 | 判級 |
|---|---|---|
| 邊界值淨化 | 非正值（`0`／負）→ 安全下限 15 | 可以接受 |
| 預設回退 | 缺欄／解析失敗 → 同一下限 15（既有） | 可以接受 |
| 行為一致 | 三路徑同回退值；與程式庫既有 `maxRetries` clamp 同風格 | 可以接受 |
| 下游不被污染 | `QueryService.CancelAfter` 逾時值恆正、逾時機制不失效 | 可以接受 |

### 鏡頭 B — 專家缺口審查

* **上限未設**：目前僅設下限、未設逾時上限（極大值可能長候）→ 列 `後續辦理`（MVP 可接受，使用者可主動 `ESC` 取消）。
* **非整數／溢位**：`GetInt32()` 對非整數 JSON 值會拋例外、走 `catch` 回預設，行為安全 → `可以接受`。
* **一致性依據更新**：已於報告與 design 註明原 `OpenAiSpeechService` 對照過期、改採 `maxRetries` clamp → `可以接受`。

### 鏡頭 C — 逐頁 UI/UX

* **N/A**：本增量無 UI 變更（僅 `AppConfig.cs` 讀取路徑，無新增／異動頁面）。

### 分級彙整

* `務必要修`：0
* `後續辦理`：1（逾時上限，未來可加）
* `可以接受`：其餘

## 結論

GATE §1 機器品管全綠（35 passed、0 漏洞、版號已釘選）、§2 案例矩陣本增量範圍「可宣稱完成」、§5 鏡頭 A/B 無 `務必要修`、鏡頭 C N/A。**本增量可宣稱完成（CODE-READY 候選），交 USR 於 PR 審查合併。**
