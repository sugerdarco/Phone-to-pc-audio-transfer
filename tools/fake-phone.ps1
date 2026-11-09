# Test sender: pretends to be the phone and streams a 440 Hz tone to the receiver.
#   powershell -ExecutionPolicy Bypass -File tools\fake-phone.ps1 [-Target 127.0.0.1] [-Seconds 5] [-LossPercent 0]
# -LossPercent drops that share of packets at random, to exercise the loss repair.
param([string]$Target = "127.0.0.1", [int]$Seconds = 5, [int]$LossPercent = 0, [switch]$DiscoverOnly)
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = 1000

$q = [Text.Encoding]::ASCII.GetBytes("PAUD")
[void]$udp.Send($q, $q.Length, $Target, 45999)
try {
    $ep = New-Object System.Net.IPEndPoint ([Net.IPAddress]::Any), 0
    $r = $udp.Receive([ref]$ep)
    "Discovery reply from $($ep.Address): $([Text.Encoding]::UTF8.GetString($r))"
} catch { "No discovery reply" }
if ($DiscoverOnly) { return }

$chunk = 480                       # 2.5 ms of 48 kHz stereo s16
$tone = New-Object byte[] (48000 * 4)   # 1 s, 440 whole cycles -> loops cleanly
for ($i = 0; $i -lt 48000; $i++) {
    $v = [int16]([Math]::Sin(2 * [Math]::PI * 440 * $i / 48000) * 6000)
    $b0 = [byte]($v -band 0xFF); $b1 = [byte](($v -shr 8) -band 0xFF)
    $tone[$i*4] = $b0; $tone[$i*4+1] = $b1; $tone[$i*4+2] = $b0; $tone[$i*4+3] = $b1
}
$pkt = New-Object byte[] (8 + 2 * $chunk)
[Text.Encoding]::ASCII.GetBytes("PAU2").CopyTo($pkt, 0)
$rand = New-Object Random
$sw = [Diagnostics.Stopwatch]::StartNew()
$seq = 0; $dropped = 0
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    while ($seq * 2.5 -le $sw.ElapsedMilliseconds) {
        [BitConverter]::GetBytes([uint32]$seq).CopyTo($pkt, 4)
        [Array]::Copy($tone, ($seq % 400) * $chunk, $pkt, 8, $chunk)
        $old = $seq - 8; if ($old -lt 0) { $old = 0 }
        [Array]::Copy($tone, ($old % 400) * $chunk, $pkt, 8 + $chunk, $chunk)
        if ($rand.Next(100) -ge $LossPercent) { [void]$udp.Send($pkt, $pkt.Length, $Target, 45999) } else { $dropped++ }
        $seq++
    }
    Start-Sleep -Milliseconds 1
}
"Sent $seq packets, dropped $dropped on purpose"
