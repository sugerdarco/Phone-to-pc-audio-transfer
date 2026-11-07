using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneToPC.Receiver;

internal sealed class ReceiverForm : Form
{
    private const int Port = 45999;
    private readonly JitterBuffer _audio = new();
    private readonly Label _status = new();
    private readonly Label _received = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly TrackBar _volumeSlider = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cancel;
    private Task? _receiveTask;
    private WavePlayer? _player;
    private int _volume = 70;
    private long _packetCount;
    private string _lastSender = "none yet";

    public ReceiverForm()
    {
        Text = "Phone to PC Audio";
        ClientSize = new Size(480, 335);
        MinimumSize = new Size(480, 335);
        MaximumSize = new Size(800, 430);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        var title = new Label { Text = "Android audio → Windows speakers", Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Location = new Point(18, 14) };
        var ip = new Label { Text = $"Windows IP: {GetLocalAddresses()}\nUDP port: {Port}", AutoSize = false, Size = new Size(445, 48), Location = new Point(19, 62) };
        _status.Text = "Receiver stopped";
        _status.Location = new Point(19, 125);
        _status.Size = new Size(440, 26);
        _received.Text = "Packets: 0";
        _received.Location = new Point(19, 156);
        _received.Size = new Size(440, 25);
        _start.Text = "Start receiver";
        _start.Location = new Point(19, 194);
        _start.Size = new Size(165, 35);
        _start.Click += (_, _) => StartReceiving();
        _stop.Text = "Stop";
        _stop.Location = new Point(196, 194);
        _stop.Size = new Size(105, 35);
        _stop.Enabled = false;
        _stop.Click += (_, _) => StopReceiving();
        var volumeLabel = new Label { Text = "Phone volume (Windows apps stay unchanged)", AutoSize = true, Location = new Point(19, 243) };
        _volumeSlider.Minimum = 0;
        _volumeSlider.Maximum = 100;
        _volumeSlider.Value = _volume;
        _volumeSlider.TickFrequency = 10;
        _volumeSlider.Location = new Point(18, 265);
        _volumeSlider.Size = new Size(430, 45);
        _volumeSlider.ValueChanged += (_, _) => Volatile.Write(ref _volume, _volumeSlider.Value);
        Controls.AddRange(new Control[] { title, ip, _status, _received, _start, _stop, volumeLabel, _volumeSlider });
        _statusTimer.Interval = 300;
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        FormClosing += (_, _) => StopReceiving();
        StartReceiving();
    }

    private void StartReceiving()
    {
        if (_udp is not null) return;
        try
        {
            _audio.Reset();
            Interlocked.Exchange(ref _packetCount, 0);
            _lastSender = "none yet";
            _udp = new UdpClient(Port);
            _cancel = new CancellationTokenSource();
            _player = new WavePlayer(_audio, () => Volatile.Read(ref _volume));
            _player.Start();
            UdpClient socket = _udp;
            CancellationToken token = _cancel.Token;
            _receiveTask = Task.Run(() => ReceiveLoop(socket, token));
            _start.Enabled = false;
            _stop.Enabled = true;
            _status.Text = "Listening – enter the Windows IP in Android";
        }
        catch (Exception ex)
        {
            StopReceiving();
            MessageBox.Show(this, $"Could not start receiver: {ex.Message}", "Phone to PC Audio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token);
                byte[] data = result.Buffer;
                if (data.Length != 12 + JitterBuffer.FrameBytes ||
                    data[0] != 'A' || data[1] != 'W' || data[2] != 'A' || data[3] != '1') continue;
                int sessionId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
                int sequence = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
                byte[] pcm = new byte[JitterBuffer.FrameBytes];
                Buffer.BlockCopy(data, 12, pcm, 0, pcm.Length);
                _audio.Add(sessionId, sequence, pcm);
                Interlocked.Increment(ref _packetCount);
                Volatile.Write(ref _lastSender, result.RemoteEndPoint.Address.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private void RefreshStatus()
    {
        if (_udp is null) return;
        if (_player?.Error is string error)
            _status.Text = "Audio device error: " + error;
        else
            _status.Text = "Receiving on port 45999 – from " + Volatile.Read(ref _lastSender);
        _received.Text = "Packets: " + Interlocked.Read(ref _packetCount).ToString("N0");
    }

    private void StopReceiving()
    {
        _cancel?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _player?.Dispose();
        _player = null;
        _cancel?.Dispose();
        _cancel = null;
        _audio.Reset();
        _start.Enabled = true;
        _stop.Enabled = false;
        _status.Text = "Receiver stopped";
    }

    private static string GetLocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct().ToArray();
            return addresses.Length == 0 ? "(not connected)" : string.Join(", ", addresses);
        }
        catch { return "Check ipconfig in Terminal"; }
    }
}
