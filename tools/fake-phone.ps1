# Test sender: pretends to be the phone. Sends a 440 Hz tone to the receiver.
#   powershell -ExecutionPolicy Bypass -File tools\fake-phone.ps1 [-Target 127.0.0.1] [-Seconds 5]
param([string]$Target = "127.0.0.1", [int]$Seconds = 5, [switch]$DiscoverOnly)
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = 1000

# discovery
$q = [Text.Encoding]::ASCII.GetBytes("PAUD")
[void]$udp.Send($q, $q.Length, $Target, 45999)
try {
    $ep = New-Object System.Net.IPEndPoint ([Net.IPAddress]::Any), 0
    $r = $udp.Receive([ref]$ep)
    "Discovery reply from $($ep.Address): $([Text.Encoding]::UTF8.GetString($r))"
} catch { "No discovery reply" }
if ($DiscoverOnly) { return }

$frames = 240 # 5 ms @ 48 kHz
$pkt = New-Object byte[] (8 + $frames * 4)
[Text.Encoding]::ASCII.GetBytes("PAU1").CopyTo($pkt, 0)
# one second of 440 Hz stereo tone (440 whole cycles, so it loops cleanly)
$tone = New-Object byte[] (48000 * 4)
for ($i = 0; $i -lt 48000; $i++) {
    $v = [int16]([Math]::Sin(2 * [Math]::PI * 440 * $i / 48000) * 6000)
    $b0 = [byte]($v -band 0xFF); $b1 = [byte](($v -shr 8) -band 0xFF)
    $tone[$i*4] = $b0; $tone[$i*4+1] = $b1; $tone[$i*4+2] = $b0; $tone[$i*4+3] = $b1
}
$sw = [Diagnostics.Stopwatch]::StartNew()
$seq = 0
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    while ($seq * 5 -le $sw.ElapsedMilliseconds) {
        [BitConverter]::GetBytes([uint32]$seq).CopyTo($pkt, 4)
        [Array]::Copy($tone, ($seq % 200) * 960, $pkt, 8, 960)
        [void]$udp.Send($pkt, $pkt.Length, $Target, 45999)
        $seq++
    }
    Start-Sleep -Milliseconds 2
}
"Sent $seq packets"
