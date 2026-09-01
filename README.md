# DualSense Voice

[![Windows build](https://github.com/tkitauji/dualsense-voice-input/actions/workflows/build.yml/badge.svg)](https://github.com/tkitauji/dualsense-voice-input/actions/workflows/build.yml)

USBまたはBluetooth接続したDualSenseの内蔵マイクを使い、ローカルのWhisperで日本語を文字起こしするWindowsアプリです。コントローラーの物理マイクボタンを押すと音声入力を開始し、もう一度押すと文字起こしして元のアプリへ貼り付けます。画面上の録音ボタンやグローバルホットキーはありません。

## 必要環境

- Windows 11（Whisper.net 1.9.1のCPUランタイム要件）
- Visual Studio 2022（.NETデスクトップ開発、Windows 10/11 SDK、MSIX Packaging Tools）
- .NET 8 SDK
- USBまたはBluetooth接続したDualSense

## 開発実行

1. DualSenseをUSBまたはBluetoothで接続します。
2. `DualSenseVoice.sln`をVisual Studioで開き、NuGetパッケージを復元します。
3. x64で起動します。USBではWindows標準マイク、Bluetoothでは直接入力が自動選択され、ミュート待機状態になります。
4. 「モデルを準備」を押します（初回のみ）。
5. 入力したいアプリにカーソルを置き、DualSenseのマイクボタンを押して話します。開始音が鳴ります。もう一度押すと終了音が鳴り、音声が文字へ変換されて入力先へ貼り付けられます。押し忘れた場合は60秒で自動終了します。

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

Partner Centerの制限付き機能の説明には、`runFullTrust`を「DualSense HID Raw Inputからの物理マイクボタンおよび音声の受信、Bluetooth音声クロックの送信、ユーザー操作によるクリップボード貼り付けに必要」と記載してください。Bluetooth音声はWindowsの通常の録音デバイスではなく、アプリがHIDから直接取得します。

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
- Bluetooth接続では、物理マイクボタンとDualSense独自のHID音声報告をRaw Inputで受け取り、Opusをアプリ内で直接PCMへ復号します。ミュート中は音声ストリームを停止します。仮想マイクやカーネルドライバーはインストールしません。
- USB接続では、Windowsが公開する標準録音デバイスからWASAPIで音声を取得し、物理マイクボタンだけをRaw Inputで監視します。
- Bluetooth実機試験では、マイクON中にWinMM/DirectInputを534回読み取り、軸ずれ・誤ボタン・読取エラーはいずれも0でした。
- 接続状態を3秒ごとに確認し、切断・再接続後はアプリ側も自動的に接続し直します。
- Bluetooth直接入力中の本体マイクLEDは点灯しません。開始・終了はWindows効果音と画面表示で通知します。
- 自動貼り付けはクリップボードとWin32 `SendInput`を使います。管理者権限で動くアプリには、通常権限の本アプリから貼り付けできません。
- Steam、DS4Windows、DSXなどが同じコントローラーへ出力していると音声クロックが競合する場合があります。録音できない場合は、それらを終了して「再読込」してください。
- Store提出前に、モデル配布元・Whisper.net・NAudio・Concentus・HidSharpのライセンス表記をStore listingまたはアプリ内Aboutへ追加してください。

Bluetooth相互運用のワイヤ形式は、[LinuxAudio4Dualsense5](https://github.com/GeorgLegato/LinuxAudio4Dualsense5)および[DS4WindowsのBluetooth audio実装](https://github.com/hbashton/DS4Windows/blob/main/docs/dualsense-bluetooth-audio-haptics.md)で公開されている観測結果と実機挙動を照合しています。これらのプロジェクトのバイナリやドライバーは本アプリへ組み込みません。
