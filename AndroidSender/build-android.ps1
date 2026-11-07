# Build the Android sender with Android Studio's installed SDK and JDK.
# This script fetches unmodified Gradle 8.9 from the official Gradle distribution site,
# because a Gradle wrapper JAR is not bundled in this source-only release.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $env:JAVA_HOME) {
    $possibleJbr = @(
        'C:\Program Files\Android\Android Studio\jbr',
        'C:\Program Files\Android\Android Studio\jre'
    )
    foreach ($candidate in $possibleJbr) {
        if (Test-Path (Join-Path $candidate 'bin\java.exe')) {
            $env:JAVA_HOME = $candidate
            break
        }
    }
}

if (-not $env:ANDROID_HOME) {
    $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path $sdk) { $env:ANDROID_HOME = $sdk }
}

$cache = Join-Path $env:LOCALAPPDATA 'PhoneToPC-Audio\build-tools'
$gradle = Join-Path $cache 'gradle-8.9\bin\gradle.bat'
if (-not (Test-Path $gradle)) {
    New-Item -Path $cache -ItemType Directory -Force | Out-Null
    $archive = Join-Path $cache 'gradle-8.9-bin.zip'
    Write-Host 'Downloading official Gradle 8.9 distribution...'
    Invoke-WebRequest 'https://services.gradle.org/distributions/gradle-8.9-bin.zip' -OutFile $archive
    Write-Host 'Extracting Gradle...'
    Expand-Archive -Path $archive -DestinationPath $cache -Force
    Remove-Item $archive
}

if (-not (Test-Path $gradle)) { throw 'Gradle 8.9 extraction failed.' }
Write-Host 'Building Android debug APK...'
& $gradle --no-daemon --project-dir $PSScriptRoot :app:assembleDebug
if ($LASTEXITCODE -ne 0) { throw "Gradle build failed with exit code $LASTEXITCODE" }
Write-Host "`nAPK: $PSScriptRoot\app\build\outputs\apk\debug\app-debug.apk"
