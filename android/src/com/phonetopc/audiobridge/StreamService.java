package com.phonetopc.audiobridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.drawable.Icon;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.os.Process;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;

/**
 * Captures phone audio (or the mic) and streams it to the PC as uncompressed PCM over UDP
 * (Wi-Fi, hotspot or USB tethering).
 * Packet: "PAU2" | uint32 LE seq | 480 bytes s16le 48 kHz stereo (2.5 ms) | the chunk of seq-8.
 */
public class StreamService extends Service {
    public static final String ACTION_START = "com.phonetopc.audiobridge.START";
    public static final String ACTION_STOP = "com.phonetopc.audiobridge.STOP";
    public static final String EXTRA_HOST = "host";
    public static final String EXTRA_MIC = "mic";
    public static final String EXTRA_MUTE = "mute";
    public static final String EXTRA_RESULT_CODE = "resultCode";
    public static final String EXTRA_RESULT_DATA = "resultData";

    public static final int PORT = 45999;
    private static final int SAMPLE_RATE = 48000;
    private static final int CHUNK_BYTES = 480; // 2.5 ms of 48 kHz stereo 16-bit
    private static final int REDUNDANCY = 8;    // each packet also carries the chunk from 20 ms earlier
    private static final String CHANNEL_ID = "stream";
    private static final int NOTIFICATION_ID = 1;

    /** Read by the activity to show live state. */
    public static volatile boolean running;
    public static volatile String status = "Idle";

    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile boolean active;
    private Thread worker;
    private MediaProjection projection;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private String host = "";
    private int savedVolume = -1;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || !ACTION_START.equals(intent.getAction())) {
            shutdown("Stopped");
            return START_NOT_STICKY;
        }
        stopWorker();

        host = intent.getStringExtra(EXTRA_HOST);
        boolean mic = intent.getBooleanExtra(EXTRA_MIC, false);
        boolean muteSpeaker = intent.getBooleanExtra(EXTRA_MUTE, true) && !mic;
        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        Intent resultData = Build.VERSION.SDK_INT >= 33
                ? intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent.class)
                : intent.<Intent>getParcelableExtra(EXTRA_RESULT_DATA);

        // Android 14+: must be in the foreground with the right type BEFORE getMediaProjection().
        Notification n = buildNotification("Connecting to " + host + "…");
        try {
            if (mic) {
                startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            } else {
                try {
                    startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                            | ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
                } catch (RuntimeException e) {
                    startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
                }
            }
        } catch (RuntimeException e) {
            shutdown("Could not start: " + e.getMessage());
            return START_NOT_STICKY;
        }

        if (!mic) {
            if (resultData == null) {
                shutdown("Capture permission missing");
                return START_NOT_STICKY;
            }
            MediaProjectionManager mpm = getSystemService(MediaProjectionManager.class);
            try {
                projection = mpm.getMediaProjection(resultCode, resultData);
            } catch (RuntimeException e) {
                projection = null;
            }
            if (projection == null) {
                shutdown("Could not start audio capture");
                return START_NOT_STICKY;
            }
            final MediaProjection mine = projection;
            projection.registerCallback(new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    // Ignore if we stopped it ourselves or a newer session replaced it.
                    if (projection == mine) shutdown("Capture ended by the system");
                }
            }, main);
        }

        acquireLocks();
        if (muteSpeaker) muteSpeaker();
        running = true;
        active = true;
        status = "Starting…";
        final boolean useMic = mic;
        final String target = host;
        worker = new Thread(new Runnable() {
            @Override
            public void run() {
                streamLoop(target, useMic);
            }
        }, "audio-stream");
        worker.start();
        return START_NOT_STICKY;
    }

    private void streamLoop(String target, boolean mic) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);
        AudioRecord rec = null;
        DatagramSocket socket = null;
        String endStatus = null;
        try {
            AudioFormat fmt = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                    .build();
            int minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT);
            int bufSize = Math.max(minBuf * 4, SAMPLE_RATE * 4 / 10); // >= 100 ms so a busy CPU never drops audio

            AudioRecord.Builder b = new AudioRecord.Builder().setAudioFormat(fmt).setBufferSizeInBytes(bufSize);
            if (mic) {
                b.setAudioSource(MediaRecorder.AudioSource.MIC);
            } else {
                AudioPlaybackCaptureConfiguration cfg = new AudioPlaybackCaptureConfiguration.Builder(projection)
                        .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                        .addMatchingUsage(AudioAttributes.USAGE_GAME)
                        .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                        .build();
                b.setAudioPlaybackCaptureConfig(cfg);
            }
            rec = b.build();
            if (rec.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("audio recorder did not initialize");
            }

            InetAddress addr = InetAddress.getByName(target);
            socket = new DatagramSocket();
            socket.setTrafficClass(0xB8); // DSCP EF: ask Wi-Fi for voice-priority queueing
            socket.setSendBufferSize(64 * 1024);

            // Packet: "PAU2" | seq | chunk[seq] | chunk[seq - 8]  (the old copy repairs lost packets)
            byte[] pkt = new byte[8 + 2 * CHUNK_BYTES];
            pkt[0] = 'P'; pkt[1] = 'A'; pkt[2] = 'U'; pkt[3] = '2';
            byte[][] history = new byte[REDUNDANCY][CHUNK_BYTES];
            DatagramPacket dp = new DatagramPacket(pkt, pkt.length, addr, PORT);

            rec.startRecording();
            int seq = 0;
            long bytesSent = 0;
            long lastReport = System.nanoTime();
            boolean netError = false;
            String label = mic ? "microphone" : "phone audio";
            status = "Streaming " + label + " to " + target;
            updateNotification("Streaming " + label + " to " + target);

            while (active) {
                int got = 0;
                while (got < CHUNK_BYTES && active) {
                    int n = rec.read(pkt, 8 + got, CHUNK_BYTES - got, AudioRecord.READ_BLOCKING);
                    if (n < 0) throw new IllegalStateException("audio read error " + n);
                    got += n;
                }
                if (!active) break;
                pkt[4] = (byte) seq;
                pkt[5] = (byte) (seq >>> 8);
                pkt[6] = (byte) (seq >>> 16);
                pkt[7] = (byte) (seq >>> 24);
                byte[] old = history[seq % REDUNDANCY];          // chunk from 8 packets (20 ms) ago
                System.arraycopy(old, 0, pkt, 8 + CHUNK_BYTES, CHUNK_BYTES);
                System.arraycopy(pkt, 8, old, 0, CHUNK_BYTES);   // remember this chunk
                seq++;
                try {
                    socket.send(dp);
                    bytesSent += pkt.length;
                    if (netError) {
                        netError = false;
                        status = "Streaming " + label + " to " + target;
                    }
                } catch (java.io.IOException e) {
                    // Wi-Fi hiccup / network change: keep capturing, retry on next chunk.
                    if (!netError) status = "Network unavailable, retrying…";
                    netError = true;
                }
                long now = System.nanoTime();
                if (now - lastReport >= 1_000_000_000L && !netError) {
                    long kbps = bytesSent * 8 / 1000 * 1_000_000_000L / (now - lastReport);
                    status = "Streaming " + label + " to " + target + "  ·  " + kbps + " kbit/s";
                    bytesSent = 0;
                    lastReport = now;
                }
            }
        } catch (Exception e) {
            endStatus = "Error: " + e.getMessage();
        } finally {
            if (rec != null) {
                try { rec.stop(); } catch (Exception ignored) { }
                rec.release();
            }
            if (socket != null) socket.close();
        }
        if (endStatus != null) {
            final String s = endStatus;
            final Thread me = Thread.currentThread();
            main.post(new Runnable() {
                @Override
                public void run() {
                    if (worker == me) shutdown(s);
                }
            });
        }
    }

    private void stopWorker() {
        active = false;
        Thread t = worker;
        worker = null;
        if (t != null && t != Thread.currentThread()) {
            try { t.join(1000); } catch (InterruptedException ignored) { }
        }
        MediaProjection p = projection;
        projection = null;
        if (p != null) {
            try { p.stop(); } catch (Exception ignored) { }
        }
        releaseLocks();
        restoreSpeaker();
    }

    /**
     * Silences the phone's own speaker/headphones. Android feeds the capture at full level
     * independent of the media volume, so the PC still gets the complete signal.
     */
    private void muteSpeaker() {
        try {
            AudioManager am = getSystemService(AudioManager.class);
            int v = am.getStreamVolume(AudioManager.STREAM_MUSIC);
            if (v > 0) {
                savedVolume = v;
                am.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0);
            }
        } catch (Exception ignored) {
        }
    }

    private void restoreSpeaker() {
        if (savedVolume < 0) return;
        try {
            AudioManager am = getSystemService(AudioManager.class);
            // Only restore if the user didn't change the volume themselves while streaming.
            if (am.getStreamVolume(AudioManager.STREAM_MUSIC) == 0) {
                am.setStreamVolume(AudioManager.STREAM_MUSIC, savedVolume, 0);
            }
        } catch (Exception ignored) {
        }
        savedVolume = -1;
    }

    private void shutdown(String finalStatus) {
        stopWorker();
        running = false;
        status = finalStatus;
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        stopWorker();
        running = false;
        super.onDestroy();
    }

    private void acquireLocks() {
        PowerManager pm = getSystemService(PowerManager.class);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioBridge:stream");
        wakeLock.setReferenceCounted(false);
        wakeLock.acquire(12 * 60 * 60 * 1000L);
        WifiManager wm = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
        if (wm != null) {
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "AudioBridge:wifi");
            wifiLock.setReferenceCounted(false);
            wifiLock.acquire();
        }
    }

    private void releaseLocks() {
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        wakeLock = null;
        if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        wifiLock = null;
    }

    private Notification buildNotification(String text) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm.getNotificationChannel(CHANNEL_ID) == null) {
            NotificationChannel ch = new NotificationChannel(CHANNEL_ID, "Audio streaming",
                    NotificationManager.IMPORTANCE_LOW);
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
        PendingIntent open = PendingIntent.getActivity(this, 0,
                new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop = PendingIntent.getService(this, 1,
                new Intent(this, StreamService.class).setAction(ACTION_STOP),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder nb = new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle("Audio Bridge")
                .setContentText(text)
                .setContentIntent(open)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_SERVICE)
                .addAction(new Notification.Action.Builder(
                        Icon.createWithResource(this, R.drawable.ic_stat), "Stop", stop).build());
        if (Build.VERSION.SDK_INT >= 31) {
            nb.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE);
        }
        return nb.build();
    }

    private void updateNotification(String text) {
        if (!active) return;
        getSystemService(NotificationManager.class).notify(NOTIFICATION_ID, buildNotification(text));
    }
}
