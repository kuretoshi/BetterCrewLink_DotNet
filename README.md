# BetterCrewLinkKai .NET

BetterCrewLinkKai をベースにした .NET/WPF 版デスクトップアプリです。

## 現在入っているもの

- .NET 8 / WPF のアプリケーション雛形
- BetterCrewLinkKai 由来の主要設定モデル
- JSON による設定保存
- Steam / Epic / Microsoft 版 Among Us の起動処理
- 接続、音声、ロビー設定の基本 UI
- ボイス接続サービスの差し替え用境界

## 起動

```powershell
dotnet run --project BetterCrewLinkKai.DotNet
```

## ビルド

```powershell
dotnet build BetterCrewLink_DotNet.sln
```

設定ファイルは `%APPDATA%\BetterCrewLinkKai.DotNet\settings.json` に保存されます。
