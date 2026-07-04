# Changelog

版本依語意化版號（SemVer）。版號於 PR merge 當下釘選。

## [0.4.1] - 2026-07-04

Issue #8 增量：`paramQueryTimeoutSec` 非正值防呆——套用安全下限。

### 修正
- `AppConfig.Load` 讀 `paramQueryTimeoutSec` 時，對 `0` 或負值於組態讀取邊界套用安全
  下限 `15`（原先會使 `QueryService` 以 `CancelAfter(0)` 即刻取消、每次查詢立即逾時、
  永遠查不到結果）；合法正值不受影響。
- 補 `AppConfigTests` 非正值（`0`／`-1`／`-30`）→ 套用下限與正值原樣保留之案例。

### 備註
- 本增量無 UI 變更，GATE §5 依「無畫面→獨立報告」處理。
- 原議題引用之 `OpenAiSpeechService` 對照已於 #9 移除 OpenAI TTS 時刪除，改以現存的
  `QueryService` `maxRetries` clamp 作一致性依據。

## [0.4.0] - 2026-07-04

Issue #7 增量：OpenAI 查詢暫時性錯誤加重試退避。

### 新增
- `QueryService` 對**暫時性**錯誤（逾時、連線中斷、HTTP 429、HTTP 5xx）以有限次數
  **指數退避**自動重試；**永久性**錯誤（401／400／其他 4xx／回應格式解析失敗）不重試、
  立即明確降級；使用者主動取消不視為暫時性錯誤。
- `paramQueryMaxRetries`（appsettings，預設 2；`0`＝不重試）——查詢暫時性錯誤最大重試次數。
- 重試迴圈與退避延遲接縫化的單元測試（狀態碼分類、重試後成功、永久性不重試、
  耗盡後降級、次數 0 不重試、取消不重試）＋`AppConfig` 新欄往返與向後相容測試。

### 備註
- 範圍收斂：原議題含 `OpenAiSpeechService` 重試，因 v0.2.0（#9）已移除 OpenAI TTS、
  改離線 Windows 語音（無網路），該半不適用；本增量僅及 `QueryService`。
- 本增量無 UI 變更，GATE §5 依「無畫面→獨立報告」產 `docs/test-summary-issue7.md`。

## [0.3.0] - 2026-07-04

Issue #6 增量：單一實例守衛——重複啟動時提示、不再起第二實例。

### 新增
- 啟動時以使用者範圍具名 `Mutex`（`SingleInstanceGuard`）偵測既有實例；已在執行時
  提示「ScreenTrans 已在執行中」並結束新實例，不再重複註冊熱鍵而跳「Alt+L 註冊失敗」。
- `SingleInstanceGuard` 單元測試（首取得為第一實例、既有實例仍持有時判為第二、
  釋放後可再取得、重複釋放不拋例外）。

### 備註
- 落實 `docs/design.md` ＜setWi自訂Usr啟動結束常駐＞驗收 row02「重複啟動 → 單一實例提示」
  與＜II.C 模組層＞「單一實例 invariant」既有規格（規格-實作落差補齊，design 無變更）。

## [0.2.0] - 2026-07-04

Issue #9 增量：發音改用 Windows 內建語音，移除 OpenAI TTS。

### 變更
- 朗讀改用 Windows 內建語音（SAPI，離線、免額度、免金鑰），對齊 [techItem語音合成] 契約。
- 系統匣「設定…」語音區改為「朗讀語音」單一下拉，列舉系統已安裝 Windows 語音
  （`GetInstalledVoices`）供選擇；空清單時提示安裝語音包。

### 移除
- OpenAI 語音合成路徑（`OpenAiSpeechService`）與其組態 `paramTtsProvider`／`paramTtsModel`。

### 備註
- OpenAI 金鑰／額度自此僅用於畫面辨識翻譯查詢，發音不再耗用。
- 沿用互動式手動驗證（本 repo 尚無自動化測試骨架，同 v0.1.0）。

## [0.1.0] - 2026-07-03

首個增量（Issue #1 MVP）：遊戲畫面選區英文發音與中譯即時查詢工具。

### 新增
- 系統匣常駐，全域熱鍵 `Alt+L`（左右 Alt 皆可）喚起，不干擾遊戲輸入。
- 全螢幕變暗遮罩 ＋ 拖曳框選 ＋ 實際像素截圖（多螢幕／DPI 對位）。
- OpenAI vision 單次查詢，回傳英文原文／KK 音標／繁體中文翻譯。
- 結果視窗：淺粉底大字、兩位女孩浮水印底圖、中英各自播放與自動播放、
  可拖曳移動與縮放、關閉後記住位置與大小、右上角關閉鈕。
- 語音：預設 OpenAI 語音音檔（自然、中英同端點），Windows SAPI 離線後備。
- 系統匣「設定…」：API 金鑰（寫入使用者環境變數、不落地）、語音來源／模型／
  嗓音、查詢模型，附「測試發音」。
- 兩位女孩應用圖示（系統匣 ＋ exe 檔案圖示）。

### 已知後續（未納入本增量）
- 自動化單元測試與 `docs/test-summary.pdf` 尚未產出（本增量以互動式手動驗證）。
- 免安裝／安裝檔兩種發佈打包（屬發佈列車 train-2rel）。
