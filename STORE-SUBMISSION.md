# Microsoft Store submission checklist

This file collects the copy-ready Partner Center fields and the remaining owner-only gates. It is not a claim that the app has already been submitted.

## Product setup

- Product name: `DualSense Voice`
- Suggested category: `Productivity`
- Supported device family: Windows Desktop only
- Minimum OS: Windows 11 (`10.0.22000.0`)
- Architecture: x64
- Pricing and markets: owner decision in Partner Center
- Age rating: complete the IARC questionnaire in Partner Center; the app contains no supplied media, social features, purchases, gambling, violence, or user-generated content.

## Support and privacy

- Website: `https://github.com/tkitauji/dualsense-voice-input`
- Support URL: `https://github.com/tkitauji/dualsense-voice-input/issues`
- Privacy policy URL: `https://github.com/tkitauji/dualsense-voice-input/blob/main/PRIVACY.md`
- Privacy summary: microphone audio and transcripts stay on the PC; no analytics, ads, or tracking; only the recognition model is downloaded.

## Package identity

Reserve the product name first, then copy the exact `Package/Identity/Name`, `Publisher`, and publisher display name from Partner Center into the build command documented in `README.md`. Do not upload a package built with the development identity `DualSenseVoice.Dev`.

## Restricted capability explanation

Paste this into Submission options for `runFullTrust`:

> DualSense Voice is a packaged WPF desktop application. Full trust is required to receive the user's physical DualSense microphone-button reports through Windows Raw Input, exchange user-mode HID reports needed for Bluetooth microphone audio, capture the Windows-standard USB microphone through WASAPI, and paste a transcript into the foreground desktop application after the user presses the physical button. The app does not install a driver, service, browser extension, or startup task; it does not request elevation; and microphone audio and transcripts remain on the device.

## Certification notes

Paste a concise version of the following for the tester:

1. Connect a Sony DualSense wireless controller by USB or Bluetooth.
2. Start the app and select `モデルを準備`. This downloads the 147,951,465-byte Whisper base model from Hugging Face and verifies SHA-256 before saving it.
3. Place the cursor in Notepad. Press the controller's physical microphone button once, speak Japanese, and press it again. The local transcript is pasted into Notepad.
4. The app intentionally does not illuminate the controller microphone LED over Bluetooth; ascending and descending sounds indicate start and stop.
5. No custom kernel driver is installed. USB capture uses the Windows audio endpoint. Bluetooth capture uses shared user-mode HID access.
6. The MSIX declares `Microsoft.VCLibs.140.00.UWPDesktop`; Microsoft Store supplies that framework dependency. The app selects the optimized Whisper CPU runtime only when AVX, AVX2, FMA, and F16C are present, and otherwise uses its packaged No-AVX runtime.

## Store listing

- Copy the Japanese description and features from `STORE-LISTING.ja-JP.md`.
- Supply at least one real desktop screenshot; four are recommended. PNG, landscape or portrait, at least 1366×768, and no more than 50 MB each.
- Suggested screenshot sequence: connected/muted home; microphone-on state; completed Japanese transcript; auto-paste result in Notepad.
- Suggested search terms: `DualSense`, `音声入力`, `文字起こし`, `Whisper`, `マイク`, `日本語`.
- What's new for 1.0.0: `DualSenseの物理マイクボタンで開始・終了できる、USB/Bluetooth対応のローカル日本語音声入力を追加しました。`

## Final gates

- [ ] Reserve the product and provide the exact Partner Center identity values.
- [ ] Build the final MSIX with those identity values and a new four-part version.
- [ ] Run `run-wack.ps1` from an Administrator PowerShell session against the final package and review the generated XML report.
- [ ] Complete Bluetooth coexistence passes with Steam Input plus live Monster Hunter Wilds gameplay, and with a live FF14 client.
- [ ] Complete the USB standard-microphone and physical-button hardware pass.
- [ ] Capture and upload at least one real 1366×768-or-larger screenshot.
- [ ] Run `run-store-preflight.ps1 -Mode Submission -PackagePath <final-msix>` and resolve every reported failure.
- [ ] Complete pricing, market availability, properties, IARC age rating, privacy URL, and support contact in Partner Center.
- [ ] Upload the final MSIX and submit it for certification.
