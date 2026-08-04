"""Generate AAC-ELD test vectors locally, so decoder work does not need the iPhone.

Orchard has to implement its own AAC-ELD decoder (ADR 0014 §4 and §6), and a decoder
cannot be trusted without something independent to check it against. The obvious
source of vectors is the phone, but it is not the only one: GStreamer's ``fdkaacenc``
exposes only lc/he/ld, while ``libfdk-aac`` itself implements ER AAC ELD (AOT 39), so
this drives the library directly.

It also prints the AudioSpecificConfig the encoder produces, which is what identifies
the exact mode. That is worth more than it sounds: the config the device sends,
``F8 E6 40 00``, is widely described as 480-sample frames, and this tool shows that
libfdk-aac emits ``F8 E6 50 00`` for 480 and ``F8 E6 40 00`` for 512. The differing
bit is ``frameLengthFlag``, the first bit of ELDSpecificConfig.

Output is length-prefixed access units -- a 4-byte big-endian length before each --
the same shape ``AacEldAudioCapture`` writes, so vectors and real captures are
interchangeable.

Usage:
    python generate-aac-eld-vectors.py OUT.aacel [--seconds 2] [--samples-per-frame 512]
    python generate-aac-eld-vectors.py --compare-configs

This is test tooling. It is not part of the shipped media path and libfdk-aac is not
a dependency of the product.
"""

from __future__ import annotations

import argparse
import ctypes
import math
import struct
import sys
from pathlib import Path

DEFAULT_LIBRARY = r"C:\msys64\ucrt64\bin\libfdk-aac-2.dll"

AACENC_AOT = 0x0100
AACENC_BITRATE = 0x0101
AACENC_SAMPLERATE = 0x0103
AACENC_GRANULE_LENGTH = 0x0105
AACENC_CHANNELMODE = 0x0106
AACENC_TRANSMUX = 0x0300

AOT_ER_AAC_ELD = 39
IN_AUDIO_DATA = 0
OUT_BITSTREAM_DATA = 3

# Offsets into AACENC_InfoStruct. The struct is not public ABI across fdk-aac
# releases, so rather than mirroring a layout that may shift, the fields actually
# needed are located once and asserted to be self-consistent.
_INFO_FRAME_LENGTH = 16
_INFO_CONF_BUF = 28
_INFO_CONF_SIZE = 92


class _BufDesc(ctypes.Structure):
    _fields_ = [
        ("numBufs", ctypes.c_int),
        ("bufs", ctypes.POINTER(ctypes.c_void_p)),
        ("bufferIdentifiers", ctypes.POINTER(ctypes.c_int)),
        ("bufSizes", ctypes.POINTER(ctypes.c_int)),
        ("bufElSizes", ctypes.POINTER(ctypes.c_int)),
    ]


class _InArgs(ctypes.Structure):
    _fields_ = [("numInSamples", ctypes.c_int), ("numAncBytes", ctypes.c_int)]


class _OutArgs(ctypes.Structure):
    _fields_ = [
        ("numOutBytes", ctypes.c_int),
        ("numInSamples", ctypes.c_int),
        ("numAncBytes", ctypes.c_int),
        ("bitResState", ctypes.c_int),
    ]


class Encoder:
    """An open libfdk-aac encoder configured for ER AAC ELD."""

    def __init__(self, library: str, sample_rate: int, channels: int, samples_per_frame: int, bitrate: int):
        self._fdk = ctypes.CDLL(library)
        self._handle = ctypes.c_void_p()
        self._channels = channels
        self._samples_per_frame = samples_per_frame
        _check(self._fdk.aacEncOpen(ctypes.byref(self._handle), 0, channels), "aacEncOpen")
        for parameter, value, name in [
            (AACENC_AOT, AOT_ER_AAC_ELD, "AOT 39 (ER AAC ELD)"),
            (AACENC_SAMPLERATE, sample_rate, f"{sample_rate} Hz"),
            (AACENC_CHANNELMODE, channels, f"{channels} channels"),
            (AACENC_GRANULE_LENGTH, samples_per_frame, f"{samples_per_frame}-sample frames"),
            (AACENC_TRANSMUX, 0, "raw access units"),
            (AACENC_BITRATE, bitrate, f"{bitrate} bit/s"),
        ]:
            _check(self._fdk.aacEncoder_SetParam(self._handle, parameter, value), f"set {name}")
        # A call with no buffers applies the configuration.
        _check(self._fdk.aacEncEncode(self._handle, None, None, None, None), "configure")

        raw = (ctypes.c_ubyte * 512)()
        _check(self._fdk.aacEncInfo(self._handle, ctypes.byref(raw)), "aacEncInfo")
        info = bytes(raw)
        self.frame_length = struct.unpack_from("<I", info, _INFO_FRAME_LENGTH)[0]
        size = struct.unpack_from("<I", info, _INFO_CONF_SIZE)[0]
        if not 1 <= size <= 64 or self.frame_length != samples_per_frame:
            raise SystemExit(
                "libfdk-aac's info struct does not match the expected layout "
                f"(frameLength={self.frame_length}, confSize={size}); this build differs and the "
                "offsets in this script need rechecking.")
        self.audio_specific_config = info[_INFO_CONF_BUF:_INFO_CONF_BUF + size]

    def encode(self, pcm: ctypes.Array) -> bytes:
        """Encode one frame of interleaved 16-bit PCM into an access unit."""
        output = (ctypes.c_ubyte * 8192)()
        in_bufs = (ctypes.c_void_p * 1)(ctypes.cast(pcm, ctypes.c_void_p).value)
        in_ids = (ctypes.c_int * 1)(IN_AUDIO_DATA)
        in_sizes = (ctypes.c_int * 1)(self._samples_per_frame * self._channels * 2)
        in_elements = (ctypes.c_int * 1)(2)
        out_bufs = (ctypes.c_void_p * 1)(ctypes.cast(output, ctypes.c_void_p).value)
        out_ids = (ctypes.c_int * 1)(OUT_BITSTREAM_DATA)
        out_sizes = (ctypes.c_int * 1)(len(output))
        out_elements = (ctypes.c_int * 1)(1)

        in_desc = _BufDesc(1, in_bufs, in_ids, in_sizes, in_elements)
        out_desc = _BufDesc(1, out_bufs, out_ids, out_sizes, out_elements)
        in_args = _InArgs(self._samples_per_frame * self._channels, 0)
        out_args = _OutArgs()
        _check(
            self._fdk.aacEncEncode(
                self._handle, ctypes.byref(in_desc), ctypes.byref(out_desc),
                ctypes.byref(in_args), ctypes.byref(out_args)),
            "aacEncEncode")
        return bytes(output[: out_args.numOutBytes])


def _check(status: int, what: str) -> None:
    if status != 0:
        raise SystemExit(f"{what} failed: 0x{status & 0xFFFFFFFF:04X}")


def compare_configs(library: str) -> None:
    """Show which AudioSpecificConfig belongs to which frame length."""
    print("AAC-ELD, 48 kHz, stereo:")
    for samples in (480, 512):
        encoder = Encoder(library, 48000, 2, samples, 128000)
        print(f"  {samples} samples/frame -> {encoder.audio_specific_config.hex().upper()}")
    print("\nThe iPhone's paired audio leg sends F8E64000.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", nargs="?", type=Path)
    parser.add_argument("--seconds", type=float, default=2.0)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--samples-per-frame", type=int, default=512, choices=(480, 512))
    parser.add_argument("--bitrate", type=int, default=128000)
    parser.add_argument("--library", default=DEFAULT_LIBRARY)
    parser.add_argument("--compare-configs", action="store_true")
    arguments = parser.parse_args()

    if arguments.compare_configs:
        compare_configs(arguments.library)
        return
    if arguments.output is None:
        parser.error("an output path is required unless --compare-configs is given")

    encoder = Encoder(
        arguments.library, arguments.sample_rate, arguments.channels,
        arguments.samples_per_frame, arguments.bitrate)
    print(f"AudioSpecificConfig : {encoder.audio_specific_config.hex().upper()}")
    print(f"frame length        : {encoder.frame_length} samples")

    frame = arguments.samples_per_frame
    total = int(arguments.seconds * arguments.sample_rate / frame)
    pcm = (ctypes.c_short * (frame * arguments.channels))()
    written = 0
    with arguments.output.open("wb") as handle:
        for index in range(total):
            for n in range(frame):
                t = (index * frame + n) / arguments.sample_rate
                # A different tone per channel, so a decoder that swaps or collapses
                # channels is visible rather than merely wrong.
                pcm[arguments.channels * n] = int(12000 * math.sin(2 * math.pi * 440.0 * t))
                if arguments.channels > 1:
                    pcm[arguments.channels * n + 1] = int(9000 * math.sin(2 * math.pi * 660.0 * t))
            unit = encoder.encode(pcm)
            if unit:
                handle.write(struct.pack(">I", len(unit)) + unit)
                written += 1

    print(f"access units        : {written} -> {arguments.output}")


if __name__ == "__main__":
    sys.exit(main())
