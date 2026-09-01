# DualSense Voice

[![Windows build](https://github.com/tkitauji/dualsense-voice-input/actions/workflows/build.yml/badge.svg)](https://github.com/tkitauji/dualsense-voice-input/actions/workflows/build.yml)

DualSenseの内蔵マイクを明示的に選び、ローカルのWhisperで日本語を文字起こしするWindowsアプリです。`Ctrl + Shift + Space`で録音を開始・停止し、停止後に元のアプリへ文字列を貼り付けます。

## 必要環境

- Windows 11（Whisper.net 1.9.1のCPUランタイム要件）
- Visual Studio 2022（.NETデスクトップ開発、Windows 10/11 SDK、MSIX Packaging Tools）
- .NET 8 SDK
- USB接続したDualSense（Bluetoothではコントローラー操作はできますが、内蔵マイクがWindowsの録音デバイスとして公開されません）

## 開発実行

1. Windowsの「設定 → プライバシーとセキュリティ → マイク」でデスクトップアプリのマイク利用を許可します。
2. `DualSenseVoice.sln`をVisual Studioで開き、NuGetパッケージを復元します。
3. x64で起動し、入力デバイスから `Microphone (Wireless Controller)` またはDualSenseに該当する項目を選びます。
4. 「モデルを準備」を押します（初回のみ）。
5. 入力したいアプリにカーソルを置き、`Ctrl + Shift + Space`で録音開始、もう一度押して停止します。

## Microsoft Store提出

1. Partner Centerでアプリ名を予約し、「製品管理 → 製品 ID」で `Package/Identity/Name`、`Publisher`、表示名を確認します。
2. PowerShellで次を実行します。スクリプトがMicrosoft公式Build Toolsを自動復元します。

   ```powershell
   ./build-msix.ps1 `
     -PackageName 'Partner CenterのIdentity Name' `
     -Publisher 'Partner CenterのPublisher' `
     -PublisherDisplayName 'ストアの発行元表示名' `
     -Version '1.0.0.0'
   ```

3. 生成された `artifacts/package/*.msix` をWindows App Certification Kitで検証します。
4. Partner Centerのパッケージ画面へMSIXをアップロードし、説明・プライバシーポリシー・スクリーンショットを登録して提出します。Store提出時の署名はMicrosoftが行います。

`Packaging/AppxManifest.xml.in`には `microphone` と `runFullTrust` capability、packaged desktop appの実行属性、必須ロゴが設定済みです。リポジトリ既定値で作ったMSIXは開発検証用の未署名パッケージであり、Partner CenterのIdentity値に置き換えてから提出してください。

## 開発用MSIXの実機インストール

配布物 `DualSenseVoice-dev-msix-v1.0.0.zip` は、自己署名した開発用MSIX、公開証明書、インストール・アンインストールスクリプトを含みます。これはStore公開版ではありません。

1. ZIPを展開する。
2. `Install-DevelopmentPackage.ps1`をPowerShellで実行する。
3. `INSTALL`と入力し、Windowsの証明書確認を承認する。
4. スクリプトはMSIXをインストールした直後に、追加した信頼済みルート証明書を削除します。
5. テスト後は`Uninstall-DevelopmentPackage.ps1`を実行します。

組織管理PCでは証明書の追加がポリシーで禁止されている場合があります。その場合は、self-contained ZIP版を使って実機テストしてください。

## 設計上の注意

- 音声認識は端末内で完結します。初回のモデル取得だけネット接続が必要です。
- 自動貼り付けはクリップボードとWin32 `SendInput`を使います。管理者権限で動くアプリには、通常権限の本アプリから貼り付けできません。
- WindowsではBluetooth接続したDualSenseの内蔵マイクが録音デバイスとして公開されません。音声入力にはUSB接続を使用してください。
- Store提出前に、モデル配布元・Whisper.net・NAudioのライセンス表記をStore listingまたはアプリ内Aboutへ追加してください。

