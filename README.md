# playnite-with-erogamescape

[批評空間（ErogameScape）](https://erogamescape.dyndns.org/)からゲームのメタデータを取得する [Playnite](https://playnite.link/) 用プラグインです。

## 機能

批評空間のデータベースと外部APIから以下のメタデータを取得できます：

- **ゲーム名** / **ふりがな**
- **開発元** / **発売元**（ブランド名）
- **発売日**
- **評価スコア**（中央値をCommunity Scoreとして使用）
- **カバー画像**（VNDB / DMMフォールバック）
- **背景画像**（VNDBスクリーンショット、SFWフィルタリング済み）
- **説明文**（DLsite API / Getchu / VNDBフォールバック）
- **ジャンル**（DLsiteジャンルタグ / 公式ジャンル）
- **タグ**（POVデータ：ジャンル・背景・傾向カテゴリ）
- **シリーズ**（ゲームグループ）
- **特徴**（属性データ）
- **リンク**（批評空間・公式サイト・DLsite・DMM）
- **年齢レーティング** / **プラットフォーム** / **リージョン**

## データソース

| データ | 主要ソース | フォールバック |
|---|---|---|
| ゲーム情報・タグ・シリーズ・特徴 | 批評空間 SQL API | — |
| カバー画像 | VNDB | DMM |
| 背景画像 | VNDB（SFWスクリーンショット） | — |
| 説明文 | DLsite API | Getchu / VNDB |
| ジャンル | DLsite API | 批評空間公式ジャンル |

## インストール

1. [Releases](../../releases) ページから最新の `.pext` ファイルをダウンロード
2. ダウンロードした `.pext` ファイルをダブルクリック、またはPlayniteにドラッグ＆ドロップ
3. Playniteを再起動

## 使い方

1. ライブラリ内のゲームを右クリック →「メタデータを編集」→「ダウンロード」
2. メタデータソースとして「ErogameScape」を選択
3. ゲーム名で検索し、該当するタイトルを選択

自動メタデータダウンロード（ライブラリ → メタデータをダウンロード）にも対応しています。ゲーム名が完全一致する場合に自動的にメタデータを取得します。

## ビルド方法

### 前提条件

- .NET SDK（net462 をターゲット）
- Visual Studio 2022 または `dotnet` CLI

### ビルド

```bash
dotnet build -c Release
```

### .pext パッケージ作成

ビルド出力ディレクトリ（`bin/Release/`）内のファイルを ZIP 圧縮し、拡張子を `.pext` に変更してください。パッケージには以下のファイルを含めます：

- `ErogameScapeMetadata.dll`
- `HtmlAgilityPack.dll`
- `extension.yaml`

## 依存ライブラリ

- [PlayniteSDK](https://github.com/JosefNemworthy/Playnite) 6.11.0
- [HtmlAgilityPack](https://html-agility-pack.net/) 1.11.54

## ライセンス

[MIT License](LICENSE)
