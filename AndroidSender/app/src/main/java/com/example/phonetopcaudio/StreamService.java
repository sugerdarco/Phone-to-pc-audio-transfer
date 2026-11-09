package com.example.phonetopcaudio;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.concurrent.atomic.AtomicBoolean;

public class StreamService extends Service {
    public static final String ACTION_START = "ACTION_START";
    public static final String ACTION_STOP = "ACTION_STOP";
    private static final String CHANNEL_ID = "PhoneToPCChannel";

    public static final int SAMPLE_RATE = 48000;
    public static final int CHANNELS = AudioFormat.CHANNEL_IN_STEREO;
    public static final int ENCODING = AudioFormat.ENCODING_PCM_16BIT;
    private static final int PCM_PACKET_SIZE = 1920;

    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private Thread workerThread;
    private MediaProjection mediaProjection;
    private PowerManager.WakeLock wakeLock;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || ACTION_STOP.equals(intent.getAction())) {
            stopStreaming();
            stopForeground(true);
            stopSelf();
            return START_NOT_STICKY;
        }

        boolean useInternal = intent.getBooleanExtra("internal", true);
        String ip = intent.getStringExtra("ip");
        int port = intent.getIntExtra("port", 50005);
        int resultCode = intent.getIntExtra("resultCode", 0);
        Intent projData = intent.getParcelableExtra("data");

        Notification notification = createNotification("Streaming audio to " + ip + ":" + port);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            int fgType = useInternal
                    ? ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
                    : ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE;
            startForeground(1, notification, fgType);
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(1, notification);
        }

        stopStreaming();
        acquireWakeLock();

        if (useInternal && projData != null) {
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            mediaProjection = mpm.getMediaProjection(resultCode, projData);
            mediaProjection.registerCallback(new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    stopStreaming();
                    stopSelf();
                }
            }, null);
        }

        isRunning.set(true);
        workerThread = new Thread(() -> runAudioLoop(ip, port, useInternal), "AudioStreamThread");
        workerThread.setPriority(Thread.MAX_PRIORITY);
        workerThread.start();

        return START_NOT_STICKY;
    }

    private void runAudioLoop(String targetIp, int targetPort, boolean useInternal) {
        AudioRecord recorder = null;
        DatagramSocket socket = null;

        try {
            int minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNELS, ENCODING);
            int bufferSize = Math.max(minBuf * 2, PCM_PACKET_SIZE * 4);

            AudioFormat audioFormat = new AudioFormat.Builder()
                    .setEncoding(ENCODING)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(CHANNELS)
                    .build();

            if (useInternal && mediaProjection != null) {
                AudioPlaybackCaptureConfiguration config =
                        new AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
                                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                                .build();

                recorder = new AudioRecord.Builder()
                        .setAudioFormat(audioFormat)
                        .setBufferSizeInBytes(bufferSize)
                        .setAudioPlaybackCaptureConfig(config)
                        .build();
            } else {
                recorder = new AudioRecord(
                        MediaRecorder.AudioSource.MIC,
                        SAMPLE_RATE,
                        CHANNELS,
                        ENCODING,
                        bufferSize
                );
            }

            socket = new DatagramSocket();
            socket.setTrafficClass(0x10);
            InetAddress address = InetAddress.getByName(targetIp);

            byte[] pcmBuffer = new byte[PCM_PACKET_SIZE];
            recorder.startRecording();

            while (isRunning.get()) {
                int read = recorder.read(pcmBuffer, 0, pcmBuffer.length);
                if (read > 0) {
                    DatagramPacket packet = new DatagramPacket(pcmBuffer, read, address, targetPort);
                    socket.send(packet);
                }
            }
        } catch (Exception e) {
            e.printStackTrace();
        } finally {
            if (recorder != null) {
                try {
                    recorder.stop();
                    recorder.release();
                } catch (Exception ignored) {}
            }
            if (socket != null) {
                socket.close();
            }
        }
    }

    private void stopStreaming() {
        isRunning.set(false);
        if (workerThread != null) {
            try {
                workerThread.join(500);
            } catch (InterruptedException ignored) {}
            workerThread = null;
        }
        if (mediaProjection != null) {
            try {
                mediaProjection.stop();
            } catch (Exception ignored) {}
            mediaProjection = null;
        }
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
    }

    private void acquireWakeLock() {
        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneToPC::AudioWakeLock");
        wakeLock.acquire(4 * 60 * 60 * 1000L);
    }

    private Notification createNotification(String text) {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID, "Audio Stream", NotificationManager.IMPORTANCE_LOW);
        nm.createNotificationChannel(channel);

        return new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("PhoneToPC Audio")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .build();
    }

    @Override
    public void onDestroy() {
        stopStreaming();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
