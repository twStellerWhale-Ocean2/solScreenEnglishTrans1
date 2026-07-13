---
name: techItem桌面通知
date: 2026-07-06
description: 技術選型·元件層(techItem) Profile —— 「桌面系統通知（native desktop toast／Action Center）」功能型態，統一標準產品 Windows 原生通知（WinRT Windows.UI.Notifications，經 AUMID 註冊之未封裝桌面 App）。於 design.md ＜III.C.(A) 技術選型＞落地版本/用法；與 techStack 多對多。與應用內自畫浮層（ToastNotifier）互補、責任區隔。
---

# I. 主旨目的

定義建置單元需要「將訊息以**作業系統原生通知**呈現、進入系統通知中心（Action Center）可回看」此一功能型態時的統一選型。**型態即契約身份、標準產品寫於內容**；日後換產品只改本契約一處，引用之 design 不動。於 design.md ＜III 模組設計.C.(A) 技術選型＞引用並落地版本/用法。此型態專責**系統層可回看通知**，與「應用內自畫浮層」（不進系統中心、不受勿擾影響、可穿透覆於全螢幕之上）責任區隔、依情境擇一或並用。

# II. 參考準備

* **統一標準產品**：**Windows 原生通知**——Windows 10/11 之 `Windows.UI.Notifications`（WinRT `ToastNotificationManager` / `ToastNotification`），以 toast XML 組裝標題／內文（多行）送出，通知進入**通知中心佇列可回看**。
* **未封裝桌面 App 之前提（AUMID）**：未封裝（非 MSIX）之桌面 App 須有一個帶 `System.AppUserModel.ID`（AUMID）之**開始功能表捷徑**，並於進程啟動時 `SetCurrentProcessExplicitAppUserModelID(aumid)`；`CreateToastNotifier(aumid).Show(toast)` 方能以該 App 身份顯示並歸戶。安裝式散佈（如 Velopack／MSIX）之安裝器通常已建立帶 AUMID 之捷徑，沿用其 AUMID 即可。
* **平台前提（TFM）**：以 .NET 取用 WinRT `Windows.UI.Notifications` 需將 TFM 指向 Windows 10 版本（如 `net9.0-windows10.0.19041.0`）以取得 WinRT 投影；**免額外 NuGet**（亦可用 `CommunityToolkit.WinUI.Notifications` 之 `ToastContentBuilder` 便利組裝、代價為多一相依）。TFM 上調須確認既有 WPF／WinForms／P-Invoke／原生相依相容（一般為超集、相容）。
* **能力／限制前置**：原生通知受 **OS 勿擾／專注（Focus Assist）** 與**全螢幕獨佔**抑制——此時**只進通知中心、不跳橫幅**（屬 OS 行為、App 不可控）；故「須即時覆於全螢幕遊戲之上」之回饋不宜用原生通知，應用自畫浮層。**降級**：無 AUMID／未安裝／WinRT 不可用時，須明確降級（如改走應用內浮層或略過），不得崩潰。

# III. 內容程序

* **呼叫慣例**：組裝 toast（標題＋內文，純字串內容組裝可抽純函式、可單元測試）→ 經 `ToastNotifier.Show`；非阻塞、UI 執行緒安全。
* **輸入／輸出**：輸入＝標題／內文（多行）／可選圖示；輸出＝一則系統通知（進通知中心）。點擊啟動（activation）為選配——需要「點通知回應」時才註冊 COM activator，僅顯示不互動則免。
* **參數**：AUMID 走組態／安裝決定、不硬編；通知內容由業務層組裝。
* **可驗證性**：內容組裝（標題／內文文案）抽純函式單元測試；送出與降級路徑抽介面（如 `INotificationService`）以便未安裝態注入假實作、不實際彈通知。
* **隱私**：通知內容不含機密（金鑰等不入通知）。

# IV. 備註紀錄

* 2026-07-06：建立。techItem「桌面系統通知（native desktop toast）」型態首份；統一 Windows 原生通知（`Windows.UI.Notifications`、AUMID 註冊）。因 solLingoIsland 增量 #101「發音練習回饋改系統通知」立案而補建；與應用內自畫浮層（ToastNotifier）責任區隔——浮層供「須覆於全螢幕遊戲、不受勿擾影響」之即時回饋，原生通知供「可回看、進通知中心」之回饋。
