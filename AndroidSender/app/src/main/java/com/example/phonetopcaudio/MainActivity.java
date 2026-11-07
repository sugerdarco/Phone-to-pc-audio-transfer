package com.example.phonetopcaudio;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Bundle;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.text.InputType;
import android.util.Patterns;
import android.view.ViewGroup;

public final class MainActivity extends Activity {
    private static final int AUDIO_PERMISSION_REQUEST = 101;
    private static final int PROJECTION_REQUEST = 102;
    private EditText hostField;
    private TextView status;
    private String requestedHost;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setPadding(28, 44, 28, 28);
        TextView heading = new TextView(this);
        heading.setText("Phone → PC Audio");
        heading.setTextSize(24);
        column.addView(heading);
        TextView help = new TextView(this);
        help.setText("1. Start the Windows receiver.\n2. Enter its IPv4 address.\n3. Tap Start and approve Android's capture prompt.\n\nBoth devices must use the same trusted local network.");
        help.setPadding(0, 24, 0, 14);
        column.addView(help);
        hostField = new EditText(this);
        hostField.setSingleLine(true);
        hostField.setHint("Windows IPv4 address, e.g. 192.168.1.50");
        hostField.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        hostField.setText(getPreferences(MODE_PRIVATE).getString("lastHost", ""));
        column.addView(hostField, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        Button start = new Button(this);
        start.setText("Start sending audio");
        start.setOnClickListener(v -> requestStart());
        column.addView(start);
        Button stop = new Button(this);
        stop.setText("Stop streaming");
        stop.setOnClickListener(v -> {
            stopService(new Intent(this, StreamService.class));
            status.setText("Stopped");
        });
        column.addView(stop);
        status = new TextView(this);
        status.setText("Not streaming");
        status.setPadding(0, 18, 0, 10);
        column.addView(status);
        TextView details = new TextView(this);
        details.setText("Captures allowed game/media audio only. Calls, protected media and apps that block capture will be silent. The app does not transmit your microphone or video. The phone may still play audio locally.");
        column.addView(details);
        setContentView(column);
    }

    @Override protected void onResume() {
        super.onResume();
        if (status != null && StreamService.isRunning) status.setText("Audio streaming in the background");
    }

    private void requestStart() {
        String host = hostField.getText().toString().trim();
        if (!Patterns.IP_ADDRESS.matcher(host).matches() || host.contains(":")) {
            hostField.setError("Enter the Windows PC's IPv4 address");
            return;
        }
        requestedHost = host;
        getPreferences(MODE_PRIVATE).edit().putString("lastHost", host).apply();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, AUDIO_PERMISSION_REQUEST);
        } else {
            askForProjection();
        }
    }

    @Override public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(requestCode, permissions, grants);
        if (requestCode != AUDIO_PERMISSION_REQUEST) return;
        if (grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) {
            askForProjection();
        } else {
            status.setText("Microphone/record-audio permission is required by Android for playback capture.");
        }
    }

    private void askForProjection() {
        MediaProjectionManager mgr = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        startActivityForResult(mgr.createScreenCaptureIntent(), PROJECTION_REQUEST);
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent resultData) {
        super.onActivityResult(requestCode, resultCode, resultData);
        if (requestCode != PROJECTION_REQUEST) return;
        if (resultCode != RESULT_OK || resultData == null) {
            status.setText("Android capture permission was not granted.");
            return;
        }
        if (requestedHost == null) {
            requestedHost = getPreferences(MODE_PRIVATE).getString("lastHost", null);
        }
        Intent service = new Intent(this, StreamService.class);
        service.putExtra(StreamService.EXTRA_HOST, requestedHost);
        service.putExtra(StreamService.EXTRA_RESULT_CODE, resultCode);
        service.putExtra(StreamService.EXTRA_RESULT_DATA, resultData);
        startForegroundService(service);
        status.setText("Starting audio stream to " + requestedHost + ":45999");
    }
}
