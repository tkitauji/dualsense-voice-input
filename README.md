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
5. 入力したいアプリにカーソルを置き、DualSenseのマイクボタンを押して話します。短い上昇音が鳴ります。もう一度押すと下降音が鳴り、音声が文字へ変換されて入力先へ貼り付けられます。押し忘れた場合は60秒で自動終了します。

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

最終MSIXは、最新版Windows SDKのWindows App Certification Kitをインストールした管理者PowerShellから次で検査します。

```powershell
.\run-wack.ps1 -PackagePath .\artifacts\package\DualSenseVoice-1.0.0.0-x64.msix
```

検査レポートは既定で`artifacts\wack`へ保存されます。スクリプトは既存レポートを上書きしません。

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
- USB単独診断`tools/DualSenseUsbHardwareProbe`は、アプリと同じWASAPI・Raw Input経路を使い、2回の物理ボタン押下間の録音時間・データ量・音声エネルギーを検証します。
- Bluetooth実機試験では、マイクON中にWindowsのWinMMジョイスティック状態を534回読み取り、軸ずれ・誤ボタン・読取エラーはいずれも0でした。
- 開発用の共存診断ツールは、Bluetoothマイク取得中にWinMMとDirectInputを同時かつ非排他的に監視します。DirectInputでの実機結果、およびSteam Input・FF14を起動した状態での最終確認は未完了です。実行手順は`tools/GameInputInterferenceProbe/README.md`にあります。
- `run-coexistence-test.ps1`はBaseline・Steam・FF14の前提条件を確認して同じ診断を実行し、比較可能な時刻付きログを`artifacts/hardware`へ保存します。
- `run-accessibility-test.ps1 -ExecutablePath <DualSenseVoice.exe>`は、UI Automationの状態読み上げイベントと文字起こし欄のコピー操作を実アプリで検証し、テスト前のクリップボード内容を終了時に復元します。
- 接続状態を3秒ごとに確認し、切断・再接続後はアプリ側も自動的に接続し直します。
- 音声入力中にBluetoothが切れても即時検出し、そこまで受信できた音声は文字起こししてから再接続待ちへ戻ります。
- Bluetooth直接入力中の本体マイクLEDは点灯しません。開始は上昇音、終了は下降音と画面表示で通知します。
- キーボードフォーカス、Windowsのハイコントラスト配色、UI Automationの日本語ラベルに対応し、録音・変換・エラーなどの状態変化はスクリーンリーダーへ通知します。
- アプリは1プロセスだけ動作します。スタートメニューなどから再度起動すると、既存ウィンドウを前面へ戻します。
- 自動貼り付けはクリップボードとWin32 `SendInput`を使います。音声入力開始前のウィンドウが現存し、実際に前面へ戻った場合だけ`Ctrl+V`を送ります。確認できない場合は別アプリへ誤入力せず、結果をクリップボードへ残します。管理者権限で動くアプリには、通常権限の本アプリから貼り付けできません。
- Steam、DS4Windows、DSXなどが同じコントローラーへ出力していると音声クロックが競合する場合があります。録音できない場合は、それらを終了して「再読込」してください。
- 配布物には、モデル・Whisper.net・NAudio・Concentus・HidSharp・.NETランタイムのライセンス本文と第三者通知を`Licenses`フォルダーへ同梱します。
- 初回取得するWhisper baseモデルは、ファイル長とSHA-256を検証してから保存します。
- 異常終了で本アプリ固有の一時WAVまたは未完了のモデル取得ファイルが残った場合は、次回起動時に対象名を厳密に確認して削除を再試行します。ほかの一時ファイルや正常なモデルは削除しません。

Bluetooth相互運用のワイヤ形式は、[LinuxAudio4Dualsense5](https://github.com/GeorgLegato/LinuxAudio4Dualsense5)および[DS4WindowsのBluetooth audio実装](https://github.com/hbashton/DS4Windows/blob/main/docs/dualsense-bluetooth-audio-haptics.md)で公開されている観測結果と実機挙動を照合しています。これらのプロジェクトのバイナリやドライバーは本アプリへ組み込みません。
