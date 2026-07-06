---
name: techItem發音評分
date: 2026-07-06
description: 技術選型·元件層(techItem) Profile —— 「發音評分／語音發音評測（pronunciation assessment）」功能型態，統一標準產品 OpenAI 音訊輸入模型（chat completions 之 input_audio，gpt-audio 系列，沿用既有金鑰）。於 design.md ＜III.C.(A) 技術選型＞落地版本/用法；與 techStack 多對多，與 techItem語音合成（TTS 輸出）互為姊妹、責任區隔。
---

# I. 主旨目的

定義建置單元需要「將使用者朗讀之語音與目標文字比對、給出發音正確度評分」此一功能型態時的統一選型。**型態即契約身份、標準產品寫於內容**；日後換產品（如改用專用發音評測服務）只改本契約一處，引用之 design 不動。於 design.md ＜III 模組設計.C.(A) 技術選型＞引用並落地版本/用法。此型態專責**語音輸入之發音評分**，與 [techItem語音合成] 之**文字轉語音輸出（TTS）** 責任區隔、不相互替代。

# II. 參考準備

* **統一標準產品**：OpenAI 音訊輸入模型（`gpt-audio` 系列，如 `gpt-audio-mini`）——以 chat completions 之 `input_audio` 內容型別，將使用者錄音（WAV，base64）連同目標文字送出，**以提示要求回 JSON**（發音分數與必要說明）並穩健解析。**注意：`gpt-audio` 系列音訊模型不支援 structured outputs（`response_format` 之 `json_schema`／`json_object` 皆回 400）**，故 payload 不帶 `response_format`、改以提示約束輸出＋容錯解析（容忍 markdown 圍欄/贅字）。沿用既有 [apiIntf標準OPENAI的API協定]／[comIntf通用HTTPS連線] 與使用者自備金鑰（`OPENAI_API_KEY`、環境變數、不落地），**不新增第二家雲端廠商與金鑰面**。
* **替代界線**：除非 design 有強需求（如逐音素校準評分、離線評測）且經 USR 核准，一律用既有 OpenAI 金鑰之音訊模型；欲升級為專用發音評測（如 Azure Pronunciation Assessment，回逐音素 accuracy／fluency）時，於本契約換標準產品一處、design 引用不動（維持 `IPronunciationAssessor` 之類介面邊界）。
* **能力／成本前置**：目標模型須支援音訊輸入（非所有模型皆支援，須以組態指定音訊型模型）；評分屬每次觸發之線上呼叫，須計成本／延遲並定義失敗降級（無金鑰／無網／模型不支援／空錄音）之明確提示、不當機。評分為機率式、非校準之絕對量測，設計須以「**可調及格門檻**」定義通過與否，不宣稱為權威發音鑑定。

# III. 內容程序

* **呼叫慣例**：非同步呼叫、不阻塞 UI；沿用查詢層之逾時與有限次指數退避重試（暫時性錯誤 429／5xx／逾時可重試、永久性 4xx／解析失敗不重試）；使用者主動取消不視為錯誤。
* **輸入／輸出**：輸入＝使用者錄音（單聲道 WAV 為佳）＋目標文字（欲評之英文）；輸出＝模型回之 JSON 含發音分數（如 0–100 之整數）與可選簡短建議（音訊模型無 structured outputs，以容錯解析取出、鉗制 0–100）；分數與及格門檻比較後產生「通過／未通過」二元結果供 UI 呈現。**無朗讀防呆**：提示須令模型**先判定音訊是否含對目標文字之真正朗讀嘗試**——靜音／只有背景雜訊／與目標無關（無真正朗讀）一律回 `0` 並註明未偵測到朗讀，只有確有朗讀才評正確度；不得對雜訊寬容而回中庸分（否則「無聲得分」使評分失真）。**即時音量**：錄音期間可即時回報音量位準（0–1）供 UI 音量表回饋。
* **參數**：評分模型名稱、及格門檻走組態、不硬編；金鑰僅環境變數。
* **可驗證性**：評分呼叫須可由測試攔截驗證（介面抽象出 `IPronunciationAssessor` 之類邊界），單元測試不實際錄音、不打真網路；錄音擷取亦抽介面（`IAudioRecorder`）以便攔截。
* **隱私**：錄音僅供當次評分上傳、不落地保存；金鑰不隨錄音入日誌。

# IV. 備註紀錄

* 2026-07-06：建立。techItem「發音評分（pronunciation assessment）」型態首份；統一 OpenAI 音訊輸入模型（沿用既有金鑰）。因 solScreenEnglishTrans1 增量「筆記發音練習」立案而補建；與 [techItem語音合成] TTS 責任區隔。
* 2026-07-06（修訂）：標準產品收斂為 `gpt-audio` 系列（`gpt-4o-*-audio-preview` 已下架）；並記錄「音訊模型不支援 structured outputs（`response_format`）」之硬限制——改以提示要 JSON＋容錯解析。因 solScreenEnglishTrans1 增量 #97 實測 `gpt-audio-mini` 得證。
* 2026-07-06（修訂）：補「無朗讀防呆」——提示須先判定有無真正朗讀，靜音／雜訊／與目標無關一律 0、註明未偵測到朗讀；移除「不因雜訊過度扣分」之寬容。因 solScreenEnglishTrans1 增量 #99 實測「無聲/背景雜訊誤得約 65 分」得證，收斂標準做法。
