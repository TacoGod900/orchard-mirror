"""Analyze and depacketize a CoreDevice DisplayService RTP capture.

The input format is the length-prefixed packet dump produced by
``pymobiledevice3 ... display start-video-stream``. The output is Annex-B
HEVC with Apple's proprietary 14-byte DisplayService trailer removed.
"""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

START_CODE = b"\x00\x00\x00\x01"
DISPLAY_SERVICE_TRAILER = bytes.fromhex("04f00ac0000003000004ec0ab003")


def depacketize_hevc(payload: bytes, fu_buffer: bytearray, nal_out: list[bytes], keep_trailer: bool) -> None:
    if len(payload) < 2:
        return
    nal_type = (payload[0] >> 1) & 0x3F
    if nal_type == 48:
        offset = 2
        while offset + 2 <= len(payload):
            size = int.from_bytes(payload[offset : offset + 2], "big")
            offset += 2
            nal = payload[offset : offset + size]
            nal_out.append(nal if keep_trailer or not nal.endswith(DISPLAY_SERVICE_TRAILER) else nal[:-14])
            offset += size
    elif nal_type == 49 and len(payload) >= 3:
        fu_header = payload[2]
        if fu_header & 0x80:
            fu_buffer[:] = bytes([(payload[0] & 0x81) | ((fu_header & 0x3F) << 1), payload[1]]) + payload[3:]
        else:
            fu_buffer.extend(payload[3:])
        if fu_header & 0x40 and fu_buffer:
            nal = bytes(fu_buffer)
            nal_out.append(nal if keep_trailer or not nal.endswith(DISPLAY_SERVICE_TRAILER) else nal[:-14])
            fu_buffer.clear()
    else:
        nal_out.append(
            payload if keep_trailer or not payload.endswith(DISPLAY_SERVICE_TRAILER) else payload[:-14]
        )


def iter_packets(data: bytes):
    offset = 0
    while offset + 4 <= len(data):
        size = int.from_bytes(data[offset : offset + 4], "big")
        offset += 4
        if offset + size > len(data):
            raise ValueError(f"truncated packet at byte {offset}")
        yield data[offset : offset + size]
        offset += size


def rtp_payload(packet: bytes) -> bytes:
    cc = packet[0] & 0x0F
    header_size = 12 + (cc * 4)
    if packet[0] & 0x10:
        if header_size + 4 > len(packet):
            raise ValueError("truncated RTP extension")
        extension_words = int.from_bytes(packet[header_size + 2 : header_size + 4], "big")
        header_size += 4 + (extension_words * 4)
    if header_size > len(packet):
        raise ValueError("truncated RTP header")
    return packet[header_size:]


def rtp_extension(packet: bytes) -> tuple[int, bytes] | None:
    cc = packet[0] & 0x0F
    header_size = 12 + (cc * 4)
    if not packet[0] & 0x10:
        return None
    if header_size + 4 > len(packet):
        raise ValueError("truncated RTP extension")
    profile = int.from_bytes(packet[header_size : header_size + 2], "big")
    extension_words = int.from_bytes(packet[header_size + 2 : header_size + 4], "big")
    start = header_size + 4
    end = start + (extension_words * 4)
    if end > len(packet):
        raise ValueError("truncated RTP extension data")
    return profile, packet[start:end]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--stats", type=Path)
    parser.add_argument("--frames", type=Path)
    parser.add_argument("--keep-trailer", action="store_true")
    args = parser.parse_args()

    fu_buffer = bytearray()
    nal_units: list[bytes] = []
    nal_types: Counter[int] = Counter()
    packet_count = 0
    rtcp_count = 0
    marker_count = 0
    forward_gaps = 0
    reorders = 0
    last_sequence: int | None = None
    first_sequence: int | None = None
    last_sequence_seen: int | None = None
    timestamps: set[int] = set()
    extension_values: Counter[tuple[int, str]] = Counter()
    frames: list[dict[str, object]] = []
    frame_start_sequence: int | None = None
    frame_packet_count = 0
    frame_bytes = 0
    frame_extension: tuple[int, bytes] | None = None
    frame_nal_types: list[int] = []

    for packet in iter_packets(args.input.read_bytes()):
        if len(packet) < 12:
            continue
        payload_type = packet[1] & 0x7F
        if 64 <= payload_type <= 95:
            rtcp_count += 1
            continue
        packet_count += 1
        sequence = int.from_bytes(packet[2:4], "big")
        timestamp = int.from_bytes(packet[4:8], "big")
        timestamps.add(timestamp)
        extension = rtp_extension(packet)
        if extension is not None:
            extension_values[(extension[0], extension[1].hex())] += 1
        if frame_start_sequence is None:
            frame_start_sequence = sequence
        frame_packet_count += 1
        frame_bytes += len(packet)
        frame_extension = extension
        if first_sequence is None:
            first_sequence = sequence
        last_sequence_seen = sequence
        if last_sequence is not None and sequence != ((last_sequence + 1) & 0xFFFF):
            if ((sequence - last_sequence) & 0xFFFF) < 0x8000:
                forward_gaps += ((sequence - last_sequence) & 0xFFFF) - 1
                fu_buffer.clear()
            else:
                reorders += 1
        if last_sequence is None or ((sequence - last_sequence) & 0xFFFF) < 0x8000:
            last_sequence = sequence
        if packet[1] & 0x80:
            marker_count += 1
        emitted: list[bytes] = []
        depacketize_hevc(rtp_payload(packet), fu_buffer, emitted, args.keep_trailer)
        for nal in emitted:
            if nal:
                nal_types[(nal[0] >> 1) & 0x3F] += 1
                frame_nal_types.append((nal[0] >> 1) & 0x3F)
                nal_units.append(nal)
        if packet[1] & 0x80:
            frames.append(
                {
                    "index": len(frames),
                    "timestamp": timestamp,
                    "firstSequence": frame_start_sequence,
                    "lastSequence": sequence,
                    "packets": frame_packet_count,
                    "bytes": frame_bytes,
                    "extensionProfile": frame_extension[0] if frame_extension else None,
                    "extensionData": frame_extension[1].hex() if frame_extension else None,
                    "nalTypes": frame_nal_types,
                }
            )
            frame_start_sequence = None
            frame_packet_count = 0
            frame_bytes = 0
            frame_extension = None
            frame_nal_types = []

    args.output.write_bytes(b"".join(START_CODE + nal for nal in nal_units))
    stats = {
        "packets": packet_count,
        "rtcpPackets": rtcp_count,
        "firstSequence": first_sequence,
        "lastSequence": last_sequence_seen,
        "forwardMissingPackets": forward_gaps,
        "reorderedPackets": reorders,
        "rtpTimestamps": len(timestamps),
        "markerFrames": marker_count,
        "nalUnits": len(nal_units),
        "nalTypes": dict(sorted(nal_types.items())),
        "rtpExtensions": [
            {"profile": profile, "data": data, "packets": count}
            for (profile, data), count in extension_values.most_common(20)
        ],
        "uniqueRtpExtensions": len(extension_values),
        "annexBBytes": args.output.stat().st_size,
    }
    rendered = json.dumps(stats, indent=2)
    print(rendered)
    if args.stats:
        args.stats.write_text(rendered + "\n", encoding="utf-8")
    if args.frames:
        args.frames.write_text(json.dumps(frames, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
