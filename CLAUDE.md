# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 專案

**Haytham Core** 是團隊 cairnpack 的可複用 Minecraft 啟動器核心（C#，GPL-3.0-or-later）：CAS 倉庫與 GC、版本中繼資料、Java、載入器安裝、伺服器 runtime、帳號、遊戲程序。它不認識 Manifest、Lockfile 或實例，啟動入口只收「遊戲目錄＋版本＋載入器」。

另外兩個產品：整合包工具鏈 **Gebinde**（`CairnPack/Gebinde`）與桌面啟動器 **Haytham Launcher**（以子模組引用本 repo）。跨產品的設計文件、ADR 與名詞表目前放在 Haytham Launcher repo。

**現況**：只有專案骨架，還沒有程式碼。啟動核心由維護者重新設計，不翻譯舊版或 ProjBobcat。

## 架構

| 專案 | 內容 | 可依賴 |
|---|---|---|
| `Haytham.Store` | CAS、下載、檔案連結、暫存目錄；GC（根集合由外部提供） | 無 |
| `Haytham.Minecraft` | 啟動核心的純領域：版本解析、規則、參數、natives、崩潰診斷；不碰磁碟與網路 | 無 |
| `Haytham.Launch` | 啟動核心的 I/O：版本中繼資料、Java、載入器安裝、伺服器 runtime、帳號、遊戲程序 | Minecraft、Store |

- **Haytham Core 與 Gebinde 互不引用。** `Haytham.*` never 引用 `Gebinde.*`。
- **全域只有一個 CAS，以 sha1 為鍵**（`objects/<前兩碼>/<sha1>`）。物件一律唯讀；下載先原子寫入 `object-*.part`，驗證大小、sha1 與可選的 sha512 後才移入。
- **暫存目錄持有鎖並記錄遊戲 PID**（`.haytham-lock`）；具名 Maven 路徑只在暫存副本中出現。
- **不在任何遊戲目錄寫 `launcher_profiles.json` 或建立 `versions\`。**
- **Microsoft 登入的 client ID 由呼叫端傳入，不設預設值**；原始碼裡不放任何 client ID。
- **機密**：含憑證的型別 `ToString()` 一律遮蔽（不要用 `record`），命令列與 token 不寫進 log。
- **授權**：GPL-3.0-or-later。為了保留日後放寬授權（例如 LGPL）的可能，不收進第三方 GPL 程式碼，也不加入 GPL-only 的相依。

## 慣例

- 文件與領域名詞用繁體中文；程式碼、識別字、註解與 API 文件用英文。
- Commit 訊息為雙語：`type(scope): 中文 / English`。作者 email 用 GitHub 的 noreply 地址，不用個人信箱。
- 新程式碼只用新名稱：`gebinde.toml`、`gebinde.lock`、`.haytham-lock`、`GEBINDE_`／`HAYTHAM_` 環境變數；程式碼中不出現 `cairn`。
- 參考 ProjBobcat、HMCL、Prism 時可參考但不複製；舊版 Cairn（Kotlin）只當行為規格，程式碼要重新寫。
- `.claude/`、`.codex/`、`.codei18n/` 與建置產物是本機內容，不要提交。

## 重要變更必須留下筆記

非平凡變更（行為、架構、跨檔案契約、流程與工具鏈、測試策略、落盤／網路／設定格式）前，依 `.agents/skills/write-notes-like-deepseek/SKILL.md` 在 `.agents/notes/` 撰寫或更新筆記；新構想放 `proposed/`，實作時隨程式碼移到 `implemented/`；放棄某個方案前先寫出它最有說服力的理由。提交前執行 `npm run verify-notes`（Node 22.18 以上）。

## 建置

.NET 10 SDK（版本由 `global.json` 釘住）。

```powershell
dotnet build HaythamCore.slnx
```

`Directory.Build.props` 開啟 nullable、警告當錯誤與 `IsAotCompatible`；每個專案一個組件，相依方向由專案參考強制。測試框架尚未決定，決定後再加入 `tests/`。
