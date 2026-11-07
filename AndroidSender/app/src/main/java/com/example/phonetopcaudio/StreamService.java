package com.example.phonetopcaudio;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Build;
import android.os.Looper;
import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.security.SecureRandom;

public final class StreamService extends Service {
    public static final String EXTRA_HOST = "destinationHost";
    public static final String EXTRA_RESULT_CODE = "projectionResultCode";
    public static final String EXTRA_RESULT_DATA = "projectionResultData";
    private static final String CHANNEL_ID = "phone_audio_stream";
    private static final int NOTIFICATION_ID = 21;
    private static final int PORT = 45999;
    private static final int SAMPLE_RATE = 48000;
    private static final int FRAME_BYTES = 960; // 5 ms, 16-bit stereo, 48 kHz
    public static volatile boolean isRunning = false;
    private volatile boolean active = false;
    private MediaProjection projection;
    private AudioRecord recorder;
    private DatagramSocket socket;
    private Thread worker;

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || active) return START_NOT_STICKY;
        String host = intent.getStringExtra(EXTRA_HOST);
        Intent resultData;
        if (Build.VERSION.SDK_INT >= 33) {
            resultData = intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent.class);
        } else {
            resultData = intent.getParcelableExtra(EXTRA_RESULT_DATA);
        }
        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        if (host == null || resultData == null || resultCode == 0) {
            stopSelf();
            return START_NOT_STICKY;
        }
        try {
            showForegroundNotification(host);
            MediaProjectionManager manager = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            projection = manager.getMediaProjection(resultCode, resultData);
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() { stopSelf(); }
            }, new Handler(Looper.getMainLooper()));

            AudioPlaybackCaptureConfiguration captureConfig =
                new AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                    .build();
            AudioFormat audioFormat = new AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(SAMPLE_RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build();
            int minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            if (minBuffer < 0) throw new IllegalStateException("Stereo audio capture not supported");
            recorder = new AudioRecord.Builder()
                .setAudioFormat(audioFormat)
                .setAudioPlaybackCaptureConfig(captureConfig)
                .setBufferSizeInBytes(Math.max(minBuffer, FRAME_BYTES * 32))
                .build();
            if (recorder.getState() != AudioRecord.STATE_INITIALIZED)
                throw new IllegalStateException("Audio capture could not initialize");
            socket = new DatagramSocket();
            InetAddress destination = InetAddress.getByName(host);
            socket.connect(destination, PORT);
            recorder.startRecording();
            active = true;
            isRunning = true;
            worker = new Thread(() -> sendAudio(destination), "PhoneToPC sender");
            worker.start();
        } catch (Exception ex) {
            Log.e("PhoneToPC", "Could not start audio capture", ex);
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    private void showForegroundNotification(String host) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL_ID,
                "Audio streaming", NotificationManager.IMPORTANCE_LOW));
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent launch = PendingIntent.getActivity(this, 0, open,
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notice = new Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle("Sending phone audio to PC")
            .setContentText("Streaming to " + host + ":" + PORT)
            .setContentIntent(launch)
            .setOngoing(true)
            .build();
        startForeground(NOTIFICATION_ID, notice,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
    }

    private void sendAudio(InetAddress destination) {
        // Each UDP datagram: magic 'AWA1', session ID, increasing sequence,
        // 5 ms of little-endian PCM. Total packet is < 1000 B: avoids IP fragmentation.
        final int sessionId = new SecureRandom().nextInt();
        int sequence = 0;
        byte[] audio20ms = new byte[FRAME_BYTES * 4];
        byte[] packet = new byte[12 + FRAME_BYTES];
        packet[0] = 'A'; packet[1] = 'W'; packet[2] = 'A'; packet[3] = '1';
        ByteBuffer bb = ByteBuffer.wrap(packet).order(ByteOrder.LITTLE_ENDIAN);
        bb.putInt(4, sessionId);
        DatagramPacket datagram = new DatagramPacket(packet, packet.length, destination, PORT);
        try {
            while (active) {
                int filled = 0;
                while (filled < audio20ms.length && active) {
                    int count = recorder.read(audio20ms, filled, audio20ms.length - filled,
                                              AudioRecord.READ_BLOCKING);
                    if (count <= 0) throw new IllegalStateException("AudioRecord read error: " + count);
                    filled += count;
                }
                if (!active) break;
                for (int i = 0; i < 4; i++) {
                    bb.putInt(8, sequence++);
                    System.arraycopy(audio20ms, i * FRAME_BYTES, packet, 12, FRAME_BYTES);
                    socket.send(datagram);
                }
            }
        } catch (Exception ex) {
            if (active) Log.e("PhoneToPC", "Streaming interrupted", ex);
        } finally {
            new Handler(Looper.getMainLooper()).post(this::stopSelf);
        }
    }

    @Override public void onDestroy() {
        active = false;
        isRunning = false;
        if (socket != null) { socket.close(); socket = null; }
        if (recorder != null) {
            try { recorder.stop(); } catch (IllegalStateException ignored) { }
            // stop() normally wakes a blocked read; let the sender exit first.
            if (worker != null && worker != Thread.currentThread()) {
                try { worker.join(700); } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                }
            }
            recorder.release();
            recorder = null;
        }
        if (projection != null) {
            try { projection.stop(); } catch (Exception ignored) { }
            projection = null;
        }
        super.onDestroy();
    }
}
