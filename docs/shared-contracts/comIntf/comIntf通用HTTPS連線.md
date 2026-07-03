---
name: comIntf通用HTTPS連線
date: 2026-05-12
description: 通用 HTTPS 通訊承載契約，供人員、瀏覽器、系統或外部服務以 TLS 保護的 HTTP 連線互動。
---

# I. 主旨目的

定義本方案中使用 HTTPS 作為安全通訊承載時的共通限制。

# II. 參考準備

* 以 HTTPS/TLS 作為傳輸保護。
* 僅描述通訊承載，不定義業務 API、行為或資料格式。

# III. 內容程序

* 用戶端與服務端應使用有效 TLS 憑證。
* 服務端應支援必要的 HTTP method、header 與狀態碼。
* 逾時、重試、身份驗證與授權細節由對應 apiIntf、runWi、etyCfg 或線上標示參數定義。

# IV. 備註紀錄

* 2026-05-12：依 solCbm 方案設計補建。
