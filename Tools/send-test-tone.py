"""Optional test: python send-test-tone.py <Windows-LAN-IP>
Send a 440 Hz stereo tone to the Windows receiver for 3 seconds.
Uses only Python's standard library. Do not use on an untrusted network.
"""
import math
import random
import socket
import struct
import sys
import time

if len(sys.argv) != 2:
    raise SystemExit("Usage: python send-test-tone.py <Windows-LAN-IP>")
addr = (sys.argv[1], 45999)
frames_per_packet = 240  # 5 ms @ 48 kHz
session = random.randint(-2**31, 2**31 - 1)
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
start = time.monotonic()
sequence = 0
while sequence < 600:  # 3 seconds
    data = bytearray()
    for n in range(frames_per_packet):
        sample = int(7500 * math.sin(2 * math.pi * 440 * (sequence * frames_per_packet + n) / 48000))
        data.extend(struct.pack("<hh", sample, sample))
    sock.sendto(b"AWA1" + struct.pack("<ii", session, sequence) + data, addr)
    sequence += 1
    next_packet_at = start + sequence * 0.005
    time.sleep(max(0, next_packet_at - time.monotonic()))
print("Sent 3-second test tone")
