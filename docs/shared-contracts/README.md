# docs/shared-contracts — 共享契約副本

本資料夾放 [`../design.md`](../design.md) 引用之**共享契約（通用／標準／常例）副本**，正本在中央契約庫（kdbUserSkills `2tech-incrSet-shared/契約庫`），由 sub-sync-contracts 對正本檢一致、**不得本地修改**。本案**自訂／專用設計不成檔**——寫入 design.md 文字（引用處 `[標記]＋就近說明`）。

## 分類 → 檔案格式對照

| 資料夾 | 契約類型 | 機器可驗格式（優先） |
|---|---|---|
| apiIntf/ | API 介面 | OpenAPI yaml、markdown 協定 |
| comIntf/ | 連線／事件 | markdown 協定說明 |
| techApp/ | 系統類型 Profile | Markdown Profile |
| techItem/ | 元件型態 Profile | Markdown Profile |
| techStack/ | 平台 Profile | Markdown Profile（建置／測試／部署指令、產物型態） |

## 現有副本

- apiIntf：標準OPENAI的API協定
- comIntf：通用HTTPS連線
- techApp：桌面查詢工具
- techItem：語音合成
- techStack：DotnetWin（候選家規選型，待 kdbUserSkills 裁決）

## 自訂設計（不在此，見 design.md 文字）

本案自訂 etyCfg（sysScreenTrans 組態）、runWi（熱鍵喚起框選／辨識翻譯選區／查看聆聽結果）、setWi（安裝金鑰／啟動結束常駐／移除）、datIntf（查詢結果格式）、comIntf（本機桌面操作）皆為本案專屬、不成檔——內容在 design.md 就近文字（invariant／欄位格式／行為步驟）描述。
