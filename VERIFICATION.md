# Verification report

Date: 2026-09-02
Environment: Windows 11 x64 (10.0.26200), .NET SDK 8.0.424

## Passed

- `dotnet build -c Release`: succeeded with 0 warnings and 0 errors.
- Self-contained x64 publish: succeeded.
- Published application startup smoke test: remained running for 5 seconds; no startup crash.
- Windows capture device enumeration: succeeded; 2 active microphones found.
- WASAPI capture through the same NAudio path used by the app: 445,440 bytes captured in about 1.2 seconds, 48kHz stereo 32-bit float.
- Local Japanese transcription: Windows Haruka synthesized `今日はデュアルセンスのマイクで音声入力をテストします。`; Whisper base returned `今日はデュアルセンスのマイクで音声入力をテストします` exactly apart from punctuation.
- Microsoft MakeAppx 10.0.28000.2705: package creation succeeded.
- MakeAppx package extraction: succeeded, proving the generated package can be parsed and its block map/content extracted.
- Package manifest semantic validation: passed as part of MakeAppx packaging.
- SignTool: development MSIX signing succeeded. The matching public certificate and an interactive installer are included for hardware testing.
- Bluetooth DualSense (VID `054C`, PID `0CE6`) discovery through Microsoft `HidBth`: succeeded with 78-byte input and 547-byte output/feature capabilities.
- Bluetooth microphone start/stop sequence: 938 media reports accepted over 10 seconds; the final `0xFE` disable report was accepted.
- Windows Raw Input Bluetooth capture: 1,000 microphone Opus reports received, 1,000 decoded, 480,000 mono PCM samples at 48 kHz (exactly 10.00 seconds).
- Native HID read cross-check: the same 1,000 microphone reports were received and decoded; Raw Input remains the app path because it coexists cleanly with Windows game-input consumers.
- Bluetooth microphone PCM energy during the final hardware pass: 300,007 average mean-square value, confirming non-silent audio.
- Bluetooth capture integration build: succeeded with 0 warnings and 0 errors.
- Application-component E2E: captured 695 frames / 6.95 seconds from the Bluetooth controller (mean-square energy 525,047), wrote WAV, resampled to 16 kHz, and returned a non-empty Japanese Whisper transcript.
- Physical microphone-button E2E: the first button press was detected from Bluetooth input report `0x31` (`buttons[2]`, bit 2) and started capture; the second press stopped capture and disabled the microphone stream.
- Physical-button speech pass: captured 344 Opus frames / 3.44 seconds (mean-square energy 31,278,839) and Whisper returned `テスト!`.
- Muted-state behavior: no media pump or PCM writer runs before the first button press or after the second press. Standard `0x31` mute-LED output is intentionally not mixed into the proprietary Bluetooth audio session because it prevents microphone frames on the tested controller.
- WinMM joystick coexistence after a clean Bluetooth reconnect: 494 audio frames / 4.94 seconds captured while 534 neutral game-controller samples reported 0.0000 maximum axis delta, 0 button samples, and 0 read errors.
- The coexistence probe now opens the controller through DirectInput in background/non-exclusive mode and samples DirectInput and WinMM concurrently while Bluetooth audio is active. Its Release build succeeds with 0 warnings and 0 errors; a connected-controller run is still pending.
- `run-coexistence-test.ps1` now enforces the Baseline/Steam/FF14 process preconditions, waits for the physical controller, preserves a timestamped report, propagates probe failures, and accepts only the exact passing WinMM/DirectInput/audio result. Its disconnected-controller failure path exits without launching or leaving a probe process.
- Combined USB-standard-audio/Bluetooth-direct-input application build: succeeded with 0 warnings and 0 errors. USB capture and physical-button behavior still require a USB-connected hardware pass.
- A dedicated USB hardware probe now uses the app's exact active-endpoint filter, WASAPI capture, USB Raw Input button monitor, and two-press interaction, then rejects empty, shorter-than-0.25-second, or silent WAV data. A USB-connected run is still pending.
- Self-contained application startup after the physical-button and auto-reconnect changes: stayed running for 5 seconds with the Bluetooth DualSense connected, then closed cleanly with exit code 0.
- Development MSIX rebuild under Windows PowerShell 5.1: succeeded after explicit UTF-8 manifest handling; MakeAppx unpacked the resulting package successfully.
- The WACK and coexistence scripts resolve repository-relative default paths after parameter binding, avoiding the empty-`PSScriptRoot` default-expression failure reproduced under Windows PowerShell 5.1. The WACK non-administrator guard and coexistence disconnected-controller failure both return exit code 1 cleanly.
- Protocol self-test: Bluetooth button press/release, Bluetooth audio-report exclusion, USB button press/release, and transport-layout separation all passed.
- Single-instance test: a second launch exited with code 0, left exactly the original process running, and the original process then closed cleanly with code 0. This prevents two app instances from writing competing Bluetooth microphone clocks.
- Accessibility startup/UI Automation test: device selection, refresh, transcript editing, and automatic-paste controls expose Japanese accessible names or help text; the published app stayed running and closed cleanly. Invoking refresh through UI Automation produced `LIVE_REGION_PASS|StatusText|DualSenseが見つかりません。接続して再読込してください`, proving that status changes raise the screen-reader `LiveRegionChanged` event with the changed message as its accessible name.
- Keyboard focus, hover, pressed, disabled, indeterminate download, and Windows high-contrast visual states are implemented. Normal-theme foreground/background contrast is at least 4.64:1 for interactive button text and 6.79:1 for secondary body text.
- Whisper base model integrity: the downloader's 147,951,465-byte model on the test machine matched pinned SHA-256 `60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE`; protocol self-test covers valid, truncated, and hash-mismatch decisions.
- Distribution licensing: complete NAudio, Concentus/Opus, HidSharp, Whisper.net, whisper.cpp/ggml, OpenAI Whisper, and self-contained .NET license/third-party notices are included in the publish output and MSIX payload.

## Not available in this environment

- Windows App Certification Kit is not installed on this machine. The Microsoft-signed 10.0.28000.2705 installer and the WACK-only offline layout were verified, and the main/Appx dependency MSIs were administratively extracted without system installation. `appcert.exe` correctly requires an elevated active-user session; UAC elevation was not granted during this pass. The repository now includes `run-wack.ps1` for the final elevated certification run.
- The development MSIX uses placeholder identity `DualSenseVoice.Dev`. A Store submission build requires the exact Identity and Publisher strings from the owner's Partner Center account.
- Automated installation was not forced because Windows requires an interactive security confirmation before trusting a self-signed root certificate. No test certificate, package, or process was left installed.
- The new DirectInput probe has not yet completed a connected-controller hardware run. Steam Input and a live FF14 client have also not yet been exercised; the successful historical WinMM pass covers one Windows joystick path but not every input API, overlay, remapping, vibration, or output-writing interaction.
- The mid-recording disconnect recovery path builds successfully and preserves the completed WAV before reconnecting, but a forced-disconnect hardware pass of that new recovery path is still pending.
