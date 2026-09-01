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

## Not available in this environment

- Windows App Certification Kit is not installed on this machine.
- The development MSIX uses placeholder identity `DualSenseVoice.Dev`. A Store submission build requires the exact Identity and Publisher strings from the owner's Partner Center account.
- Automated installation was not forced because Windows requires an interactive security confirmation before trusting a self-signed root certificate. No test certificate, package, or process was left installed.

