# Verification report

Date: 2026-09-01  
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
- Combined USB-standard-audio/Bluetooth-direct-input application build: succeeded with 0 warnings and 0 errors. USB capture and physical-button behavior still require a USB-connected hardware pass.
- Self-contained application startup after the physical-button and auto-reconnect changes: stayed running for 5 seconds with the Bluetooth DualSense connected, then closed cleanly with exit code 0.
- Development MSIX rebuild under Windows PowerShell 5.1: succeeded after explicit UTF-8 manifest handling; MakeAppx unpacked the resulting package successfully.
- Protocol self-test: Bluetooth button press/release, Bluetooth audio-report exclusion, USB button press/release, and transport-layout separation all passed.
- Single-instance test: a second launch exited with code 0, left exactly the original process running, and the original process then closed cleanly with code 0. This prevents two app instances from writing competing Bluetooth microphone clocks.

## Not available in this environment

- Windows App Certification Kit is not installed on this machine.
- The development MSIX uses placeholder identity `DualSenseVoice.Dev`. A Store submission build requires the exact Identity and Publisher strings from the owner's Partner Center account.
- Automated installation was not forced because Windows requires an interactive security confirmation before trusting a self-signed root certificate. No test certificate, package, or process was left installed.
- Steam Input, DirectInput itself, and a live FF14 client have not yet been exercised; the successful WinMM probe covers one Windows joystick path but not every input API, overlay, or output-writing interaction.
- The mid-recording disconnect recovery path builds successfully and preserves the completed WAV before reconnecting, but a forced-disconnect hardware pass of that new recovery path is still pending.
