# Audio Bridge

Play your Android phone's audio on a Windows 11 PC over Wi-Fi or a USB cable with low latency.
The audio is sent uncompressed and plays back on the PC exactly as captured.

| Part | What it is | Size |
|------|------------|------|
| `android/` | Native Android app (Java, Android SDK only, no libraries). Captures phone audio with `AudioPlaybackCapture`, or the microphone. Android 10 to 16. | ~30 KB APK |
| `windows/` | Windows receiver (C#, WinForms, built-in .NET Framework 4.8, WinMM output). Doesn't need an installer or runtime. | ~50 KB EXE |

## Download

Get **AudioBridge.apk** and **AudioBridge.exe** from the [latest release](../../releases/tag/latest).
Every push to `main` rebuilds both. Update both apps together.

## Use

1. Run `AudioBridge.exe` on the PC.
2. Install `AudioBridge.apk` on the phone.
3. Connect them in one of two ways:
   - **Wi-Fi:** put both on the same network.
   - **USB cable:** plug in, then on the phone turn on **Settings > Network > Hotspot & tethering > USB tethering**.
     The app's "Use USB cable instead of Wi-Fi" button opens that screen. You don't need USB debugging.
4. In the app, tap **Find PC**, then **Start streaming**. When asked, choose **Entire screen**; only audio is sent.

If **Find PC** doesn't find the PC, especially over USB, click **Allow through Windows Firewall** in the PC app once.
Windows treats USB tethering as a new "Public" network and blocks the app there by default.

## Audio quality

- The audio is 48 kHz / 16-bit stereo PCM, uncompressed (about 1.5 Mbit/s). By comparison, Bluetooth LDAC is lossy, with a maximum of 990 kbit/s, and SBC is lower still.
- **No lost audio:** every packet also carries a copy of the audio from 20 ms earlier. Anything still missing is re-requested from the phone, which keeps the last 2.5 s, while there's still time before it plays. The PC window shows the counts: repaired, resent, lost and dropouts.
- **No clicks from clock drift:** the phone and PC clocks never run at exactly the same speed. Instead of dropping samples, the PC uses a 64-tap windowed-sinc resampler and adjusts speed by at most ±0.05%, which you can't hear.
- **Clean output:** the PC sends 32-bit float to Windows, from a "Pro Audio" priority thread with a 120 ms device queue.
- Offline test (`tools/sim.cs`): 5% packet loss, Wi-Fi stalls and ±300 ppm clock drift gave 0 dropouts and a noise floor of about −90 dB (the 16-bit limit).
- **Mute phone speaker while streaming** sets the phone's media volume to 0 and restores it when you stop. The PC still gets full volume, because Android captures the audio before the phone's volume is applied.
- The **Buffer** setting on the PC: **Music, 300 ms** (the default and the smoothest), Balanced 150 ms, or Low delay 60 ms (for video over USB).
- Apps that opt out of capture stay silent. That includes some DRM video apps and calls. This is an Android restriction.

## Protocol

UDP port **45999**, little-endian:

- Audio: `"PAU2"` + `uint32 seq` + 480 bytes PCM (2.5 ms, s16le 48 kHz stereo) + the 480 bytes of packet `seq-8`
- Resend request (PC to phone): `"PAUN"` + `uint16 count` + `count × uint32 seq`. Keep-alive (PC to phone): `"PAUK"`
- Discovery: the phone broadcasts `"PAUD"` on every interface; the PC replies `"PAUH" + computer name`

`tools/fake-phone.ps1 [-LossPercent 10]` sends a test tone to the receiver without a phone.

## Build it yourself

- Windows: run `windows\build.bat`. It uses the C# compiler that ships with Windows.
- Android: run `android/build.sh`. It needs JDK 17 and an Android SDK with `platforms;android-36` and build-tools. It doesn't use Gradle.
  The APK is signed with `android/signing.p12` (password `audiobridge`), so newer builds install over older ones.
