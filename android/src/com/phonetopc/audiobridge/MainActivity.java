package com.phonetopc.audiobridge;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.media.projection.MediaProjectionConfig;
import android.media.projection.MediaProjectionManager;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public class MainActivity extends Activity {
    private static final int REQ_PERMISSIONS = 1;
    private static final int REQ_CAPTURE = 2;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private EditText hostInput;
    private RadioButton phoneAudio;
    private RadioButton micAudio;
    private Button startButton;
    private Button findButton;
    private TextView statusText;

    private final Runnable poll = new Runnable() {
        @Override
        public void run() {
            refresh();
            ui.postDelayed(this, 500);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences("settings", MODE_PRIVATE);

        final int pad = dp(20);
        final ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setPadding(pad, pad, pad, pad);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(root);
        scroll.setOnApplyWindowInsetsListener(new View.OnApplyWindowInsetsListener() {
            @Override
            public WindowInsets onApplyWindowInsets(View v, WindowInsets insets) {
                int l, t, r, b;
                if (Build.VERSION.SDK_INT >= 30) {
                    android.graphics.Insets i = insets.getInsets(
                            WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout() | WindowInsets.Type.ime());
                    l = i.left; t = i.top; r = i.right; b = i.bottom;
                } else {
                    l = insets.getSystemWindowInsetLeft();
                    t = insets.getSystemWindowInsetTop();
                    r = insets.getSystemWindowInsetRight();
                    b = insets.getSystemWindowInsetBottom();
                }
                v.setPadding(pad + l, pad + t, pad + r, pad + b);
                return insets;
            }
        });

        TextView title = new TextView(this);
        title.setText("Audio Bridge");
        title.setTextSize(28);
        title.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        root.addView(title);

        TextView subtitle = text("Play this phone's audio on your Windows PC.");
        subtitle.setAlpha(0.7f);
        root.addView(subtitle, margins(0, dp(2), 0, dp(24)));

        root.addView(label("PC address"));
        LinearLayout hostRow = new LinearLayout(this);
        hostRow.setOrientation(LinearLayout.HORIZONTAL);
        hostRow.setGravity(Gravity.CENTER_VERTICAL);
        hostInput = new EditText(this);
        hostInput.setHint("e.g. 192.168.1.20");
        hostInput.setSingleLine(true);
        hostInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        hostInput.setText(prefs.getString("host", ""));
        hostRow.addView(hostInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        findButton = new Button(this);
        findButton.setText("Find PC");
        findButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                findPc();
            }
        });
        hostRow.addView(findButton);
        root.addView(hostRow, margins(0, 0, 0, dp(20)));

        root.addView(label("Audio source"));
        RadioGroup source = new RadioGroup(this);
        phoneAudio = new RadioButton(this);
        phoneAudio.setId(View.generateViewId());
        phoneAudio.setText("Phone audio (music, video, games)");
        micAudio = new RadioButton(this);
        micAudio.setId(View.generateViewId());
        micAudio.setText("Microphone");
        source.addView(phoneAudio);
        source.addView(micAudio);
        if (prefs.getBoolean("mic", false)) micAudio.setChecked(true); else phoneAudio.setChecked(true);
        root.addView(source, margins(0, 0, 0, dp(24)));

        startButton = new Button(this);
        startButton.setTextSize(18);
        startButton.setMinHeight(dp(56));
        startButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                onStartStop();
            }
        });
        root.addView(startButton);

        statusText = text("");
        statusText.setTextSize(15);
        root.addView(statusText, margins(0, dp(16), 0, dp(24)));

        TextView help = text(
                "1. Open Audio Bridge on the Windows PC.\n" +
                "2. Connect both to the same Wi-Fi (or phone hotspot).\n" +
                "3. Tap Find PC, then Start, and choose “Entire screen” if asked.\n\n" +
                "Audio keeps playing on the phone too. Apps that block recording " +
                "(some DRM video apps, calls) stay silent on the PC.");
        help.setAlpha(0.6f);
        help.setTextSize(13);
        root.addView(help);

        setContentView(scroll);
    }

    @Override
    protected void onResume() {
        super.onResume();
        ui.post(poll);
    }

    @Override
    protected void onPause() {
        super.onPause();
        ui.removeCallbacks(poll);
    }

    private void refresh() {
        boolean running = StreamService.running;
        startButton.setText(running ? "Stop streaming" : "Start streaming");
        statusText.setText(StreamService.status);
        hostInput.setEnabled(!running);
        findButton.setEnabled(!running);
        phoneAudio.setEnabled(!running);
        micAudio.setEnabled(!running);
    }

    private void onStartStop() {
        if (StreamService.running) {
            startService(new Intent(this, StreamService.class).setAction(StreamService.ACTION_STOP));
            return;
        }
        String host = hostInput.getText().toString().trim();
        if (host.isEmpty()) {
            Toast.makeText(this, "Enter the PC address or tap Find PC", Toast.LENGTH_SHORT).show();
            return;
        }
        prefs.edit().putString("host", host).putBoolean("mic", micAudio.isChecked()).apply();

        List<String> missing = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.RECORD_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                && !prefs.getBoolean("askedNotif", false)) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
            prefs.edit().putBoolean("askedNotif", true).apply();
        }
        if (!missing.isEmpty()) {
            requestPermissions(missing.toArray(new String[0]), REQ_PERMISSIONS);
            return;
        }
        beginCapture();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != REQ_PERMISSIONS) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            beginCapture();
        } else {
            Toast.makeText(this, "Audio permission is needed to capture sound", Toast.LENGTH_LONG).show();
        }
    }

    private void beginCapture() {
        if (micAudio.isChecked()) {
            launchService(0, null);
            return;
        }
        MediaProjectionManager mpm = getSystemService(MediaProjectionManager.class);
        Intent consent = Build.VERSION.SDK_INT >= 34
                // Whole-device capture so audio from every app is included.
                ? mpm.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
                : mpm.createScreenCaptureIntent();
        startActivityForResult(consent, REQ_CAPTURE);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQ_CAPTURE) return;
        if (resultCode == RESULT_OK && data != null) {
            launchService(resultCode, data);
        } else {
            Toast.makeText(this, "Capture was cancelled", Toast.LENGTH_SHORT).show();
        }
    }

    private void launchService(int resultCode, Intent data) {
        Intent i = new Intent(this, StreamService.class)
                .setAction(StreamService.ACTION_START)
                .putExtra(StreamService.EXTRA_HOST, hostInput.getText().toString().trim())
                .putExtra(StreamService.EXTRA_MIC, micAudio.isChecked())
                .putExtra(StreamService.EXTRA_RESULT_CODE, resultCode)
                .putExtra(StreamService.EXTRA_RESULT_DATA, data);
        StreamService.status = "Starting…";
        startForegroundService(i);
        refresh();
    }

    // ---------- PC discovery: broadcast "PAUD", PC answers "PAUH<name>" ----------

    private void findPc() {
        findButton.setEnabled(false);
        statusText.setText("Searching for PCs on this network…");
        final List<InetAddress> targets = broadcastTargets();
        new Thread(new Runnable() {
            @Override
            public void run() {
                final Map<String, String> found = discover(targets);
                ui.post(new Runnable() {
                    @Override
                    public void run() {
                        findButton.setEnabled(!StreamService.running);
                        showDiscovery(found);
                    }
                });
            }
        }, "discovery").start();
    }

    private List<InetAddress> broadcastTargets() {
        List<InetAddress> out = new ArrayList<>();
        try {
            out.add(InetAddress.getByName("255.255.255.255"));
            ConnectivityManager cm = getSystemService(ConnectivityManager.class);
            for (Network net : cm.getAllNetworks()) {
                LinkProperties lp = cm.getLinkProperties(net);
                if (lp == null) continue;
                for (LinkAddress la : lp.getLinkAddresses()) {
                    InetAddress a = la.getAddress();
                    int prefix = la.getPrefixLength();
                    if (!(a instanceof Inet4Address) || a.isLoopbackAddress() || prefix <= 0 || prefix >= 31) continue;
                    byte[] b = a.getAddress();
                    int ip = ((b[0] & 0xff) << 24) | ((b[1] & 0xff) << 16) | ((b[2] & 0xff) << 8) | (b[3] & 0xff);
                    int bc = ip | (-1 >>> prefix);
                    out.add(InetAddress.getByAddress(new byte[]{
                            (byte) (bc >>> 24), (byte) (bc >>> 16), (byte) (bc >>> 8), (byte) bc}));
                }
            }
        } catch (Exception ignored) {
        }
        return out;
    }

    private static Map<String, String> discover(List<InetAddress> targets) {
        Map<String, String> found = new LinkedHashMap<>();
        DatagramSocket s = null;
        try {
            s = new DatagramSocket();
            s.setBroadcast(true);
            s.setSoTimeout(250);
            byte[] q = "PAUD".getBytes(StandardCharsets.US_ASCII);
            byte[] buf = new byte[512];
            long end = System.currentTimeMillis() + 1800;
            int round = 0;
            while (System.currentTimeMillis() < end) {
                if (round++ < 3) {
                    for (InetAddress t : targets) {
                        try {
                            s.send(new DatagramPacket(q, q.length, t, StreamService.PORT));
                        } catch (Exception ignored) {
                        }
                    }
                }
                try {
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    s.receive(p);
                    if (p.getLength() >= 4 && new String(buf, 0, 4, StandardCharsets.US_ASCII).equals("PAUH")) {
                        String name = new String(buf, 4, p.getLength() - 4, StandardCharsets.UTF_8);
                        found.put(p.getAddress().getHostAddress(), name);
                    }
                } catch (SocketTimeoutException ignored) {
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (s != null) s.close();
        }
        return found;
    }

    private void showDiscovery(Map<String, String> found) {
        if (found.isEmpty()) {
            statusText.setText("No PC found. Make sure Audio Bridge is open on Windows, both devices are on " +
                    "the same network, and Windows Firewall allows it. You can also type the address shown on the PC.");
            return;
        }
        statusText.setText(StreamService.status);
        if (found.size() == 1) {
            Map.Entry<String, String> e = found.entrySet().iterator().next();
            hostInput.setText(e.getKey());
            Toast.makeText(this, "Found " + e.getValue(), Toast.LENGTH_SHORT).show();
            return;
        }
        final String[] ips = found.keySet().toArray(new String[0]);
        String[] labels = new String[ips.length];
        for (int i = 0; i < ips.length; i++) labels[i] = found.get(ips[i]) + "  (" + ips[i] + ")";
        new AlertDialog.Builder(this)
                .setTitle("Choose your PC")
                .setItems(labels, (d, which) -> hostInput.setText(ips[which]))
                .show();
    }

    // ---------- small view helpers ----------

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }

    private TextView text(String s) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(15);
        return t;
    }

    private TextView label(String s) {
        TextView t = text(s);
        t.setTextSize(13);
        t.setAlpha(0.7f);
        t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    private static LinearLayout.LayoutParams margins(int l, int t, int r, int b) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.setMargins(l, t, r, b);
        return p;
    }
}
