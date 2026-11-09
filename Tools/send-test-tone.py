import math
import socket
import struct
import time

TARGET_IP = "127.0.0.1"
TARGET_PORT = 50005
SAMPLE_RATE = 48000
FREQ = 440.0
FRAMES_PER_PACKET = 480

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
print(f"Sending 440Hz test tone to {TARGET_IP}:{TARGET_PORT} (Ctrl+C to stop)...")

phase = 0.0
step = 2.0 * math.pi * FREQ / SAMPLE_RATE

try:
    while True:
        packet = bytearray()
        for _ in range(FRAMES_PER_PACKET):
            sample = int(math.sin(phase) * 12000)
            packet += struct.pack("<hh", sample, sample)
            phase += step
        sock.sendto(packet, (TARGET_IP, TARGET_PORT))
        time.sleep(FRAMES_PER_PACKET / SAMPLE_RATE)
except KeyboardInterrupt:
    print("Stopped.")
