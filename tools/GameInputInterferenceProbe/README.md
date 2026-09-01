# Game-input coexistence probe

This developer-only probe verifies that Bluetooth microphone capture can run
while Windows reads the same DualSense through both WinMM and DirectInput.
It opens DirectInput with `Background | NonExclusive`; it does not hide,
replace, or exclusively acquire the controller.

## Run

1. Connect the DualSense through Bluetooth.
2. Close DualSense Voice. Steam Input or the game under test may remain open.
3. Keep every stick, trigger, button, and D-pad direction neutral for the full
   seven-second test.
4. From the repository root, run:

   ```powershell
   dotnet run --project tools/GameInputInterferenceProbe -c Release
   ```

The probe samples a two-second baseline, starts the Bluetooth microphone, and
then samples WinMM and DirectInput concurrently for five seconds. It reports
axis changes, active controls, read errors, and decoded audio frames. A passing
result requires clean readings from both APIs and at least one decoded audio
frame. The temporary WAV file is deleted before exit.

This test verifies the two Windows input APIs used by many desktop games. The
repository wrapper additionally requires the real Steam game process (Monster
Hunter Wilds by default) or FF14 to be running for its named scenario. Manual
gameplay checks are still required because overlays, remapping, vibration, and
controller output traffic are not fully represented by neutral-state samples.
