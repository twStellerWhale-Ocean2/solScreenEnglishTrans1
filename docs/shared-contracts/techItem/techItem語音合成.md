---
name: techItem語音合成
date: 2026-07-03
description: 技術選型·元件層(techItem) Profile —— 「語音合成（TTS）」功能型態，統一標準產品 Windows 內建語音合成（System.Speech／Windows.Media.SpeechSynthesis）。於 design.md ＜III.C.(A) 技術選型＞落地版本/用法；與 techStack 多對多。
---

# I. 主旨目的

定義建置單元需要「文字轉語音（TTS）朗讀」此一功能型態時的統一選型。**型態即契約身份、標準產品寫於內容**；日後換產品只改本契約一處，引用之 design 不動。於 design.md ＜III 模組設計.C.(A) 技術選型＞引用並落地版本/用法。

# II. 參考準備

* **統一標準產品**：Windows 內建語音合成——.NET 桌面應用優先採 `System.Speech.Synthesis`（SAPI，離線、免費、零外部依賴）；需較自然語音時可選 `Windows.Media.SpeechSynthesis`（WinRT）。
* **替代界線**：除非 design 有強需求（如多語言高擬真語音）且經 USR 核准，一律用 Windows 內建引擎；不得預設引入雲端 TTS（Azure／ElevenLabs／OpenAI TTS 等）增加成本與金鑰面。
* **語音可用性前置**：目標語言之語音包須於 OS 已安裝（如英文 `Microsoft David/Zira`）；設計須定義語音缺失時之降級行為（明確提示，不當機）。

# III. 內容程序

* **呼叫慣例**：以非同步方式播放，不阻塞 UI 執行緒；重複觸發時先停止前次播放再播新內容。
* **參數**：語音名稱（voice）、語速（rate）走組態，不硬編；預設採系統預設英文語音。
* **可驗證性**：播放呼叫須可由測試攔截驗證（介面抽象出 `ISpeechService` 之類邊界），單元測試不實際發聲。

# IV. 備註紀錄

* 2026-07-03：建立。techItem「語音合成（TTS）」型態首份；統一 Windows 內建語音合成。因 solScreenEnglishTrans1 立案而補建。
