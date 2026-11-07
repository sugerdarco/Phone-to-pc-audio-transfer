# Phone → PC Audio (prototype, v1.0)

**Goal:** Stream permitted Android app audio to a Windows 11 PC while keeping all other Windows audio playing normally. This is a complete **source-code project**, not precompiled APK/EXE. It has not been device-tested in this environment.

## What you get

- `AndroidSender/`: a small Java/Android app (Android 10+, API 29+); captures allowed media/game audio with Android's `AudioPlaybackCapture` API after explicit system consent. Uses a foreground notification, no microphone or video transmission.
- `WindowsReceiver/`: a WinForms receiver (.NET 10) with Start/Stop, LAN IP display, input status, packet counter, and **independent volume control**.
- `Tools/send-test-tone.py`: optional protocol/network test without an Android phone.
- No cloud, accounts, advertisements, NuGet audio library, or audio-device rerouting. No Internet needed after the apps are built.

## How audio works

```
Android app's game/media audio
    ↓ Android AudioPlaybackCapture (only apps that permit capture)
AndroidSender foreground service
    ↓ Wi-Fi UDP 45999 (48 kHz, 16-bit, 2-channel PCM)
WindowsReceiver -> WinMM waveOut shared output -> Windows audio mixer -> speakers/headphones
                                                     ↑
                                Other PC apps continue playing normally
```

- Audio format: stereo 48,000 Hz 16-bit PCM, 1.536 Mbit/s excluding UDP overhead.
- Protocol: `AWA1` magic (4 bytes), signed LE session ID (4), signed LE increasing sequence (4), stereo PCM (960 bytes = 5 ms). 972 byte datagrams avoid typical MTU fragmentation.
- Receiver uses an 80 ms prefill jitter buffer plus six 20 ms native audio output buffers. End-to-end latency varies by device/network (approximately a few hundred milliseconds) and is **not zero-latency**.
- The Windows program uses standard shared audio playback. It does not change the default output device, master volume, or any other application's volume.
- PC audio output is to the **current default playback device when the receiver starts**. If you switch devices, stop and restart the Windows receiver.

## Build Windows receiver

1. Install the **.NET 10 SDK** on a Windows 11 x64 PC: https://dotnet.microsoft.com/download/dotnet/10.0
2. Open `WindowsReceiver/build-windows.bat` to publish a self-contained EXE (no .NET runtime needed on another PC). The EXE is under `WindowsReceiver/bin/Release/net10.0-windows/win-x64/publish/PhoneToPC.Receiver.exe`.
3. Or use `build-windows-small.bat` for a much smaller framework-dependent publish; install the **.NET 10 Desktop Runtime** on the PC if not present.
4. Launch the receiver. It listens on **UDP port 45999** and displays the PC's IPv4 address(es).

If Windows Firewall blocks inbound audio, open **PowerShell as Administrator** and run (restricted to private profiles/local subnet):

```powershell
New-NetFirewallRule -DisplayName "Phone to PC Audio" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45999 -Profile Private -RemoteAddress LocalSubnet
```

Use a **Private** network profile. Do not open this port to the Internet. If multiple IPv4 addresses appear, use the one associated with your real Wi-Fi/Ethernet adapter (`ipconfig` helps identify it).

## Build Android sender

1. Install [Android Studio](https://developer.android.com/studio) (SDK Platform **35**, JDK **17**; Android Studio includes a JDK). Use a real Android 10+ phone with USB debugging enabled if installing via Android Studio.
2. Run `AndroidSender/build-android.bat` on Windows. This fetches official Gradle 8.9 into `%LOCALAPPDATA%/PhoneToPC-Audio/build-tools/` if necessary, then compiles the APK using Android Studio's SDK. Internet is required for the initial Gradle/plugin dependencies.
3. Output: `AndroidSender/app/build/outputs/apk/debug/app-debug.apk`.
4. Install via `adb install -r app-debug.apk` or Android Studio's Run/Build APK flow, or copy the APK to your phone and enable installing from that source.

The standalone build script is included because this source archive does not bundle the standard Gradle wrapper JAR. The Android Studio project uses the Android Gradle Plugin 8.7.3 and Gradle 8.9.

## First connection

1. Connect the Android phone and Windows 11 PC to the same **trusted** Wi-Fi/LAN. Turn off any VPN/guest-network isolation that prevents peer-to-peer traffic.
2. Start `PhoneToPC.Receiver.exe` and note the **Windows IPv4 address** (example: `192.168.1.50`).
3. Open `Phone to PC Audio` on Android, enter that IPv4 address, tap **Start sending audio**, and grant Android's `Record audio` and screen-capture/media-projection consent. Android may describe this as sharing/recording the screen; **this app transmits audio only**.
4. Play a game or video in an Android app that permits playback capture. The PC's **Packets** number should increase and sound should emerge from the selected speakers/headphones.
5. Adjust **Phone volume** in the Windows receiver; Windows music/game/call volumes are unaffected. Press **Stop streaming** on Android to end the foreground service.

## Important limitations

- **Some Android audio cannot be captured.** Apps must allow Android's system playback capture. DRM/protected streams and many commercial media apps block it. Phone calls, VoIP call audio and microphone audio are not captured. The phone may continue playing the same audio locally.
- **Android system consent is mandatory.** No silent/hidden capture; restarting a capture may require fresh consent.
- **Wi-Fi required for this version.** No Bluetooth A2DP receiver, USB transport or Internet streaming.
- **No encryption or pairing in this prototype.** Anyone with access to the LAN path can potentially see the audio packets or send packets to the receiver. Use only a trusted LAN; never expose UDP port 45999 to the public Internet. Add encryption/authentication before using on untrusted networks.
- If your Windows app is using the output device in **exclusive mode**, normal mixed playback may not be available. Disable exclusive mode in that app if necessary.
- Some Android devices have different capture implementations; channel format, background restrictions or power-management settings can affect performance.
- There is no audio/video synchronization control; not suitable for competitive gaming where ultra-low latency is essential.

## Troubleshooting

- **No increase in packet count**: wrong Windows IPv4, Android not streaming, Windows Firewall, guest Wi-Fi/client isolation, VPN, or devices on different networks.
- **Packets increase but no sound**: Android source app blocks capture; test with a locally played, unprotected video/game. Also check Windows Volume Mixer, default playback device, and Phone volume slider.
- **Choppy sound**: improve Wi-Fi signal and disable aggressive Android battery optimization. This first version uses raw PCM for simplicity, so it needs ~2 Mbit/s including packet overhead.
- **Android service stops**: check Android notifications and Logcat (`PhoneToPC`) for errors; unlock the phone if OEM battery settings suspend streaming.
- **Test without phone**: On a device with Python, run `python Tools/send-test-tone.py <windows-ip>`; the Windows receiver should play a 3-second 440 Hz tone. Test only on a trusted network.

## Further improvements

A production version could add secure device pairing and encryption, a codec such as Opus to reduce bandwidth, auto-discovery, a system-tray receiver, and adjustable latency.

## References

- Android AudioPlaybackCapture: https://developer.android.com/media/platform/av-capture
- Android media projection foreground service: https://developer.android.com/media/grow/media-projection
- Microsoft waveOutOpen: https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutopen

## Windows EXE built in this chat

The separate `PhoneToPC-Audio-Windows.exe` is a small native Go alternative to the C# WinForms receiver in `WindowsReceiver/`. It uses the **same AWA1 UDP packet protocol**, `winmm.dll` shared-output playback, automatic start/stop and a native Windows window with stream-volume buttons. It requires no .NET runtime. Its source is in `WindowsReceiver/GoNativeReceiver/main.go`. The binary is cross-compiled for 64-bit Windows 11, but not yet run-tested on a Windows machine. It is not an installer: simply double-click the EXE.

To reproduce the executable on a PC with Go installed:

```powershell
cd WindowsReceiver/GoNativeReceiver
$env:GOOS='windows'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
go build -trimpath -ldflags='-H windowsgui -s -w' -o PhoneToPC-Audio-Windows.exe main.go
```

## Build Android APK without Android Studio (GitHub Actions)

If you do not have a local Android SDK, upload the contents of this project folder into your own private or public GitHub repository, including the hidden `.github/` directory. Go to **Actions → Build installable Android APK → Run workflow**. After a successful run, open the workflow run and download the **PhoneToPC-Android-APK** artifact ZIP; extract `app-debug.apk` and install it on the phone.

This workflow requires a GitHub account and a working remote build; it has **not been run or verified** in this environment. The resulting debug APK is installable for personal testing; it is not Play Store-signed. Do not download APKs from untrusted mirrors.
