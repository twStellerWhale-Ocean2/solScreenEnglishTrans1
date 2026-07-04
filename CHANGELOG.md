# Changelog

版本依語意化版號（SemVer）。版號於 PR merge 當下釘選。

## [0.12.0] - 2026-07-05

Issue #36 增量：應用情境改為可管理的命名清單，支援貼圖／上傳畫面由 vision 自動解釋，查詢擇一使用（spec#9）。

### 新增
- **情境分頁**（統一主視窗第 5 分頁 💬）：**命名情境 CRUD** 清單（名稱＋縮圖＋使用中標記）；每則可輸入
  描述文字，或**貼上剪貼簿圖片／上傳畫面檔**，按「🔎 以圖片自動解釋」由 OpenAI vision 產生情境描述、
  再手動補充；「設為使用中」擇一。
- `modQuery/ContextStore`（`contexts.json` 命名情境＋圖片存 `contexts\`；CRUD／單一使用中／`ActiveText`
  注入來源；純函式抽可測、失敗降級、金鑰不入情境）。
- `QueryService.DescribeImageAsync`（單次 vision 回純文字情境描述；僅於為情境加入圖片時呼叫，查詢仍只
  注入文字、成本/延遲不變）。

### 變更
- 查詢注入來源由 #14 單一 `paramContextHint` 改為**使用中情境之描述文字**；無使用中＝維持預設翻譯（回歸保護）。
- 舊 `paramContextHint` **相容遷移**：首次啟動若有值且情境清單為空 → 建一則「預設情境」設為使用中。

### 備註
- design.md：spec#9、orgSop#5→teamSop#5.1/#5.2→prsnSop#5.1.1/#5.2.1→情境分頁、契約（ContextStore／圖片解釋／
  ContextPage）、e2eTest#05／docProgTest#05／intTest#22-23；新增 `page-情境分頁.png`。README 同步。
- 測試 +16（ContextStore CRUD/使用中/遷移/往返/容錯、ExtractContent），全套 138 綠；build／repoLint／docLint 0；
  發佈 exe 啟動 smoke 通過。圖片貼上/上傳/自動解釋等 UI 互動留手動/e2e 驗；GATE §5 產 docs/test-summary-issue36.pdf(A5)。

## [0.11.0] - 2026-07-05

Issue #34 增量：整合維運/檢視為單一 Office 式主視窗、筆記改多層可拖曳樹狀、結果視窗調整收藏入口。

### 變更
- **統一主視窗**（`MainWindow`）：頂部功能列分頁（**圖示在上、文字在下**：📒筆記／🕘歷史／⚙選項／
  ℹ關於）＋下方對應功能頁，取代原 `DockWindow`／`HistoryWindow`／`NotesWindow`／`SettingsWindow`
  各獨立視窗；標準工作列視窗、關閉＝收合非結束、點工作列／Alt+Tab 還原；淺粉底＋小女孩 logo 背景。
- **筆記分頁**改用標準 `TreeView` **多層資料夾樹**：可新增子夾、拖曳移動節點（如檔案總管、防移入
  自身／子孫成環）；右側條目拖曳排序、播音／檢視／刪除。`NotesData` 樹化、向後相容舊平面 `notes.json`。
- **結果視窗**：入口按鈕移到卡片**底部工具列**（「加入我的筆記」＋「自動加入筆記」），移除「我的筆記」
  「歷史」按鈕（改由統一主視窗）。
- 系統匣選單改為「開啟主視窗／查詢歷史／我的筆記／選項／關於／結束」，前五開主視窗至對應分頁。

### 備註
- design.md ＜III.C.(C)＞ 頁面清單重構＋契約／IA／look／prsnSop 同步、intTest#20/#21；重繪 mockup
  （含 icon+text 分頁列，依 USR 回饋）。README 操作說明改分頁。
- NotesStore 樹純函式測試 +5（子夾／移動防環／舊平面升級），全套 122 綠；build／repoLint／docLint 0；
  發佈 exe 啟動 smoke 通過。視窗互動（拖曳／分頁切換）留手動／e2e 驗；GATE §5 產 docs/test-summary-issue34.pdf(A5)。

## [0.10.1] - 2026-07-05

Issue #32 修正：與前景程式共用滑鼠快捷鍵時，喚起／關閉結果視窗崩潰。

### 修正
- **結果視窗關閉的重入崩潰**：多處（喚起、開維運 UI、主控頁 `Activated`）直接 `_result?.Close()`，
  在關閉序列進行中（`OnClosing` 已起、`Closed` 尚未設 `_result=null`）重複 `Close()` 會拋
  `InvalidOperationException：Cannot call Close while a Window is closing`。前景程式（如 COP 儀表板）
  共用同一滑鼠鍵時焦點/關閉交錯更易觸發。改為集中於單一守衛 `App.CloseResult`（先解參考、僅在
  未 `IsClosing` 時關閉、極端交錯以 catch 兜底）。
- **滑鼠 hook 觸發收斂**：低階滑鼠 hook 連續命中以 `FireGate` 收斂（handler 未跑完前不重複派工），
  避免共用鍵情境下的觸發風暴。

### 備註
- design.md（modCapture 喚起契約／modPresent 呈現契約 invariant、intTest#19）同步。
- 新增單元測試 2 項（FireGate 收斂），全套 117 綠；視窗重入行為屬 WPF 生命週期，留手動／e2e 驗；
  GATE §5 產 `docs/test-summary-issue32.pdf`。

## [0.10.0] - 2026-07-04

Issue #14 增量：可設定應用情境提示以提升翻譯準確度（spec#8）。

### 新增
- **應用情境提示**（`paramContextHint`）：使用者可於「設定」以自然語言描述目前應用主題／情境
  （如「中世紀奇幻 RPG，用遊戲用語翻譯」）。查詢時 `QueryService.BuildPrompt` 將情境以
  **「參考、非指令」**語氣併入既有 text prompt、輔助翻譯語意；structured output 三欄
  （original／phonetic／translation）schema 不變；**留空＝維持現行預設提示行為**（回歸保護）。
- 設定視窗新增「應用情境提示」多行輸入框；`AppConfig.Context`（appsettings，預設空、可存可清）。

### 修正
- `SettingsWindow.Gather()` 於 #13 新增 `paramHistoryMax` 後漏帶該欄，導致存設定會把查詢歷史
  保留上限重置為預設 200；本版一併補齊（存設定不再重置 `HistoryMax`）。

### 備註
- design.md（3.2 研改：spec#8、`paramContextHint`、modQuery 查詢契約補情境注入 invariant、
  系統匣設定情境欄位、intTest#18）與 README 同步。
- 新增單元測試 7 項（BuildPrompt 空/非空/trim、AppConfig Context 往返/預設），全套 115 綠。
  情境注入之 prompt 組裝為純函式、已測；設定 UI 行為留手動／e2e 驗；GATE §5 產
  `docs/test-summary-issue14.pdf`。

## [0.9.0] - 2026-07-04

Issue #28 增量：查詢結果可收藏為我的筆記並依資料夾分類檢視（spec#7）。承接 #13。

### 新增
- **我的筆記儲存**（`modQuery/NotesStore`＋`NoteEntry`）：把查詢結果／歷史條目收藏至
  `%APPDATA%\ScreenTrans\notes.json`（資料夾＋條目＋順序）；**加入去重**（英文原文正規化為 key、
  跨全部資料夾）；資料夾新增／更名／刪除、同夾拖曳排序、跨夾移動；讀寫失敗退空／靜默降級、
  金鑰不入筆記；獨立於歷史（不受 `paramHistoryMax`／歷史清除影響）。
- **我的筆記視窗**（`modPresent/NotesWindow`）：左自訂資料夾樹、右該夾條目（新在上、前端 `≡`
  拖曳排序），單筆播音／檢視（重用結果卡片三欄詳情與整句/逐字發音）／刪除、右鍵移到他夾；ESC 關閉。
- **右下角 toast**（`modPresent/ToastNotifier`）：加入時「已加入」／「已在筆記中」，不搶焦、
  命中穿透、逾時自動淡出。
- 入口：結果視窗「＋筆記」「📒 我的筆記」按鈕、歷史條目「＋筆記」、常駐主控頁「我的筆記」、
  系統匣「我的筆記…」；收藏後開著的筆記視窗即時刷新。

### 備註
- design.md（3.2 研改：spec#7、orgSop#4→teamSop#4.1/#4.2→prsnSop#4.1.1/#4.2.1、我的筆記頁、
  e2eTest#04／docProgTest#04／intTest#15-17；並補上 #13 常駐主控頁/系統匣入口的圖文一致）與
  README 同步；新增 `page-我的筆記頁.png`。
- 新增單元測試 16 項（NotesStore 去重／資料夾 CRUD／排序／跨夾移動／往返／容錯），全套 108 綠。
  視窗互動（拖曳排序、toast、TreeView）以純函式與儲存契約單元測試為底、UI 行為留手動／e2e 驗；
  GATE §5 產 `docs/test-summary-issue28.pdf`。

## [0.8.0] - 2026-07-04

Issue #13 增量：查詢歷史紀錄可回顧檢視與重聽（spec#6）。

### 新增
- **查詢歷史自動留存**（`modQuery/HistoryStore`）：每次成功查詢（非空結果）自動寫入
  `%APPDATA%\ScreenTrans\history.json`（時間＋原文／KK 音標／中譯），新在前、達
  `paramHistoryMax`（預設 200）時環形截汰最舊；讀寫失敗退空清單／靜默降級、不影響查詢
  主流程；金鑰不入歷史。
- **查詢歷史視窗**（`modPresent/HistoryWindow`）：左側依日期分組、右側該日條目垂直堆疊
  （新在上、最舊在下），單筆可播音（英文原句）、檢視（重用結果卡片回三欄中英詳情與整句／
  逐字發音）、刪除；頂部「清除全部」。獨立視窗、非結果視窗、不被下一次查詢關閉。
- 開啟入口三處：結果視窗「🕘 歷史」按鈕、常駐主控頁「查詢歷史」按鈕、系統匣「查詢歷史…」；
  查詢新增時開著的歷史視窗即時刷新。
- `paramHistoryMax`（appsettings，預設 200；非正值套用預設）。

### 備註
- design.md（3.2 研改：spec#6、orgSop#3→teamSop#3.1/#3.2→prsnSop#3.1.1/#3.2.1、查詢歷史頁、
  e2eTest#03／docProgTest#03／intTest#13-14）與 README 同步；新增 `page-查詢歷史頁.png`。
- 新增單元測試 18 項（HistoryStore 純函式截汰／往返／容錯、AppConfig HistoryMax 邊界），全套
  92 項綠。視窗互動（播音、日期分組渲染）以純函式與儲存契約單元測試為底、UI 行為留手動／e2e 驗；
  GATE §5 產 `docs/test-summary-issue13.pdf`。
- 「加入我的筆記／資料夾／拖曳」整套屬另一增量（#28、依賴本增量），不在本版。

## [0.7.1] - 2026-07-04

Issue #12 增量：結果視窗失焦時不自動關閉，改由明確操作關閉。

### 變更
- **結果視窗失焦（切換到其他視窗）不再自動關閉**：移除 `ResultWindow.OnDeactivated`
  自動關閉，使用者切去對照其他視窗（遊戲／字典／瀏覽器）時查詢結果保留不消失。
- 關閉改由明確操作觸發：`ESC`／關閉鈕，或**下一次查詢取代**——喚起流程於新查詢開始時
  關閉前一結果視窗，維持「同時至多一個結果視窗」不堆疊。

### 備註
- 一併清理隨 `OnDeactivated` 移除而失效的 `_isLoading` 死碼欄位。
- design.md（＜II.B＞look、modPresent 呈現契約＋invariant、docProgTest #08）與 README 同步
  新關閉語意。失焦保留、取代不堆疊等視窗行為屬 WPF 焦點/生命週期，以既有純函式單元測試
  （74 項全綠）為底、關閉行為留手動／e2e 驗；GATE §5 產 `docs/test-summary-issue12.pdf`。

## [0.7.0] - 2026-07-04

Issue #25 增量：執行後保留常顯操作入口（工作列按鈕型），免受系統匣自動隱藏影響。

### 新增
- **常駐主控頁**（`DockWindow`，工作列按鈕型可見入口）：啟動即建立、預設最小化不擋遊戲，
  顯示金鑰狀態與當前喚起快捷鍵，提供「設定…」「結束」；可隨時從工作列或 `Alt+Tab` 找回，
  不再依賴 Windows 系統匣顯示設定（換版／換路徑後仍可尋）。
- 系統匣新增「開啟主控頁」、雙擊圖示即開主控頁；系統匣退為輔助入口。
- `AppStatusText`：維運狀態顯示文字單一來源（金鑰狀態／快捷鍵／tray 提示），主控頁與系統匣
  共用不各寫一份（含單元測試）。

### 變更
- **關閉主控視窗（✕／最小化）＝收合而非結束**程式（`DockWindow` 攔截 `Closing` 改最小化）；
  唯主控頁或系統匣「結束」才真正退出常駐。`ShutdownMode` 維持 `OnExplicitShutdown`。

### 備註
- 全域熱鍵截圖查詢主動線不變。工作列按鈕之實機可尋性、關視窗＝收合等留發車環對成品驗；
  本層以 `AppStatusText` 單元測試＋真實 `DockWindow` 渲染佐證，GATE §5 產
  `docs/test-summary-issue25.pdf`。依 USR 授權採工作列按鈕型入口，回修 design spec#1「常駐」描述。

## [0.6.0] - 2026-07-04

Issue #11 增量：結果視窗英文句支援逐字點選發音（保留整句朗讀）。

### 新增
- 結果視窗英文原文改為逐字可點：點選任一單字即以 Windows 語音（en-US）單獨朗讀該字
  （游標呈可點狀、tooltip 提示），整句朗讀與自動播放並存不受影響。
- `EnglishWordTokenizer` 純函式（不依賴 WPF、可單元測試）：切分英文句為單字／分隔 token，
  前後標點剝除、單字內部撇號（`'` `’`）與連字號（`-`）及大小寫原樣保留，token 串接還原原句。

### 變更
- 英文整句播放鈕文案「▶ 英文發音」→「▶ 整句發音」，與逐字單字發音區分。

### 備註
- 沿用既有 `ISpeechService`（Issue #9）介面朗讀單字，未新增契約；design 於 spec#4 範圍內
  擴充逐字發音，＜III＞新增 intTest#11（單字發音＋標點切分）、`page-查詢結果頁` 示意圖更新。

## [0.5.0] - 2026-07-04

Issue #10 增量：可自訂喚起快捷鍵（鍵盤/滑鼠組合），設定採監聽輸入、Esc 離開。

### 新增
- 喚起快捷鍵可自訂：鍵盤組合（修飾鍵＋主鍵）走 `RegisterHotKey`；滑鼠中鍵／側鍵
  （`XButton1`／`XButton2`）／左右鍵同按走低階滑鼠 hook `WH_MOUSE_LL`。
- 系統匣「設定」新增「喚起快捷鍵」：顯示當前綁定＋「變更」進入監聽模式，直接按下鍵盤
  組合或滑鼠鍵擷取、`Esc` 取消；存 `paramHotkey`（appsettings）重啟沿用。
- `HotKeyBinding`（可序列化綁定 model，含序列化/解析/組合比對單元測試）；系統匣提示、
  「關於」文案改為動態顯示當前快捷鍵。

### 修正
- `SettingsWindow.Gather()` 原以 3-arg `AppConfig` 建構子重建組態、遺漏 `MaxRetries`
  （存設定會重置為預設 2）；一併修正為帶全欄（含新 `paramHotkey`）。

### 備註
- 依 USR 拍板採鍵鼠全支援，回修 design modCapture 熱鍵契約：放寬「禁低階 hook」為
  「滑鼠 `WH_MOUSE_LL` 允許、callback 輕量放行且確保釋放；鍵盤仍走 `RegisterHotKey`」；
  spec#1 併入「喚起快捷鍵可自訂」（未新增 spec#6）。
- 低階滑鼠 hook 之實機全域延遲與重啟沿用留發車環對成品驗；本層以元件測試＋設定視窗
  渲染截圖佐證，GATE §5 產 A5 `docs/test-summary-issue10.pdf`。

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
