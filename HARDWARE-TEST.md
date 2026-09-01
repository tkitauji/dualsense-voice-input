# DualSense実機テスト

このチェックはDualSenseをUSBまたはBluetooth接続したWindows 11 PCで行います。

## 前提

1. DualSenseをUSBまたはBluetoothでPCへ接続し、ライトバーが点灯していることを確認する。
2. 通常の機能テストと`Baseline`では、Steam、DS4Windows、DSXなど、コントローラーへ出力するアプリを終了する。共存テストでは下記シナリオで指定した対象だけを起動する。

## Bluetooth共存テスト

次の3回を順に実行します。各回とも7秒間、スティック、トリガー、ボタン、D-padを中立に保ちます。結果は`artifacts/hardware`に時刻付きで保存されます。

```powershell
.\run-coexistence-test.ps1 -Scenario Baseline
.\run-coexistence-test.ps1 -Scenario Steam
.\run-coexistence-test.ps1 -Scenario FF14
```

- `Baseline`: Steam、FF14、DualSense Voiceを終了して実行する。
- `Steam`: Steamを起動し、DualSense用Steam Inputを有効にしてから実行する。
- `FF14`: FF14 DirectX 11版でキャラクターを操作できるところまで入り、DualSense Voiceだけ終了して実行する。
- スクリプトは必要なプロセス、DualSense接続、WinMM、DirectInput、Bluetooth音声フレームを確認します。`PASS`以外は合格として扱いません。

## テスト項目

- [ ] アプリを起動すると、接続方法に応じて`USB（Windows標準）`または`Bluetooth（直接）`が自動選択される。
- [ ] 「モデルを準備」を押すと約148MBのモデルを取得し、SHA-256検証後に「準備完了」と表示される。
- [ ] 待機中は「ミュート中」と表示され、Bluetooth音声レポートが停止している。
- [ ] テキストエディターへカーソルを置き、DualSenseの物理マイクボタンを押すと「マイクON」と表示される。
- [ ] 開始時に上昇音、終了時に下降音が鳴る。
- [ ] DualSenseへ向かって「今日は音声入力のテストです」と話す。
- [ ] もう一度物理マイクボタンを押すとミュートへ戻り、認識結果がテキストエディターへ貼り付けられる。
- [ ] アプリ内の入力結果にも同じ文章が表示される。
- [ ] 「自動貼り付け」をオフにすると、結果は他アプリへ貼り付けられない。
- [ ] 「コピー」で結果をクリップボードへコピーできる。
- [ ] 録音中にDualSenseを切断しても、アプリが終了せずエラー表示へ戻る。
- [ ] 録音中に切断した場合、切断までに受信した音声が文字起こしされ、一時ファイルが削除される。
- [ ] 再接続して「再読込」を押すとDualSenseが再表示される。
- [ ] 再ミュート後、次のミュート解除までマイク音声報告が停止する。
- [ ] 60秒間停止操作をしない場合、自動的に終了して文字起こしされる。
- [ ] `run-coexistence-test.ps1 -Scenario Baseline`が`PASS`し、WinMM・DirectInputの両方で軸ずれ、ボタン/D-pad誤入力、読取エラーが0になり、Bluetooth音声フレームも取得できる。
- [ ] FF14を実際に起動した状態でマイクをON/OFFしても、操作不能、スティックやボタンの誤入力、切断が発生しない。
- [ ] `run-coexistence-test.ps1 -Scenario Steam`が`PASS`し、その後もSteam Inputの通常操作・振動・再接続が維持される。
- [ ] `run-coexistence-test.ps1 -Scenario FF14`が`PASS`する。
- [ ] アプリをもう一度起動しても2つ目のプロセスは残らず、既存ウィンドウが表示される。

## USB経路の単独診断

DualSense Voiceを終了し、DualSenseをデータ通信対応USBケーブルで接続して次を実行します。1回目の物理マイクボタンで録音を開始し、話してから2回目で停止します。

```powershell
dotnet run --project tools/DualSenseUsbHardwareProbe -c Release
```

`RESULT|USB standard microphone and physical mute-button capture passed.`と表示され、録音時間・バイト数・音声エネルギーが0より大きければ、Windows標準マイクとUSB Raw Inputボタンの両経路が合格です。`USB_ENDPOINT_MUTE`には、物理ボタン前後でWindowsの録音エンドポイント自体がミュートされたかも記録されます。その後、アプリ本体で文字起こしと貼り付けまで確認します。

## 既知の制約

- 管理者権限で動くアプリへは、通常権限の本アプリから自動貼り付けできません。
- CPUはAVX、AVX2、FMA、F16Cに対応している必要があります。
- Bluetooth直接入力はWindowsの通常の録音デバイス一覧には現れません。本アプリがDualSenseのHID音声を直接復号します。
