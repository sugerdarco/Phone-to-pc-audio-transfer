#!/usr/bin/env bash
# Builds AudioBridge.apk with plain Android SDK tools (no Gradle, no Android Studio).
# Needs: JDK 17+, ANDROID_HOME with build-tools and platforms;android-36.
set -euo pipefail
cd "$(dirname "$0")"

API=36
VERSION_CODE="${VERSION_CODE:-1}"
VERSION_NAME="${VERSION_NAME:-1.0}"
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:?set ANDROID_HOME}}"
JAR="$SDK/platforms/android-$API/android.jar"
if [ ! -f "$JAR" ]; then
  yes | "$SDK/cmdline-tools/latest/bin/sdkmanager" "platforms;android-$API" >/dev/null
fi
BT="$SDK/build-tools/$(ls "$SDK/build-tools" | sort -V | tail -1)"
echo "Using $BT and $JAR"

OUT=build
rm -rf "$OUT"
mkdir -p "$OUT/gen" "$OUT/classes" "$OUT/dex"

# 1. resources + manifest -> base apk (and R.java)
"$BT/aapt2" compile --dir res -o "$OUT/res.zip"
"$BT/aapt2" link -o "$OUT/base.apk" -I "$JAR" \
  --manifest AndroidManifest.xml --java "$OUT/gen" \
  --min-sdk-version 29 --target-sdk-version $API \
  --version-code "$VERSION_CODE" --version-name "$VERSION_NAME" \
  "$OUT/res.zip"

# 2. java -> class
find src "$OUT/gen" -name '*.java' > "$OUT/sources.txt"
javac -source 8 -target 8 -encoding UTF-8 -Xlint:-options -Xlint:-deprecation \
  -bootclasspath "$JAR" -d "$OUT/classes" @"$OUT/sources.txt"

# 3. class -> dex
find "$OUT/classes" -name '*.class' > "$OUT/classes.txt"
"$BT/d8" --release --min-api 29 --lib "$JAR" --output "$OUT/dex" @"$OUT/classes.txt"

# 4. package, align, sign
cp "$OUT/base.apk" "$OUT/unsigned.apk"
(cd "$OUT/dex" && zip -q -X ../unsigned.apk classes.dex)
"$BT/zipalign" -f -p 4 "$OUT/unsigned.apk" "$OUT/aligned.apk"
"$BT/apksigner" sign --ks signing.p12 --ks-type PKCS12 --ks-pass pass:audiobridge \
  --ks-key-alias audiobridge --key-pass pass:audiobridge \
  --out "$OUT/AudioBridge.apk" "$OUT/aligned.apk"
"$BT/apksigner" verify --print-certs "$OUT/AudioBridge.apk" | head -3
ls -l "$OUT/AudioBridge.apk"
