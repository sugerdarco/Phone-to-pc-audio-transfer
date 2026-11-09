package com.example.phonetopcaudio;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;
import android.widget.Toast;

public class MainActivity extends Activity {
    private static final int REQ_PERMS = 100;
    private static final int REQ_PROJECTION = 101;

    private EditText ipInput;
    private EditText portInput;
    private RadioButton rbInternal;
    private RadioButton rbMic;
    private TextView statusText;
    private SharedPreferences prefs;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences("PhoneToPC", MODE_PRIVATE);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(64, 96, 64, 64);

        TextView title = new TextView(this);
        title.setText("PhoneToPC Audio (Android 15)");
        title.setTextSize(22f);
        root.addView(title);

        TextView ipLabel = new TextView(this);
        ipLabel.setText("\nWindows 11 PC IP Address:");
        root.addView(ipLabel);

        ipInput = new EditText(this);
        ipInput.setHint("e.g. 192.168.1.100");
        ipInput.setText(prefs.getString("ip", "192.168.1.100"));
        root.addView(ipInput);

        TextView portLabel = new TextView(this);
        portLabel.setText("\nUDP Port:");
        root.addView(portLabel);

        portInput = new EditText(this);
        portInput.setInputType(InputType.TYPE_CLASS_NUMBER);
        portInput.setText(String.valueOf(prefs.getInt("port", 50005)));
        root.addView(portInput);

        TextView modeLabel = new TextView(this);
        modeLabel.setText("\nAudio Source:");
        root.addView(modeLabel);

        RadioGroup modeGroup = new RadioGroup(this);
        rbInternal = new RadioButton(this);
        rbInternal.setText("Internal System Audio (Media/Games)");
        rbInternal.setChecked(true);
        rbMic = new RadioButton(this);
        rbMic.setText("Microphone");
        modeGroup.addView(rbInternal);
        modeGroup.addView(rbMic);
        root.addView(modeGroup);

        Button startBtn = new Button(this);
        startBtn.setText("START STREAMING");
        startBtn.setOnClickListener(v -> checkPermissionsAndStart());
        root.addView(startBtn);

        Button stopBtn = new Button(this);
        stopBtn.setText("STOP STREAMING");
        stopBtn.setOnClickListener(v -> stopStream());
        root.addView(stopBtn);

        statusText = new TextView(this);
        statusText.setText("\nStatus: Idle");
        statusText.setTextSize(16f);
        root.addView(statusText);

        setContentView(root);
    }

    private void checkPermissionsAndStart() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                requestPermissions(new String[]{
                        Manifest.permission.RECORD_AUDIO,
                        Manifest.permission.POST_NOTIFICATIONS
                }, REQ_PERMS);
            } else {
                requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQ_PERMS);
            }
            return;
        }
        initiateStream();
    }

    private void initiateStream() {
        String ip = ipInput.getText().toString().trim();
        int port = 50005;
        try {
            port = Integer.parseInt(portInput.getText().toString().trim());
        } catch (NumberFormatException ignored) {}

        prefs.edit().putString("ip", ip).putInt("port", port).apply();

        if (rbInternal.isChecked()) {
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            startActivityForResult(mpm.createScreenCaptureIntent(), REQ_PROJECTION);
        } else {
            startStreamService(0, null);
        }
    }

    private void startStreamService(int resultCode, Intent data) {
        Intent serviceIntent = new Intent(this, StreamService.class);
        serviceIntent.setAction(StreamService.ACTION_START);
        serviceIntent.putExtra("ip", ipInput.getText().toString().trim());
        serviceIntent.putExtra("port", Integer.parseInt(portInput.getText().toString().trim()));
        serviceIntent.putExtra("internal", rbInternal.isChecked());
        serviceIntent.putExtra("resultCode", resultCode);
        serviceIntent.putExtra("data", data);

        startForegroundService(serviceIntent);
        statusText.setText("\nStatus: Streaming to " + ipInput.getText().toString().trim());
    }

    private void stopStream() {
        Intent serviceIntent = new Intent(this, StreamService.class);
        serviceIntent.setAction(StreamService.ACTION_STOP);
        startService(serviceIntent);
        statusText.setText("\nStatus: Stopped");
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_PROJECTION) {
            if (resultCode == Activity.RESULT_OK && data != null) {
                startStreamService(resultCode, data);
            } else {
                Toast.makeText(this, "Screen/Audio capture permission denied", Toast.LENGTH_SHORT).show();
            }
        }
    }
}
