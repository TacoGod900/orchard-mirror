"""Capture the exact RTP stream relayed by Orchard's CoreDevice agent."""

from __future__ import annotations

import argparse
import asyncio
import socket
import sys
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY_ROOT / "src" / "Orchard.Mirror.Agent"))

from agent import CoreDeviceSession  # noqa: E402


async def capture(
    output: Path,
    duration: float,
    udid: str | None,
    *,
    ltrp: bool,
    fec: bool,
    rtcp_fb: bool,
    rctl: bool,
    paired_audio: bool,
    exercise_motion: bool,
    mount_ddi: bool,
) -> None:
    loop = asyncio.get_running_loop()
    receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    receiver.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
    receiver.bind(("127.0.0.1", 0))
    receiver.setblocking(False)
    session = CoreDeviceSession()
    packets = 0
    try:
        if mount_ddi:
            await session.mount_ddi(udid)
        await session.connect(udid)
        await session.start_video(
            "127.0.0.1",
            receiver.getsockname()[1],
            1,
            allow_rtcp_fb=rtcp_fb,
            ltrp_enabled=ltrp,
            fec_enabled=fec,
            rctl_enabled=rctl,
            paired_audio_enabled=paired_audio,
        )

        async def request_clean_anchor() -> None:
            await asyncio.sleep(0.5)
            await session.request_keyframe()

        keyframe_task = asyncio.create_task(request_clean_anchor())

        async def exercise_screen_motion() -> None:
            await asyncio.sleep(1.0)
            motion_deadline = loop.time() + max(0.0, duration - 2.0)
            while loop.time() < motion_deadline:
                await session.drag(52000, 32768, 12000, 32768, 24, 0.22)
                await asyncio.sleep(0.12)
                await session.drag(12000, 32768, 52000, 32768, 24, 0.22)
                await asyncio.sleep(0.12)

        motion_task = asyncio.create_task(exercise_screen_motion()) if exercise_motion else None
        deadline = loop.time() + duration
        with output.open("wb") as stream:
            while loop.time() < deadline:
                try:
                    packet = await asyncio.wait_for(
                        loop.sock_recv(receiver, 65535),
                        timeout=deadline - loop.time(),
                    )
                except asyncio.TimeoutError:
                    break
                stream.write(len(packet).to_bytes(4, "big"))
                stream.write(packet)
                packets += 1
        await keyframe_task
        if motion_task is not None:
            await motion_task
    finally:
        await session.disconnect()
        receiver.close()
    print(f"captured {packets} packets to {output}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--duration", type=float, default=12.0)
    parser.add_argument("--udid")
    parser.add_argument("--ltrp", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument("--fec", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--rtcp-fb", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--rctl", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--paired-audio", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument("--exercise-motion", action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument("--mount-ddi", action=argparse.BooleanOptionalAction, default=False)
    args = parser.parse_args()
    asyncio.run(
        capture(
            args.output,
            args.duration,
            args.udid,
            ltrp=args.ltrp,
            fec=args.fec,
            rtcp_fb=args.rtcp_fb,
            rctl=args.rctl,
            paired_audio=args.paired_audio,
            exercise_motion=args.exercise_motion,
            mount_ddi=args.mount_ddi,
        )
    )


if __name__ == "__main__":
    main()
