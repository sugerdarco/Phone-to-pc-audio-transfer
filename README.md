# Audio Bridge

Play your Android phone's audio on a Windows 11 PC over Wi-Fi with low latency.

| Part | What it is | Size |
|------|------------|------|
| `android/` | Native Android app (Java, Android SDK only, no libraries). Captures phone audio with `AudioPlaybackCapture`, or the microphone. Android 10 to 16. | ~30 KB APK |
| `windows/` | Windows receiver (C#, WinForms, built-in .NET Framework 4.8, WinMM output). Doesn't need an installer or runtime. | ~50 KB EXE |

## Download

Get **AudioBridge.apk** and **AudioBridge.exe** from the [latest release](../../releases/tag/latest).
Every push to `main` rebuilds both files.

## Use

1. Run `AudioBridge.exe` on the PC. When Windows Firewall asks, allow **Private networks**.
2. Install `AudioBridge.apk` on the phone. Allow installs from your browser or file manager if asked.
3. Put the phone and PC on the same Wi-Fi. The phone's hotspot or USB tethering also works.
4. In the app, tap **Find PC** (or type the address shown on the PC), then **Start streaming**.
   When Android asks, choose **Entire screen**. Only audio is sent; video is never recorded.

Tips
- Use the PC window's **Buffer** setting to trade latency for stability: Low 30 ms, Normal 60 ms, or Stable 120 ms.
- Sound keeps playing on the phone too, so turn the phone's volume down if you like.
- Apps that opt out of capture stay silent. That includes some DRM video apps and phone or VoIP calls. This is an Android restriction.
- To stop, use the app's button or the **Stop** action in the notification.

## Protocol

UDP port **45999**, little-endian:

- Audio: `"PAU1"` + `uint32 seq` + 960 bytes PCM (s16le, 48 kHz, stereo = 5 ms)
- Discovery: phone broadcasts `"PAUD"`, PC replies `"PAUH" + computer name`

`tools/fake-phone.ps1` sends a test tone to the receiver without a phone.

## Build it yourself

- Windows: run `windows\build.bat`. It uses the C# compiler that ships with Windows.
- Android: run `android/build.sh`. It needs JDK 17 and an Android SDK with `platforms;android-36` and build-tools. It doesn't use Gradle.
  The APK is signed with `android/signing.p12` (password `audiobridge`), so newer builds install over older ones.
  It's a project key, not a Play Store key.
