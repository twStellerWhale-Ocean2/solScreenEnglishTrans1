# test-summary — Issue #7：OpenAI 查詢暫時性錯誤加重試退避

> 本增量為**後端邏輯變更、未動 UI**。依 GATE §5「無畫面→獨立報告」，本報告即該獨立報告（非 A5 逐頁 UI PDF）；鏡頭 C 逐頁 UI/UX 審查 **N/A**（本增量僅 `App.xaml.cs` 1 行接線，無 XAML／視窗變更）。gateVersion 1.4。

## 範圍

* 對 `QueryService.QueryAsync`（`sysLingoIsland/modQuery/QueryService.cs`）之**暫時性**錯誤（逾時、連線中斷、HTTP 429、HTTP 5xx）加有限次數**指數退避**重試；**永久性**錯誤（401／400／其他 4xx／回應格式解析失敗）不重試、立即降級。
* 重試次數以 `paramQueryMaxRetries`（appsettings，預設 2）可調。
* **範圍收斂**：原議題之 `OpenAiSpeechService.FetchWavAsync` 已於 #15 移除（改 Windows 內建語音、離線 SAPI、無網路），不在本增量。

## 1. 品管測試（元件層，GATE §1）

| 判定 | 指令 | 結果 |
|---|---|---|
| 建置 0 錯 | `dotnet build -c Release`（含於 `dotnet test`） | ✅ 0 錯 |
| 單元測試全過 | `dotnet test` | ✅ **31 passed / 0 failed**（本增量新增 9：重試迴圈 6＋AppConfig 3） |
| 依賴安全 | `dotnet list package --vulnerable` | ✅ 0 漏洞（LingoIsland／LingoIsland.Tests） |
| 結構合規 | repoLint／docLint | N/A：本 repo（repoStructVersion 2.0）無 lint 腳本；design.md 依 3.2 骨架局部研改、章節結構未破壞 |
| HMI 結構合規 | uiLint | N/A：無 React 建置單元 |

**新增／異動單元測試（元件層，`sysLingoIsland.Tests`）**：

* `QueryServiceRetryTests`：
  * `IsTransientStatus_ClassifiesRetryable`（Theory）：429／500／503→可重試；400／401／404／200→不可重試。
  * `Transient_FailsThenSucceeds_WithinLimit_Returns`：暫時性連 2 次失敗後成功 → 回傳；共 3 次嘗試、退避索引 {0,1}。
  * `Permanent_Propagates_Immediately_NoRetry`：永久性 → 立即上拋、僅 1 次嘗試、無退避。
  * `Transient_Exhausted_ThrowsQueryException_AfterMaxRetries`：暫時性耗盡 → `QueryException`（訊息含「已重試 2 次」）、3 次嘗試、退避 {0,1}。
  * `MaxRetriesZero_Transient_FailsImmediately_NoBackoff`：次數 0 → 立即失敗、無退避。
  * `UserCancellation_Propagates_NotRetried`：使用者取消（`OperationCanceledException`）→ 上拷、不重試。
* `AppConfigTests`：`SaveLoad_Roundtrips_AllFields`（含 `MaxRetries`）、`Load_MissingMaxRetries_DefaultsToTwo`（向後相容）、`Save_Writes_MaxRetriesKey`。

## 2. 使用案例測試（案例層，GATE §2；按本增量範圍縮放）

本增量受影響之驗收單元：`runWi自訂Sys辨識翻譯選區`（intTest#06，屬 orgSop#1-畫面選區查詢）。

### orgSop/teamSop 驗收矩陣

| 來源 | orgSop/teamSop | 系統類型 | prsnSop/wi | 設計責任鏈 | 實際責任鏈 | 實作入口 | 測試檔案 | 狀態 | 證據 | 缺口 |
|---|---|---|---|---|---|---|---|---|---|---|
| design ＜III.D＞ intTest#06 | teamSop#1.2 | AI/外部服務整合 | runWi自訂Sys辨識翻譯選區（降級/重試） | QueryService→OpenAI（HTTPS）；暫時性退避重試、永久性降級 | 同設計，未以 mock 替代正式路徑（mock 僅用於單元測試注入 attempt/backoff） | `QueryService.QueryAsync`／`RunWithRetryAsync`／`SendOnceAsync` | `QueryServiceRetryTests`／`QueryServiceParseTests` | 已測通過 | 6 項重試單元測試 exit 0；分類、退避次數、耗盡降級皆驗 | 全系統實機 intTest#06（斷網/429 實打）留發車環對成品做 |

> intTest#06 之**實機**執行（真實 Windows、真斷網/逾時）依 GATE §2「全系統 int/e2e 留發車環對成品做」，於發佈列車對 source-free 成品驗；本層以元件層單元測試涵蓋分類與重試控制邏輯。

### 企業常規完整性審查

* 重試僅施於冪等之單次查詢 POST（讀取語意、無副作用），重試安全。
* 使用者主動取消與暫時性逾時分流，避免取消被誤重試。
* 退避有次數與時間上限（預設 2 次、約 1s＋2s），最壞等待有界，符合議題「不因重試拖長等待」。

### 重大缺口與是否可宣稱完成

* **1/1 已測通過，0 未實作，0 責任邊界不符**。
* 結論：**可宣稱完成**（本增量範圍）。

## 5. 業界水準審查（GATE §5，本增量範圍）

### 鏡頭 A — 系統類型能力盤點（AI/外部服務/跨系統整合）

依「AI/外部服務整合」最低能力（動態輸入、失敗、**重試**、**降級**、邊界資料驗證使用者可見結果）逐項對照：

| 能力 | 落地 | 判級 |
|---|---|---|
| 失敗處理 | 逾時/連線/HTTP 錯誤皆轉可讀 `QueryException`、present 層顯示 | 可以接受 |
| 重試 | 暫時性（逾時/429/5xx/連線中斷）指數退避重試（本增量新增） | 可以接受 |
| 降級 | 永久性（401/400/4xx/解析失敗）立即明確降級、不重試 | 可以接受 |
| 邊界資料 | 空選區三欄空字串、缺欄/非 JSON 走降級（既有 `Parse` 契約） | 可以接受 |
| 使用者可見結果 | 重試耗盡訊息含次數；永久性錯誤含狀態碼 | 可以接受 |

### 鏡頭 B — 專家缺口審查

* **金鑰不落地**：重試不改變金鑰讀取路徑（僅環境變數），錯誤訊息以 `Truncate` 截斷回應（120/200 字）避免外洩過量內容。`務必要修` 無。
* **429 Retry-After 未解析**：目前採固定指數退避、未讀 `Retry-After` 標頭 → 列 `後續辦理`（MVP 可接受，退避已有界）。
* **重試放大負載**：僅 2 次、指數退避，且僅暫時性錯誤觸發，對 OpenAI 端壓力有限 → `可以接受`。

### 鏡頭 C — 逐頁 UI/UX

* **N/A**：本增量無 UI 變更（僅 `App.xaml.cs` 1 行接線，無新增/異動頁面）。

### 分級彙整

* `務必要修`：0
* `後續辦理`：1（429 `Retry-After` 標頭解析，未來可加）
* `可以接受`：其餘

## 結論

GATE §1 機器品管全綠、§2 案例矩陣本增量範圍「可宣稱完成」、§5 鏡頭 A/B 無 `務必要修`、鏡頭 C N/A。**本增量可宣稱完成（CODE-READY 候選），交 USR 於 PR 審查合併。**
