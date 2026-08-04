#!/usr/bin/env python3
"""Orchard Mirror's GPL-3.0 CoreDevice control-plane agent.

SPDX-License-Identifier: GPL-3.0-only
"""

from __future__ import annotations

import asyncio
import contextlib
import functools
import json
import logging
import os
import select
import socket
import struct
import sys
import time
import uuid
from dataclasses import dataclass
from typing import Any


PROTOCOL_VERSION = 1
AGENT_VERSION = "1.0.0"
LOGGER = logging.getLogger("orchard_mirror_agent")

# The payload-100 bank is the HEVC codec even though the current pymobiledevice3
# builder exposes that bank through its ``avc_features`` argument. This fuller
# feature list is used by idevice's CoreDevice receiver specifically to prevent
# the predictive stream from tearing during sustained motion.
_SCREEN_HEVC_FEATURES = (
    "FLS;MS:-1;LF:-1;LTR;CABAC;POS:0;EOD:1;HTS:2;RR:3;"
    "AR:16/9,5/8;XR:16/9,5/8;"
)


# Commands that mutate the CoreDevice session or open a service on it.
_LIFECYCLE_COMMANDS = frozenset({
    "mount-ddi",
    "usb-status",
    "enable-developer-mode",
    "connect",
    "disconnect",
    "start-video",
    "stop-video",
    "device-info",
    "lockstate",
    "privacy-status",
    "screen-curtain",
})

# Commands that drive the HID surfaces, which must stay in submission order.
_INPUT_COMMANDS = frozenset({"touch", "drag", "type", "key", "button", "silence"})


class ProtocolError(Exception):
    """A request error that is safe to return to the supervisor."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _json_safe(value: Any) -> Any:
    """Convert CoreDevice/XPC values into ordinary JSON-compatible values."""
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, bytes):
        return value.hex()
    if isinstance(value, uuid.UUID):
        return str(value)
    if isinstance(value, dict):
        return {str(key): _json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [_json_safe(item) for item in value]
    return str(value)


def _classify_error(error: BaseException) -> ProtocolError:
    """Map pymobiledevice3 failures to stable product-facing error codes."""
    name = type(error).__name__
    message = str(error).strip() or name
    if name in {"NoDeviceConnectedError", "DeviceNotFoundError"}:
        return ProtocolError("no-device", "No iPhone is connected over USB.")
    if name in {"NotPairedError", "UserDeniedPairingError", "PairingDialogResponsePendingError"}:
        return ProtocolError("not-paired", "Unlock the iPhone and trust this computer, then try again.")
    if name in {"DeveloperModeIsNotEnabledError", "DeveloperModeError"}:
        return ProtocolError("developer-mode-off", "Developer Mode is disabled on the iPhone.")
    if name in {"InvalidServiceError", "RSDRequiredError", "CoreDeviceError"}:
        return ProtocolError(
            "ddi-unavailable",
            "CoreDevice is unavailable. Mount the Developer Disk Image for this boot and try again.",
        )
    if name in {"UserspaceTunnelUnavailableError", "RoutableTunnelRequiredError"}:
        return ProtocolError("tunnel-unavailable", message)
    if name in {
        "ConnectionTerminatedError",
        "ConnectionResetError",
        "BrokenPipeError",
        "IncompleteReadError",
        "TimeoutError",
    }:
        return ProtocolError("connection-lost", "The iPhone connection was lost.")
    if isinstance(error, (ModuleNotFoundError, ImportError)):
        return ProtocolError(
            "dependency-missing",
            "pymobiledevice3 is not installed in the selected Python runtime.",
        )
    return ProtocolError("agent-error", message)


def _is_transient_connection_error(error: BaseException) -> bool:
    return isinstance(error, (ConnectionError, TimeoutError, asyncio.IncompleteReadError)) or type(error).__name__ in {
        "ConnectionTerminatedError",
        "ConnectionResetError",
        "BrokenPipeError",
        "IncompleteReadError",
        "TimeoutError",
    }


_REMOTE_PAIRING_SERVICE = b"_remotepairing._tcp"


def _browse_with_windows_bonjour(timeout: float) -> list[tuple[str, int]]:
    """Resolve RemotePairing endpoints through Apple's Bonjour service on Windows.

    pymobiledevice3 discovers services with ``zeroconf``, which opens its own
    multicast listener on UDP 5353. When Apple's Bonjour service already owns
    that port -- it is installed alongside iTunes and the Apple device drivers --
    the responses are delivered to Bonjour and ``zeroconf`` observes nothing, so
    wireless discovery silently returns an empty list. Querying ``dnssd.dll``
    directly asks the component that actually holds the socket.
    """
    import ctypes

    dnssd = ctypes.WinDLL("dnssd.dll")
    service_ref = ctypes.c_void_p

    browse_reply = ctypes.WINFUNCTYPE(
        None, service_ref, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int32,
        ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p)
    resolve_reply = ctypes.WINFUNCTYPE(
        None, service_ref, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int32,
        ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint16, ctypes.c_uint16,
        ctypes.POINTER(ctypes.c_ubyte), ctypes.c_void_p)

    dnssd.DNSServiceBrowse.argtypes = [
        ctypes.POINTER(service_ref), ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_char_p, ctypes.c_char_p, browse_reply, ctypes.c_void_p]
    dnssd.DNSServiceBrowse.restype = ctypes.c_int32
    dnssd.DNSServiceResolve.argtypes = [
        ctypes.POINTER(service_ref), ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, resolve_reply, ctypes.c_void_p]
    dnssd.DNSServiceResolve.restype = ctypes.c_int32
    address_reply = ctypes.WINFUNCTYPE(
        None, service_ref, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int32,
        ctypes.c_char_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p)
    dnssd.DNSServiceGetAddrInfo.argtypes = [
        ctypes.POINTER(service_ref), ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_char_p, address_reply, ctypes.c_void_p]
    dnssd.DNSServiceGetAddrInfo.restype = ctypes.c_int32

    dnssd.DNSServiceRefSockFD.argtypes = [service_ref]
    dnssd.DNSServiceRefSockFD.restype = ctypes.c_int
    dnssd.DNSServiceProcessResult.argtypes = [service_ref]
    dnssd.DNSServiceProcessResult.restype = ctypes.c_int32
    dnssd.DNSServiceRefDeallocate.argtypes = [service_ref]
    dnssd.DNSServiceRefDeallocate.restype = None

    def pump(reference: ctypes.c_void_p, deadline: float, is_done) -> None:
        """Deliver callbacks until they stop arriving or the deadline passes."""
        descriptor = dnssd.DNSServiceRefSockFD(reference)
        if descriptor < 0:
            return
        pump_socket = socket.socket(fileno=descriptor)
        try:
            while not is_done():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return
                readable, _, _ = select.select([pump_socket], [], [], min(remaining, 0.25))
                if not readable:
                    continue
                if dnssd.DNSServiceProcessResult(reference) != 0:
                    return
        finally:
            # The descriptor belongs to the DNSServiceRef; detach so closing the
            # Python wrapper cannot invalidate it before deallocation.
            pump_socket.detach()

    def lookup(host_target: str, port: int, interface_index: int) -> list[tuple[str, int]]:
        """Resolve a .local name over mDNS.

        ``socket.getaddrinfo`` takes about three seconds for these names because
        Windows consults unicast DNS before falling back to Bonjour. Asking
        Bonjour directly answers in milliseconds.
        """
        found: list[tuple[str, int]] = []

        @address_reply
        def on_address(_reference, _flags, _interface, error, _hostname, address, _ttl, _context):
            if error != 0 or not address:
                return
            family = ctypes.cast(address, ctypes.POINTER(ctypes.c_uint16))[0]
            raw = ctypes.string_at(address, 28)
            if family == socket.AF_INET:
                found.append((socket.inet_ntop(socket.AF_INET, raw[4:8]), port))
            elif family == socket.AF_INET6:
                scope = int.from_bytes(raw[24:28], "little")
                text = socket.inet_ntop(socket.AF_INET6, raw[8:24])
                found.append((f"{text}%{scope}" if scope else text, port))

        address_ref = service_ref()
        if dnssd.DNSServiceGetAddrInfo(
            ctypes.byref(address_ref), 0, interface_index, 0,
            host_target.encode("utf-8"), on_address, None
        ) != 0:
            return _addresses_for(host_target, port)
        try:
            pump(address_ref, min(deadline, time.monotonic() + 1.0), lambda: bool(found))
        finally:
            dnssd.DNSServiceRefDeallocate(address_ref)

        if not found:
            return _addresses_for(host_target, port)
        found.sort(key=lambda entry: ":" in entry[0])
        return found

    instances: list[tuple[str, int]] = []

    @browse_reply
    def on_browse(_reference, flags, interface_index, error, name, regtype, domain, _context):
        add_flag = 0x2
        if error != 0 or not (flags & add_flag) or name is None:
            return
        instances.append((name.decode("utf-8", "replace"), int(interface_index)))
        if regtype is not None and domain is not None:
            found_types.append((regtype, domain))

    found_types: list[tuple[bytes, bytes]] = []
    deadline = time.monotonic() + timeout
    browse_ref = service_ref()
    if dnssd.DNSServiceBrowse(
        ctypes.byref(browse_ref), 0, 0, _REMOTE_PAIRING_SERVICE, None, on_browse, None
    ) != 0:
        return []
    try:
        # Stop as soon as the first instance appears; a paired phone answers
        # immediately and waiting out the full window only delays connecting.
        pump(browse_ref, deadline, lambda: bool(instances))
    finally:
        dnssd.DNSServiceRefDeallocate(browse_ref)

    endpoints: list[tuple[str, int]] = []
    for index, (instance, interface_index) in enumerate(instances):
        resolved: list[tuple[str, int]] = []

        @resolve_reply
        def on_resolve(_reference, _flags, _interface, error, _fullname, host_target,
                       port, _txt_length, _txt, _context, _sink=resolved):
            if error != 0 or host_target is None:
                return
            _sink.append((host_target.decode("utf-8", "replace"), socket.ntohs(int(port))))

        regtype, domain = found_types[index] if index < len(found_types) else (
            _REMOTE_PAIRING_SERVICE + b".", b"local.")
        resolve_ref = service_ref()
        if dnssd.DNSServiceResolve(
            ctypes.byref(resolve_ref), 0, interface_index, instance.encode("utf-8"),
            regtype, domain, on_resolve, None
        ) != 0:
            continue
        try:
            pump(resolve_ref, min(deadline, time.monotonic() + 1.5), lambda: bool(resolved))
        finally:
            dnssd.DNSServiceRefDeallocate(resolve_ref)

        for host_target, port in resolved:
            for endpoint in lookup(host_target, port, interface_index):
                if endpoint not in endpoints:
                    endpoints.append(endpoint)
        if endpoints:
            # One reachable endpoint is enough to open the tunnel, and a phone
            # advertises several rotating instance names for the same address.
            break
    return endpoints


def _addresses_for(host_target: str, port: int) -> list[tuple[str, int]]:
    """Resolve a Bonjour host name, preferring IPv4 and scoping link-local IPv6."""
    try:
        candidates = socket.getaddrinfo(host_target, port, type=socket.SOCK_STREAM)
    except OSError:
        return []

    addresses: list[tuple[str, int]] = []
    for family, _type, _protocol, _canonical, sockaddr in candidates:
        if family == socket.AF_INET:
            addresses.append((sockaddr[0], port))
        elif family == socket.AF_INET6:
            address, scope = sockaddr[0], sockaddr[3]
            addresses.append((f"{address}%{scope}" if scope else address, port))
    addresses.sort(key=lambda entry: ":" in entry[0])
    return addresses


async def _discover_windows_remote_pairing(timeout: float = 2.5) -> list[tuple[str, int]]:
    """Return fresh RemotePairing endpoints, followed by any configured fallback."""
    configured_host = os.environ.get("ORCHARD_MIRROR_WIFI_HOST")
    configured_port = os.environ.get("ORCHARD_MIRROR_WIFI_PORT")
    configured = (
        (configured_host, int(configured_port))
        if configured_host and configured_port
        else None
    )
    if sys.platform != "win32":
        return [configured] if configured is not None else []

    endpoints: list[tuple[str, int]] = []
    try:
        endpoints = await asyncio.to_thread(_browse_with_windows_bonjour, timeout)
    except (OSError, RuntimeError, AttributeError) as error:
        LOGGER.info("Windows Bonjour discovery unavailable (%s); falling back", error)

    if not endpoints:
        from pymobiledevice3.bonjour import browse_remotepairing

        try:
            services = await browse_remotepairing(timeout=timeout)
        except (OSError, RuntimeError):
            services = []
        for service in services:
            # IPv4 is routable without a scope identifier and is the most reliable
            # RemotePairing path on Windows. Retain IPv6 as a later fallback.
            addresses = sorted(service.addresses, key=lambda address: ":" in address.ip)
            for address in addresses:
                endpoint = (address.full_ip, int(service.port))
                if endpoint not in endpoints:
                    endpoints.append(endpoint)

    if configured is not None and configured not in endpoints:
        endpoints.append(configured)
    return endpoints


@dataclass
class Request:
    """A validated protocol request."""

    request_id: str
    command: str
    arguments: dict[str, Any]

    @classmethod
    def parse(cls, line: str) -> "Request":
        try:
            value = json.loads(line)
        except json.JSONDecodeError as error:
            raise ProtocolError("invalid-json", f"Invalid JSON: {error.msg}.") from error
        if not isinstance(value, dict):
            raise ProtocolError("invalid-request", "A request must be a JSON object.")
        if value.get("version") != PROTOCOL_VERSION:
            raise ProtocolError("protocol-version", f"Only protocol version {PROTOCOL_VERSION} is supported.")
        request_id = value.get("id")
        command = value.get("command")
        arguments = value.get("arguments", {})
        if not isinstance(request_id, str) or not request_id:
            raise ProtocolError("invalid-request", "Request id must be a non-empty string.")
        if not isinstance(command, str) or not command:
            raise ProtocolError("invalid-request", "Command must be a non-empty string.")
        if not isinstance(arguments, dict):
            raise ProtocolError("invalid-request", "Arguments must be a JSON object.")
        return cls(request_id, command, arguments)


class CoreDeviceSession:
    """Own the one userspace tunnel allowed in the agent process."""

    def __init__(self) -> None:
        self._tunnel: Any = None
        self._rsd: Any = None
        self._udid: str | None = None
        self._transport: str | None = None
        self._video_service: Any = None
        self._video_transport: Any = None
        self._video_session_id: uuid.UUID | None = None
        self._video_receive_task: asyncio.Task[None] | None = None
        self._video_rtcp_task: asyncio.Task[None] | None = None
        self._audio_service: Any = None
        self._audio_transport: Any = None
        self._audio_session_id: uuid.UUID | None = None
        self._audio_receive_task: asyncio.Task[None] | None = None
        self._audio_rtcp_task: asyncio.Task[None] | None = None
        self._relay_socket: socket.socket | None = None
        self._relay_target: tuple[str, int] | None = None
        self._audio_relay_socket: socket.socket | None = None
        self._audio_relay_target: tuple[str, int] | None = None
        self._sender_ip: str | None = None
        self._rtcp_port = 0
        self._local_ssrc = 0
        self._remote_ssrc = 0
        self._highest_sequence = 0
        self._last_sequence: int | None = None
        self._packets_received = 0
        self._frames_received = 0
        self._last_rtp_timestamp = 0
        self._packets_in_frame = 0
        self._last_frame_packet_count = 0
        self._rctl_enabled = True
        self._audio_rtcp_port = 0
        self._audio_local_ssrc = 0
        self._audio_remote_ssrc = 0
        self._audio_highest_sequence = 0
        self._audio_last_sequence: int | None = None
        self._audio_packets_received = 0
        self._fir_sequence = 0
        self._last_keyframe_request = 0.0
        self._universal_hid: Any = None
        self._indigo_hid: Any = None
        self._keyboard_service_id: int | None = None
        self._video_started_at = 0.0
        self._last_touch = (32768, 32768)
        self._keep_awake_task: asyncio.Task[None] | None = None
        self._screen_curtain_assumed = False
        self._voice_over_was_enabled: bool | None = None

    @property
    def connected(self) -> bool:
        return self._rsd is not None

    async def mount_ddi(self, udid: str | None) -> dict[str, Any]:
        if self.connected:
            await self.disconnect()
        from pymobiledevice3.exceptions import AlreadyMountedError
        from pymobiledevice3.lockdown import create_using_usbmux
        from pymobiledevice3.services.mobile_image_mounter import auto_mount

        lockdown = await create_using_usbmux(serial=udid, autopair=True)
        try:
            developer_mode = bool(await lockdown.get_developer_mode_status())
            if not developer_mode:
                raise ProtocolError("developer-mode-off", "Developer Mode is disabled on the iPhone.")
            try:
                await auto_mount(lockdown)
                state = "mounted"
            except AlreadyMountedError:
                state = "already-mounted"
            return {
                "state": state,
                "udid": lockdown.udid,
                "productVersion": lockdown.product_version,
            }
        finally:
            await lockdown.close()

    async def connect(self, udid: str | None) -> dict[str, Any]:
        await self.disconnect()
        import pymobiledevice3.remote.userspace_tunnel as userspace_tunnel
        from pymobiledevice3.remote.tunnel_service import (
            create_core_device_tunnel_service_using_remotepairing,
            iter_remote_paired_identifiers,
        )
        from pymobiledevice3.remote.userspace_tunnel import UserspaceRsdTunnel

        usb_error: BaseException | None = None
        for attempt in range(1, 4):
            tunnel = UserspaceRsdTunnel(serial=udid, autopair=True, remotepairing_fallback=False)
            try:
                rsd = await tunnel.aopen()
                self._tunnel = tunnel
                self._rsd = rsd
                self._udid = str(rsd.udid)
                self._transport = "usb"
                return await self.device_info()
            except BaseException as error:
                usb_error = error
                await tunnel.aclose()
                self._tunnel = None
                self._rsd = None
                self._udid = None
                self._transport = None
                if attempt == 3 or not _is_transient_connection_error(error):
                    break
                LOGGER.info("USB tunnel opening failed transiently; retrying (%d/3)", attempt)
                await asyncio.sleep(0.4 * attempt)

        endpoints = await _discover_windows_remote_pairing()
        if not endpoints:
            if usb_error is not None and type(usb_error).__name__ not in {
                "NoDeviceConnectedError",
                "DeviceNotFoundError",
            }:
                raise usb_error
            raise ProtocolError("no-device", "No paired iPhone was found over USB or Wi-Fi.")

        identifiers = [udid] if udid else list(iter_remote_paired_identifiers())
        if not identifiers:
            raise ProtocolError(
                "not-paired",
                "No wireless pairing record exists. Connect the iPhone over USB once and pair it.",
            )

        wifi_error: BaseException | None = None
        original_provider_factory = userspace_tunnel._create_no_root_tunnel_provider
        for host, port in endpoints:
            for identifier in identifiers:
                async def direct_wifi_provider(
                    serial: str | None,
                    autopair: bool,
                    remotepairing_fallback: bool = True,
                    *,
                    selected_identifier: str = identifier,
                    selected_host: str = host,
                    selected_port: int = port,
                ) -> tuple[Any, None]:
                    provider = await create_core_device_tunnel_service_using_remotepairing(
                        serial or selected_identifier,
                        selected_host,
                        selected_port,
                        autopair=autopair,
                    )
                    return provider, None

                userspace_tunnel._create_no_root_tunnel_provider = direct_wifi_provider
                tunnel = UserspaceRsdTunnel(serial=identifier, autopair=False)
                try:
                    rsd = await tunnel.aopen()
                    self._tunnel = tunnel
                    self._rsd = rsd
                    self._udid = str(rsd.udid)
                    self._transport = "wifi"
                    LOGGER.info("Wi-Fi tunnel established through %s:%d", host, port)
                    return await self.device_info()
                except BaseException as error:
                    wifi_error = error
                    await tunnel.aclose()
                finally:
                    userspace_tunnel._create_no_root_tunnel_provider = original_provider_factory

        assert wifi_error is not None
        raise wifi_error

    async def device_info(self) -> dict[str, Any]:
        self._require_connected()
        from pymobiledevice3.remote.core_device.device_info import DeviceInfoService

        # iOS 27 closes the CoreDevice device-info XPC stream after one feature
        # invocation. Use a fresh service connection for each query. Newer builds
        # already include displayInfo in get_device_info, so avoid a redundant
        # getdisplayinfo invocation when that value is present.
        async with DeviceInfoService(self._rsd) as service:
            device = await service.get_device_info()
        display = device.get("displayInfo") if isinstance(device, dict) else None
        if not isinstance(display, dict):
            async with DeviceInfoService(self._rsd) as service:
                display = await service.get_display_info()
        lock_state = await self.lockstate()
        return _json_safe(
            {
                "udid": self._udid,
                "transport": self._transport,
                "device": device,
                "display": display,
                "lockState": lock_state,
            }
        )

    async def lockstate(self) -> dict[str, Any]:
        self._require_connected()
        from pymobiledevice3.exceptions import CoreDeviceError
        from pymobiledevice3.remote.core_device.device_info import DeviceInfoService

        try:
            async with DeviceInfoService(self._rsd) as service:
                return _json_safe(await service.get_lockstate())
        except CoreDeviceError as error:
            if "getlockstate" not in str(error) or "not implemented" not in str(error):
                raise
            # iOS 27 beta removed the CoreDevice getlockstate action. Treat it as
            # explicitly unknown; video-stall recovery remains the lock/disconnect
            # safety net instead of failing the whole session at startup.
            return {
                "supported": False,
                "lockState": "unknown",
                "reason": "feature-not-implemented",
            }

    async def start_video(
        self,
        relay_host: str,
        relay_port: int,
        display_id: int,
        *,
        audio_relay_host: str | None = None,
        audio_relay_port: int | None = None,
        allow_rtcp_fb: bool = True,
        ltrp_enabled: bool = True,
        fec_enabled: bool = False,
        rctl_enabled: bool = True,
        paired_audio_enabled: bool = True,
    ) -> dict[str, Any]:
        self._require_connected()
        if relay_host not in {"127.0.0.1", "::1"}:
            raise ProtocolError("invalid-argument", "Video RTP may only be relayed to loopback.")
        if not 1 <= relay_port <= 65535:
            raise ProtocolError("invalid-argument", "relayPort must be between 1 and 65535.")
        if audio_relay_host not in {None, "127.0.0.1", "::1"}:
            raise ProtocolError("invalid-argument", "Audio RTP may only be relayed to loopback.")
        if audio_relay_port is not None and not 1 <= audio_relay_port <= 65535:
            raise ProtocolError("invalid-argument", "audioRelayPort must be between 1 and 65535.")
        if (audio_relay_host is None) != (audio_relay_port is None):
            raise ProtocolError(
                "invalid-argument",
                "audioRelayHost and audioRelayPort must be supplied together.",
            )
        if display_id < 1:
            raise ProtocolError("invalid-argument", "displayId must be positive.")

        await self.stop_video()
        from pymobiledevice3.remote.core_device import display_service as display_service_module
        from pymobiledevice3.remote.core_device.media_stream_offer import build_negotiator_offer_video
        from pymobiledevice3.remote.core_device.screen_stream import open_media_receiver

        # Select the capability offer that keeps the phone's HEVC encoder and a
        # standards-compliant receiver on the same reference-picture model.
        display_service_module.build_negotiator_offer_video = functools.partial(
            build_negotiator_offer_video,
            avc_features=_SCREEN_HEVC_FEATURES,
        )
        DisplayService = display_service_module.DisplayService

        async def connect_display_service() -> Any:
            last_error: BaseException | None = None
            for attempt in range(1, 4):
                candidate = DisplayService(self._rsd)
                try:
                    await candidate.connect()
                    return candidate
                except BaseException as error:
                    last_error = error
                    try:
                        await candidate.close()
                    except BaseException:
                        pass
                    if attempt == 3 or not _is_transient_connection_error(error):
                        raise
                    await asyncio.sleep(float(attempt))
            assert last_error is not None
            raise last_error

        service = None
        transport = None
        relay_socket = None
        audio_relay_socket = None
        audio_service = None
        audio_transport = None
        audio_session_id = None
        # Bound before the try, like the rest. The failure path below reads it, and a failure
        # earlier than the negotiation that assigns it would otherwise raise NameError from inside
        # the handler and lose the original exception on the way out.
        session_id = None
        try:
            sender_ip = str(self._rsd.service.address[0])
            client_session_id = uuid.uuid4()
            audio_config: dict[str, Any] | None = None

            # Xcode opens the paired system-audio leg first and the video leg second,
            # under the same client session UUID. pymobiledevice3's RemoteXPC request
            # channel is single-use here, so each leg needs its own service channel.
            # iOS's media manager still groups both legs into one AVConference session.
            if paired_audio_enabled:
                audio_service = await connect_display_service()
                audio_transport, audio_receiver_ip = open_media_receiver(
                    audio_service,
                    (4 * 1024 * 1024, 1 * 1024 * 1024),
                )
                audio_answer = await asyncio.wait_for(
                    audio_service.start_audio_stream(
                        receiver_ip=audio_receiver_ip,
                        receiver_port=audio_transport.port,
                        sender_ip=sender_ip,
                        client_session_id=client_session_id,
                    ),
                    timeout=25.0,
                )
                audio_session_id = audio_answer["connection"]["options"][
                    "avcMediaStreamOptionClientSessionID"
                ]["uuid"]
                if not isinstance(audio_session_id, uuid.UUID):
                    audio_session_id = uuid.UUID(str(audio_session_id))
                audio_config = audio_answer["connection"].get("streamConfig", {})

            service = await connect_display_service()
            transport, receiver_ip = open_media_receiver(service, (8 * 1024 * 1024, 4 * 1024 * 1024))
            answer = await asyncio.wait_for(
                service.start_video_stream(
                    receiver_ip=receiver_ip,
                    receiver_port=transport.port,
                    sender_ip=sender_ip,
                    display_id=display_id,
                    allow_rtcp_fb=allow_rtcp_fb,
                    ltrp_enabled=ltrp_enabled,
                    fec_enabled=fec_enabled,
                    client_session_id=client_session_id,
                ),
                timeout=25.0,
            )
            session_id = answer["connection"]["options"]["avcMediaStreamOptionClientSessionID"]["uuid"]
            if not isinstance(session_id, uuid.UUID):
                session_id = uuid.UUID(str(session_id))
            config = answer["connection"].get("streamConfig", {})
            relay_socket = socket.socket(socket.AF_INET6 if relay_host == "::1" else socket.AF_INET, socket.SOCK_DGRAM)
            relay_socket.setblocking(False)
            if audio_relay_host is not None:
                audio_relay_socket = socket.socket(
                    socket.AF_INET6 if audio_relay_host == "::1" else socket.AF_INET,
                    socket.SOCK_DGRAM,
                )
                audio_relay_socket.setblocking(False)

            self._video_service = service
            self._video_transport = transport
            self._video_session_id = session_id
            self._audio_service = audio_service
            self._audio_transport = audio_transport
            self._audio_session_id = audio_session_id if paired_audio_enabled else None
            self._relay_socket = relay_socket
            self._relay_target = (relay_host, relay_port)
            self._audio_relay_socket = audio_relay_socket
            self._audio_relay_target = (
                (audio_relay_host, audio_relay_port)
                if audio_relay_host is not None and audio_relay_port is not None
                else None
            )
            self._sender_ip = sender_ip
            self._rtcp_port = int(config.get("SourcePort", 0))
            self._local_ssrc = int(config.get("RemoteSSRC", 0))
            self._remote_ssrc = int(config.get("LocalSSRC", 0))
            self._highest_sequence = 0
            self._last_sequence = None
            self._packets_received = 0
            self._frames_received = 0
            self._last_rtp_timestamp = 0
            self._packets_in_frame = 0
            self._last_frame_packet_count = 0
            self._rctl_enabled = rctl_enabled
            if audio_config is not None:
                self._audio_rtcp_port = int(audio_config.get("SourcePort", 0))
                self._audio_local_ssrc = int(audio_config.get("RemoteSSRC", 0))
                self._audio_remote_ssrc = int(audio_config.get("LocalSSRC", 0))
                self._audio_highest_sequence = 0
                self._audio_last_sequence = None
                self._audio_packets_received = 0
            self._fir_sequence = 0
            self._last_keyframe_request = 0.0
            self._video_receive_task = asyncio.create_task(self._relay_video(), name="orchard-video-relay")
            self._video_rtcp_task = asyncio.create_task(self._send_video_rtcp(), name="orchard-video-rtcp")
            if audio_transport is not None:
                self._audio_receive_task = asyncio.create_task(
                    self._receive_audio_for_session_parity(),
                    name="orchard-audio-session-parity",
                )
                self._audio_rtcp_task = asyncio.create_task(
                    self._send_audio_rtcp(),
                    name="orchard-audio-rtcp",
                )
            self._keep_awake_task = asyncio.create_task(self._keep_awake(), name="orchard-keep-awake")
            self._video_started_at = asyncio.get_running_loop().time()
            return _json_safe(
                {
                    "state": "streaming",
                    "relayHost": relay_host,
                    "relayPort": relay_port,
                    "audioRelayPort": audio_relay_port,
                    "streamConfig": config,
                    "audioStreamConfig": audio_config,
                    "pairedAudio": paired_audio_enabled,
                }
            )
        except BaseException:
            if audio_transport is not None:
                audio_transport.close()
            if audio_service is not None:
                if audio_session_id is not None:
                    try:
                        await asyncio.wait_for(self._stop_all_media_streams(audio_service), timeout=5.0)
                    except (asyncio.TimeoutError, OSError, ConnectionError):
                        pass
                try:
                    await audio_service.close()
                except (OSError, ConnectionError):
                    pass
            if relay_socket is not None:
                relay_socket.close()
            if audio_relay_socket is not None:
                audio_relay_socket.close()
            if transport is not None:
                transport.close()
            if service is not None:
                # Symmetric with the audio leg above, which has always done this. A video session
                # that got far enough to be negotiated has to be stopped, not merely disconnected
                # from: closing the channel alone leaves the phone's media daemon holding a session
                # nothing will ever end, which is the same wedge as losing the stopAll on the way
                # out. Reachable whenever start_video fails after the video leg came up -- a slow
                # phone against the caller's media timeout is enough.
                if session_id is not None:
                    try:
                        await asyncio.wait_for(self._stop_all_media_streams(service), timeout=5.0)
                    except (asyncio.TimeoutError, OSError, ConnectionError):
                        pass
                try:
                    await service.close()
                except (OSError, ConnectionError):
                    pass
            raise

    async def stop_video(self) -> dict[str, Any]:
        await self._stop_hid()
        keep_awake_task, self._keep_awake_task = self._keep_awake_task, None
        if keep_awake_task is not None:
            keep_awake_task.cancel()
            try:
                await keep_awake_task
            except asyncio.CancelledError:
                pass
        # Tell the phone the paired session is over BEFORE forgetting which session it was, and
        # before closing any of the transport underneath it.
        #
        # This ordering is the whole fix. The fields below used to be swapped out first, so the
        # second stop_video() inside disconnect() -- which exists precisely as a safety net -- found
        # `service is None` and silently did nothing. If the .NET side abandoned the wait in
        # between, the stopAll was simply lost, and losing it can leave the phone's media daemon
        # unable to accept another DisplayService channel until the device is restarted. Sending it
        # first also means it goes out ahead of the socket closes, so an abandoned wait still leaves
        # the message on its way.
        for stopping_service, stopping_session in (
            (self._video_service, self._video_session_id),
            (self._audio_service, self._audio_session_id),
        ):
            if stopping_service is None or stopping_session is None:
                continue
            try:
                await asyncio.wait_for(self._stop_all_media_streams(stopping_service), timeout=5.0)
            except (asyncio.TimeoutError, OSError, ConnectionError):
                pass

        receive_task, self._video_receive_task = self._video_receive_task, None
        rtcp_task, self._video_rtcp_task = self._video_rtcp_task, None
        audio_receive_task, self._audio_receive_task = self._audio_receive_task, None
        audio_rtcp_task, self._audio_rtcp_task = self._audio_rtcp_task, None
        service, self._video_service = self._video_service, None
        transport, self._video_transport = self._video_transport, None
        session_id, self._video_session_id = self._video_session_id, None
        audio_service, self._audio_service = self._audio_service, None
        audio_transport, self._audio_transport = self._audio_transport, None
        audio_session_id, self._audio_session_id = self._audio_session_id, None
        relay_socket, self._relay_socket = self._relay_socket, None
        self._relay_target = None
        audio_relay_socket, self._audio_relay_socket = self._audio_relay_socket, None
        self._audio_relay_target = None
        self._sender_ip = None
        self._video_started_at = 0.0

        for task in (receive_task, rtcp_task, audio_receive_task, audio_rtcp_task):
            if task is not None:
                task.cancel()
        for task in (receive_task, rtcp_task, audio_receive_task, audio_rtcp_task):
            if task is not None:
                try:
                    await task
                except (asyncio.CancelledError, OSError):
                    pass
        if service is not None:
            # The stopAll already went out above; only the channel is left to close.
            try:
                await asyncio.wait_for(service.close(), timeout=2.0)
            except (asyncio.TimeoutError, OSError, ConnectionError):
                pass
        if transport is not None:
            transport.close()
        if audio_transport is not None:
            audio_transport.close()
        if relay_socket is not None:
            relay_socket.close()
        if audio_relay_socket is not None:
            audio_relay_socket.close()
        if audio_service is not None:
            try:
                await asyncio.wait_for(audio_service.close(), timeout=2.0)
            except (asyncio.TimeoutError, OSError, ConnectionError):
                pass
        return {"state": "stopped", "packetsRelayed": self._packets_received}

    @staticmethod
    async def _stop_all_media_streams(service: Any) -> dict[str, Any]:
        """Stop the whole paired AVConference session on DisplayService.

        The service's stop action is keyed by ``stopAll``. Sending only the
        client-session UUID can leave the phone's media daemon wedged and unable
        to accept another DisplayService channel until the device is restarted.
        The daemon commonly closes RemoteXPC while processing a successful stop.
        """
        try:
            return await service.invoke(
                "com.apple.coredevice.feature.stopmediastream",
                {"stopAll": True},
                action_identifier="com.apple.coredevice.action.mediastreamstop",
            )
        except (asyncio.IncompleteReadError, ConnectionResetError, BrokenPipeError):
            return {"stopped": True}

    async def request_keyframe(self) -> dict[str, Any]:
        self._require_video()
        if (
            self._video_transport is None
            or self._sender_ip is None
            or self._rtcp_port == 0
            or self._local_ssrc == 0
            or self._remote_ssrc == 0
        ):
            raise ProtocolError("feedback-unavailable", "The video stream did not provide RTCP feedback details.")

        now = asyncio.get_running_loop().time()
        if now - self._last_keyframe_request < 0.7:
            return {"state": "rate-limited"}

        self._fir_sequence = (self._fir_sequence + 1) & 0xFF
        # RFC 5104 Full Intra Request: PSFB/FMT=4 with one FCI entry. iOS 27
        # honours FIR when allowRTCPFB is negotiated; plain PLI is ignored.
        packet = struct.pack(
            "!BBHIIIB3x",
            0x84,
            206,
            4,
            self._local_ssrc & 0xFFFFFFFF,
            0,
            self._remote_ssrc & 0xFFFFFFFF,
            self._fir_sequence,
        )
        await self._video_transport.sendto(packet, self._sender_ip, self._rtcp_port)
        self._last_keyframe_request = now
        return {"state": "requested", "sequence": self._fir_sequence}

    async def touch(self, state: str, x: int, y: int) -> dict[str, Any]:
        self._validate_coordinate(x, "x")
        self._validate_coordinate(y, "y")
        from pymobiledevice3.remote.core_device.hid_service import (
            TOUCHSCREEN_STATE_CONTACT,
            TOUCHSCREEN_STATE_RELEASE,
        )

        hid = await self._ensure_universal_hid()
        states = {
            "contact": TOUCHSCREEN_STATE_CONTACT,
            "release": TOUCHSCREEN_STATE_RELEASE,
        }
        if state not in states:
            raise ProtocolError("invalid-argument", "touch state must be contact or release.")
        await hid.send_touchscreen(states[state], x, y)
        self._last_touch = (x, y)
        return {"state": state, "x": x, "y": y}

    async def drag(
        self,
        x1: int,
        y1: int,
        x2: int,
        y2: int,
        steps: int,
        duration: float,
        settle: float = 0.0,
    ) -> dict[str, Any]:
        """Drag from one point to another, optionally holding still before releasing.

        iOS derives fling velocity from the last samples before the release, so a
        drag that stops moving and lifts immediately is read as a throw and the
        content keeps scrolling afterwards. Holding the contact stationary for a
        moment first brings that velocity back to zero, which is what makes a
        mouse wheel stop when the user stops.
        """
        for name, value in (("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2)):
            self._validate_coordinate(value, name)
        if not 1 <= steps <= 240:
            raise ProtocolError("invalid-argument", "steps must be between 1 and 240.")
        if not 0.0 <= duration <= 5.0:
            raise ProtocolError("invalid-argument", "duration must be between 0 and 5 seconds.")
        if not 0.0 <= settle <= 2.0:
            raise ProtocolError("invalid-argument", "settle must be between 0 and 2 seconds.")

        from pymobiledevice3.remote.core_device.hid_service import (
            TOUCHSCREEN_STATE_CONTACT,
            TOUCHSCREEN_STATE_RELEASE,
        )

        hid = await self._ensure_universal_hid()
        delay = duration / steps
        for step in range(steps + 1):
            progress = step / steps
            x = round(x1 + ((x2 - x1) * progress))
            y = round(y1 + ((y2 - y1) * progress))
            await hid.send_touchscreen(TOUCHSCREEN_STATE_CONTACT, x, y)
            if step < steps and delay > 0:
                await asyncio.sleep(delay)
        if settle > 0:
            holds = max(2, min(8, round(settle / 0.02)))
            for _ in range(holds):
                await asyncio.sleep(settle / holds)
                await hid.send_touchscreen(TOUCHSCREEN_STATE_CONTACT, x2, y2)
        await hid.send_touchscreen(TOUCHSCREEN_STATE_RELEASE, x2, y2)
        self._last_touch = (x2, y2)
        return {"state": "released", "x": x2, "y": y2}

    async def type_text(self, text: str) -> dict[str, Any]:
        if len(text) > 4096:
            raise ProtocolError("invalid-argument", "text is limited to 4096 characters per command.")
        from pymobiledevice3.remote.core_device.hid_service import ASCII_TO_HID, KEY_LEFT_SHIFT

        hid = await self._ensure_universal_hid()
        keyboard_id = await self._ensure_keyboard(hid)
        for character in text:
            mapping = ASCII_TO_HID.get(character)
            if mapping is None:
                raise ProtocolError("unsupported-key", f"The virtual keyboard cannot type {character!r}.")
            usage, shifted = mapping
            usages = (KEY_LEFT_SHIFT, usage) if shifted else (usage,)
            await hid.send_keyboard(keyboard_id, usages)
            await asyncio.sleep(0.02)
            await hid.send_keyboard(keyboard_id, ())
            await asyncio.sleep(0.01)
        return {"characters": len(text)}

    async def key(self, name: str) -> dict[str, Any]:
        from pymobiledevice3.remote.core_device import hid_service

        keys = {
            "enter": hid_service.KEY_ENTER,
            "escape": hid_service.KEY_ESC,
            "backspace": hid_service.KEY_BACKSPACE,
            "tab": hid_service.KEY_TAB,
            "left": hid_service.KEY_LEFT,
            "right": hid_service.KEY_RIGHT,
            "up": hid_service.KEY_UP,
            "down": hid_service.KEY_DOWN,
        }
        if name not in keys:
            raise ProtocolError("invalid-argument", f"Unknown key: {name}.")
        hid = await self._ensure_universal_hid()
        keyboard_id = await self._ensure_keyboard(hid)
        await hid.send_keyboard(keyboard_id, (keys[name],))
        await asyncio.sleep(0.03)
        await hid.send_keyboard(keyboard_id, ())
        return {"key": name}

    async def button(self, name: str) -> dict[str, Any]:
        from pymobiledevice3.remote.core_device.hid_service import HID_BUTTON_STATE_DOWN, HID_BUTTON_STATE_UP

        # Lock is a tap, not a hold. Held for 0.5 s the phone stayed awake and raised the
        # volume HUD instead; at 0.05 s it slept every time, verified by streaming across
        # the transition and decoding the lock screen out of the video. Only Siri wants a
        # genuine long press.
        buttons = {
            "home": (0x0C, 0x40, 0.05),
            "lock": (0x0C, 0x30, 0.05),
            "volume-up": (0x0C, 0xE9, 0.05),
            "volume-down": (0x0C, 0xEA, 0.05),
            "mute": (0x0C, 0xE2, 0.05),
            "siri": (0x0C, 0xCF, 1.0),
        }
        if name not in buttons:
            raise ProtocolError("invalid-argument", f"Unknown hardware button: {name}.")
        indigo = await self._ensure_indigo_hid()
        usage_page, usage_code, hold = buttons[name]
        await indigo.send_button(usage_page, usage_code, HID_BUTTON_STATE_DOWN)
        await asyncio.sleep(hold)
        await indigo.send_button(usage_page, usage_code, HID_BUTTON_STATE_UP)
        return {"button": name}

    async def usb_status(self) -> dict[str, Any]:
        """Is an iPhone on the cable, and is Developer Mode already on?

        Deliberately USB only. AMFI is a lockdown service reached over usbmux, and
        the whole point of this call is to answer questions about a device that
        cannot yet be reached any other way -- before Developer Mode is on there is
        no CoreDevice tunnel to ask.
        """
        from pymobiledevice3.lockdown import create_using_usbmux

        try:
            lockdown = await create_using_usbmux(autopair=True)
        except Exception:
            return {"present": False}

        try:
            return {
                "present": True,
                "udid": lockdown.udid,
                "deviceName": lockdown.all_values.get("DeviceName", "iPhone"),
                "productVersion": lockdown.product_version,
                "developerMode": bool(await lockdown.get_developer_mode_status()),
            }
        finally:
            with contextlib.suppress(Exception):
                await lockdown.close()

    async def enable_developer_mode(self) -> dict[str, Any]:
        """Turn on Developer Mode, restart the phone, and answer the prompt that follows.

        This is why the setup flow does not need to drive Settings by hand. AMFI
        exposes the whole sequence as a lockdown service: reveal the toggle, request
        the change, ride out the reboot, and confirm afterwards.

        Takes as long as a reboot, so callers must not put a short deadline on it.
        """
        from pymobiledevice3.exceptions import DeviceHasPasscodeSetError
        from pymobiledevice3.lockdown import create_using_usbmux
        from pymobiledevice3.services.amfi import AmfiService

        lockdown = await create_using_usbmux(autopair=True)
        if await lockdown.get_developer_mode_status():
            with contextlib.suppress(Exception):
                await lockdown.close()
            return {"developerMode": True, "restarted": False}

        service = AmfiService(lockdown)
        await service.reveal_developer_mode_option_in_ui()
        try:
            await service.enable_developer_mode(enable_post_restart=True)
        except DeviceHasPasscodeSetError:
            raise ProtocolError(
                "passcode-set",
                "iOS will not let a computer turn on Developer Mode while the iPhone has a "
                "passcode. Turn the passcode off, or enable Developer Mode by hand in "
                "Settings > Privacy & Security.",
            ) from None
        return {"developerMode": True, "restarted": True}

    async def silence(self, steps: int = 16) -> dict[str, Any]:
        """Hold the device volume down until it reaches zero.

        There is no CoreDevice action that sets a volume level, so the only lever is
        the hardware button, pressed enough times to walk any starting level down to
        silence. iOS has sixteen steps, so sixteen presses is unconditional rather
        than a guess about where the volume started.

        This changes the phone's real volume and cannot be undone from here: nothing
        reports the level it had before, so there is nothing to restore.
        """
        from pymobiledevice3.remote.core_device.hid_service import HID_BUTTON_STATE_DOWN, HID_BUTTON_STATE_UP

        steps = max(1, min(steps, 32))
        indigo = await self._ensure_indigo_hid()
        for _ in range(steps):
            await indigo.send_button(0x0C, 0xEA, HID_BUTTON_STATE_DOWN)
            await asyncio.sleep(0.02)
            await indigo.send_button(0x0C, 0xEA, HID_BUTTON_STATE_UP)
            await asyncio.sleep(0.03)
        return {"silenced": True, "steps": steps}

    async def set_screen_curtain(self, enabled: bool) -> dict[str, Any]:
        self._require_video()
        hid = await self._ensure_universal_hid()
        if enabled == self._screen_curtain_assumed:
            return await self.privacy_status()

        from pymobiledevice3.remote.core_device.hid_service import (
            ASCII_TO_HID,
            KEY_LEFT_ALT,
            KEY_LEFT_CTRL,
            KEY_LEFT_SHIFT,
        )

        if enabled:
            self._voice_over_was_enabled = bool(await self._rsd.get_voice_over())
            if not self._voice_over_was_enabled:
                await self._rsd.set_voice_over(True)
                await asyncio.sleep(0.5)

        keyboard_id = await self._ensure_keyboard(hid)
        s_usage, _ = ASCII_TO_HID["s"]
        await hid.send_keyboard(
            keyboard_id,
            (KEY_LEFT_CTRL, KEY_LEFT_ALT, KEY_LEFT_SHIFT, s_usage),
        )
        await asyncio.sleep(0.08)
        await hid.send_keyboard(keyboard_id, ())
        await asyncio.sleep(0.2)
        self._screen_curtain_assumed = enabled

        if not enabled and self._voice_over_was_enabled is False:
            await self._rsd.set_voice_over(False)
            self._voice_over_was_enabled = None
        return await self.privacy_status()

    async def privacy_status(self) -> dict[str, Any]:
        self._require_connected()
        return {
            "voiceOver": bool(await self._rsd.get_voice_over()),
            "screenCurtainAssumed": self._screen_curtain_assumed,
            "screenCurtainQueryable": False,
            "verificationRequired": True,
        }

    async def _ensure_universal_hid(self) -> Any:
        self._require_video()
        await self._wait_for_hid_gate()
        if self._universal_hid is None:
            from pymobiledevice3.remote.core_device.hid_service import UniversalHIDServiceService

            hid = UniversalHIDServiceService(self._rsd)
            await hid.connect()
            self._universal_hid = hid
        return self._universal_hid

    async def _ensure_indigo_hid(self) -> Any:
        self._require_video()
        await self._wait_for_hid_gate()
        if self._indigo_hid is None:
            from pymobiledevice3.remote.core_device.hid_service import IndigoHIDService

            hid = IndigoHIDService(self._rsd)
            await hid.connect()
            self._indigo_hid = hid
        return self._indigo_hid

    async def _ensure_keyboard(self, hid: Any) -> int:
        if self._keyboard_service_id is None:
            self._keyboard_service_id = int(await hid.create_keyboard_service())
        return self._keyboard_service_id

    async def _wait_for_hid_gate(self) -> None:
        remaining = 0.3 - (asyncio.get_running_loop().time() - self._video_started_at)
        if remaining > 0:
            await asyncio.sleep(remaining)

    async def _stop_hid(self) -> None:
        if self._universal_hid is not None:
            try:
                if self._screen_curtain_assumed:
                    from pymobiledevice3.remote.core_device.hid_service import (
                        ASCII_TO_HID,
                        KEY_LEFT_ALT,
                        KEY_LEFT_CTRL,
                        KEY_LEFT_SHIFT,
                    )

                    keyboard_id = await self._ensure_keyboard(self._universal_hid)
                    s_usage, _ = ASCII_TO_HID["s"]
                    await self._universal_hid.send_keyboard(
                        keyboard_id,
                        (KEY_LEFT_CTRL, KEY_LEFT_ALT, KEY_LEFT_SHIFT, s_usage),
                    )
                    await asyncio.sleep(0.08)
                    await self._universal_hid.send_keyboard(keyboard_id, ())
                    self._screen_curtain_assumed = False
                if self._keyboard_service_id is not None:
                    await self._universal_hid.send_keyboard(self._keyboard_service_id, ())
                from pymobiledevice3.remote.core_device.hid_service import TOUCHSCREEN_STATE_RELEASE

                await self._universal_hid.send_touchscreen(TOUCHSCREEN_STATE_RELEASE, *self._last_touch)
            except Exception:
                pass
            try:
                await self._universal_hid.close()
            except Exception:
                pass
        if self._indigo_hid is not None:
            try:
                await self._indigo_hid.close()
            except Exception:
                pass
        self._universal_hid = None
        self._indigo_hid = None
        self._keyboard_service_id = None
        if self._voice_over_was_enabled is False:
            try:
                await self._rsd.set_voice_over(False)
            except Exception:
                pass
        self._voice_over_was_enabled = None

    def _require_video(self) -> None:
        if self._video_service is None:
            raise ProtocolError("video-required", "Start the video stream before sending input.")

    @staticmethod
    def _validate_coordinate(value: int, name: str) -> None:
        if not 0 <= value <= 65535:
            raise ProtocolError("invalid-argument", f"{name} must be between 0 and 65535.")

    async def _relay_video(self) -> None:
        assert self._video_transport is not None
        assert self._relay_socket is not None
        assert self._relay_target is not None
        loop = asyncio.get_running_loop()
        while True:
            datagram = await self._video_transport.recv(65535)
            if len(datagram) < 12 or datagram[0] >> 6 != 2:
                continue
            payload_type = datagram[1] & 0x7F
            if 64 <= payload_type <= 95:
                # The CoreDevice media socket multiplexes RTCP with RTP. Relaying control
                # packets into the video pipe makes their marker/subtype bits look like
                # synthetic HEVC frame boundaries, so consume them here instead.
                continue
            sequence = int.from_bytes(datagram[2:4], "big")
            self._last_rtp_timestamp = int.from_bytes(datagram[4:8], "big")
            self._packets_in_frame += 1
            if self._last_sequence is not None and sequence < self._last_sequence and self._last_sequence - sequence > 0x8000:
                self._highest_sequence = (self._highest_sequence & 0xFFFF0000) + 0x10000
            self._last_sequence = sequence
            self._highest_sequence = (self._highest_sequence & 0xFFFF0000) | sequence
            self._packets_received += 1
            if datagram[1] & 0x80:
                self._frames_received += 1
                self._last_frame_packet_count = self._packets_in_frame
                self._packets_in_frame = 0
                # Xcode's receiver sends one RTCP APP receipt at every frame
                # boundary. The timestamp must echo the frame just received;
                # synthetic timestamps are ignored by iOS rate control.
                if self._rctl_enabled and self._rtcp_port and self._local_ssrc:
                    await self._video_transport.sendto(
                        self._build_rctl_receipt(),
                        self._sender_ip,
                        self._rtcp_port,
                    )
            await loop.sock_sendto(self._relay_socket, datagram, self._relay_target)

    async def _send_video_rtcp(self) -> None:
        tick = 0
        while True:
            await asyncio.sleep(0.05)
            if (
                self._video_transport is None
                or self._sender_ip is None
                or self._rtcp_port == 0
                or self._local_ssrc == 0
                or self._remote_ssrc == 0
                or self._packets_received == 0
            ):
                continue
            # AVConference sends the private RCTL report at roughly 20 Hz.
            # Keep the standards-based RR once per second for the negotiated
            # RTCP timeout as well.
            if self._rctl_enabled:
                await self._video_transport.sendto(
                    self._build_rctl_report(),
                    self._sender_ip,
                    self._rtcp_port,
                )
            if tick % 20 == 0:
                await self._video_transport.sendto(
                    self._build_rtcp_receiver_report(),
                    self._sender_ip,
                    self._rtcp_port,
                )
            tick += 1

    async def _receive_audio_for_session_parity(self) -> None:
        assert self._audio_transport is not None
        loop = asyncio.get_running_loop()
        while True:
            datagram = await self._audio_transport.recv(65535)
            if len(datagram) < 12 or datagram[0] >> 6 != 2:
                continue
            payload_type = datagram[1] & 0x7F
            if 64 <= payload_type <= 95:
                continue
            sequence = int.from_bytes(datagram[2:4], "big")
            if (
                self._audio_last_sequence is not None
                and sequence < self._audio_last_sequence
                and self._audio_last_sequence - sequence > 0x8000
            ):
                self._audio_highest_sequence = (self._audio_highest_sequence & 0xFFFF0000) + 0x10000
            self._audio_last_sequence = sequence
            self._audio_highest_sequence = (self._audio_highest_sequence & 0xFFFF0000) | sequence
            self._audio_packets_received += 1
            if self._audio_relay_socket is not None and self._audio_relay_target is not None:
                await loop.sock_sendto(
                    self._audio_relay_socket,
                    datagram,
                    self._audio_relay_target,
                )

    async def _send_audio_rtcp(self) -> None:
        while True:
            await asyncio.sleep(1.0)
            if (
                self._audio_transport is None
                or self._sender_ip is None
                or self._audio_rtcp_port == 0
                or self._audio_local_ssrc == 0
                or self._audio_remote_ssrc == 0
                or self._audio_packets_received == 0
            ):
                continue
            await self._audio_transport.sendto(
                self._build_receiver_report(
                    self._audio_local_ssrc,
                    self._audio_remote_ssrc,
                    self._audio_highest_sequence,
                ),
                self._sender_ip,
                self._audio_rtcp_port,
            )

    async def _keep_awake(self) -> None:
        from pymobiledevice3.services.power_assertion import PowerAssertionService

        # The assertion is held for as long as the stream runs, rather than entered and immediately
        # released while relying on its own 300-second timeout to outlive the renewal loop. With
        # that shape, cancelling this task only stopped the renewals: the last assertion the phone
        # had been given kept it awake for up to five more minutes after the window closed. Exiting
        # the context on cancellation releases it, so closing Orchard hands the phone straight back
        # to its own sleep schedule. Renewed a minute before expiry rather than after it.
        while True:
            try:
                service = PowerAssertionService(self._rsd)
                async with service.create_power_assertion(
                    "PreventUserIdleSystemSleep",
                    "Orchard Mirror",
                    300,
                    "Keep the unlocked iPhone awake while its screen is mirrored",
                ):
                    await asyncio.sleep(240.0)
            except asyncio.CancelledError:
                return
            except Exception as error:
                LOGGER.warning("keep-awake renewal failed: %s", error)
                try:
                    await asyncio.sleep(120.0)
                except asyncio.CancelledError:
                    return

    def _build_rtcp_receiver_report(self) -> bytes:
        return self._build_receiver_report(
            self._local_ssrc,
            self._remote_ssrc,
            self._highest_sequence,
        )

    @staticmethod
    def _build_receiver_report(local_ssrc: int, remote_ssrc: int, highest_sequence: int) -> bytes:
        # RFC 3550 sections 6.4.2 and 6.5: one RR report block followed by
        # the mandatory compound-packet SDES/CNAME chunk.
        receiver_report = struct.pack(
            "!BBHII4B4I",
            0x81,
            201,
            7,
            local_ssrc & 0xFFFFFFFF,
            remote_ssrc & 0xFFFFFFFF,
            0,
            0,
            0,
            0,
            highest_sequence & 0xFFFFFFFF,
            0,
            0,
            0,
        )
        sdes = struct.pack(
            "!BBHI4B",
            0x81,
            202,
            2,
            local_ssrc & 0xFFFFFFFF,
            1,
            0,
            0,
            0,
        )
        return receiver_report + sdes

    def _build_rctl_report(self) -> bytes:
        """Build the AVConference RTCP APP RCTL packet used by Xcode Mirror."""
        # AVConference's 20-byte RCTL body is not timestamp-based. Captures of
        # DeviceHub and idevice's independent implementation agree on these
        # fields: received frame count, local millisecond clock, and the highest
        # RTP sequence number. The other interval metrics are zero on a lossless
        # USB tunnel.
        return struct.pack(
            "!BBHI4sIHHIHHHH",
            0x80,
            204,
            7,
            self._local_ssrc & 0xFFFFFFFF,
            b"RCTL",
            0x85000004,
            self._frames_received & 0xFFFF,
            0,
            0,
            int(time.monotonic() * 1000) & 0xFFFF,
            0,
            self._highest_sequence & 0xFFFF,
            0,
        )

    def _build_rctl_receipt(self) -> bytes:
        """Build Xcode's per-frame RTCP APP receipt (application name 5)."""
        return struct.pack(
            "!BBHIII",
            0x80,
            204,
            3,
            self._local_ssrc & 0xFFFFFFFF,
            5,
            self._last_rtp_timestamp & 0xFFFFFFFF,
        )

    async def disconnect(self) -> dict[str, Any]:
        await self.stop_video()
        tunnel, self._tunnel = self._tunnel, None
        self._rsd = None
        self._udid = None
        self._transport = None
        if tunnel is not None:
            await tunnel.aclose()
        return {"state": "disconnected"}

    def _require_connected(self) -> None:
        if not self.connected:
            raise ProtocolError("not-connected", "Connect to an iPhone before using this command.")


class Agent:
    """Dispatch protocol commands serially on the tunnel's event loop."""

    def __init__(self) -> None:
        self.session = CoreDeviceSession()
        self.stopping = False
        # Requests run concurrently, so commands that mutate the session must not
        # interleave with each other, and HID traffic must stay ordered. Anything
        # else -- notably request-keyframe -- is deliberately left unlocked so a
        # drag in flight can never delay stream recovery or teardown.
        self._lifecycle_lock = asyncio.Lock()
        self._input_lock = asyncio.Lock()

    async def execute(self, request: Request) -> dict[str, Any]:
        if request.command in _LIFECYCLE_COMMANDS:
            # Always lifecycle before input: a single ordering keeps teardown from
            # deadlocking against an HID command that is already running.
            async with self._lifecycle_lock, self._input_lock:
                return await self._dispatch(request)
        if request.command in _INPUT_COMMANDS:
            async with self._input_lock:
                return await self._dispatch(request)
        return await self._dispatch(request)

    async def _dispatch(self, request: Request) -> dict[str, Any]:
        command = request.command
        arguments = request.arguments
        if command == "hello":
            return {
                "agentVersion": AGENT_VERSION,
                "protocolVersion": PROTOCOL_VERSION,
                "commands": [
                    "hello",
                    "mount-ddi",
                    "usb-status",
                    "enable-developer-mode",
                    "connect",
                    "device-info",
                    "lockstate",
                    "start-video",
                    "stop-video",
                    "request-keyframe",
                    "touch",
                    "drag",
                    "type",
                    "key",
                    "button",
                    "silence",
                    "screen-curtain",
                    "privacy-status",
                    "disconnect",
                    "stop",
                ],
            }
        if command == "mount-ddi":
            return await self.session.mount_ddi(_optional_string(arguments, "udid"))
        if command == "usb-status":
            return await self.session.usb_status()
        if command == "enable-developer-mode":
            return await self.session.enable_developer_mode()
        if command == "connect":
            return await self.session.connect(_optional_string(arguments, "udid"))
        if command == "device-info":
            return await self.session.device_info()
        if command == "lockstate":
            return await self.session.lockstate()
        if command == "start-video":
            return await self.session.start_video(
                _required_string(arguments, "relayHost"),
                _required_int(arguments, "relayPort"),
                _optional_int(arguments, "displayId", 1),
                audio_relay_host=_optional_string(arguments, "audioRelayHost"),
                audio_relay_port=_optional_int(arguments, "audioRelayPort", 0) or None,
                paired_audio_enabled=arguments.get("pairedAudioEnabled", False) is True,
            )
        if command == "stop-video":
            return await self.session.stop_video()
        if command == "request-keyframe":
            return await self.session.request_keyframe()
        if command == "touch":
            return await self.session.touch(
                _required_string(arguments, "state"),
                _required_int(arguments, "x"),
                _required_int(arguments, "y"),
            )
        if command == "drag":
            return await self.session.drag(
                _required_int(arguments, "x1"),
                _required_int(arguments, "y1"),
                _required_int(arguments, "x2"),
                _required_int(arguments, "y2"),
                _optional_int(arguments, "steps", 20),
                _optional_number(arguments, "duration", 0.25),
                _optional_number(arguments, "settle", 0.0),
            )
        if command == "type":
            return await self.session.type_text(_required_string(arguments, "text"))
        if command == "key":
            return await self.session.key(_required_string(arguments, "name"))
        if command == "button":
            return await self.session.button(_required_string(arguments, "name"))
        if command == "silence":
            return await self.session.silence(int(_optional_number(arguments, "steps", 16)))
        if command == "screen-curtain":
            return await self.session.set_screen_curtain(_required_bool(arguments, "enabled"))
        if command == "privacy-status":
            return await self.session.privacy_status()
        if command == "disconnect":
            return await self.session.disconnect()
        if command == "stop":
            # Acknowledge immediately and let the read loop cancel outstanding work
            # before disconnecting. Tearing the session down here instead would race
            # whatever else is still running and can block the supervisor's exit wait.
            self.stopping = True
            return {"state": "stopping"}
        raise ProtocolError("unknown-command", f"Unknown command: {command}.")


def _optional_string(arguments: dict[str, Any], name: str) -> str | None:
    value = arguments.get(name)
    if value is None:
        return None
    if not isinstance(value, str) or not value:
        raise ProtocolError("invalid-argument", f"{name} must be a non-empty string or null.")
    return value


def _required_string(arguments: dict[str, Any], name: str) -> str:
    value = _optional_string(arguments, name)
    if value is None:
        raise ProtocolError("invalid-argument", f"{name} is required.")
    return value


def _required_int(arguments: dict[str, Any], name: str) -> int:
    value = arguments.get(name)
    if isinstance(value, bool) or not isinstance(value, int):
        raise ProtocolError("invalid-argument", f"{name} must be an integer.")
    return value


def _optional_int(arguments: dict[str, Any], name: str, default: int) -> int:
    if name not in arguments:
        return default
    return _required_int(arguments, name)


def _optional_number(arguments: dict[str, Any], name: str, default: float) -> float:
    value = arguments.get(name, default)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ProtocolError("invalid-argument", f"{name} must be a number.")
    return float(value)


def _required_bool(arguments: dict[str, Any], name: str) -> bool:
    value = arguments.get(name)
    if not isinstance(value, bool):
        raise ProtocolError("invalid-argument", f"{name} must be true or false.")
    return value


def _write_response(message: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(message, separators=(",", ":"), ensure_ascii=False) + "\n")
    sys.stdout.flush()


async def _readline() -> str:
    return await asyncio.to_thread(sys.stdin.readline)


def _write_error(request_id: str, code: str, message: str) -> None:
    _write_response(
        {
            "version": PROTOCOL_VERSION,
            "id": request_id,
            "ok": False,
            "error": {"code": code, "message": message},
        }
    )


async def _run_request(agent: "Agent", request: Request) -> None:
    """Execute one request and write its response; never raises to the read loop."""
    try:
        result = await agent.execute(request)
        _write_response(
            {
                "version": PROTOCOL_VERSION,
                "id": request.request_id,
                "ok": True,
                "result": _json_safe(result),
            }
        )
    except ProtocolError as error:
        _write_error(request.request_id, error.code, str(error))
    except asyncio.CancelledError:
        _write_error(request.request_id, "agent-stopping", "The agent stopped before the command completed.")
        raise
    except BaseException as error:
        safe_error = _classify_error(error)
        if safe_error.code == "agent-error":
            LOGGER.exception("command failed")
        else:
            LOGGER.warning("%s: %s", safe_error.code, safe_error)
        _write_error(request.request_id, safe_error.code, str(safe_error))


async def run() -> int:
    """Read commands until EOF or `stop`, preserving one event loop per session.

    Requests are dispatched concurrently. Handling them one at a time made every
    command wait behind the one before it, so a quarter-second drag delayed the
    keyframe requests that recover the picture, and teardown queued behind
    whatever input happened to be in flight.
    """
    agent = Agent()
    running: set[asyncio.Task[None]] = set()
    try:
        while True:
            line = await _readline()
            if not line:
                break
            try:
                request = Request.parse(line)
            except ProtocolError as error:
                _write_error("", error.code, str(error))
                continue

            if request.command == "stop":
                # Handled inline so the loop exits without blocking on another
                # read that would only arrive when the supervisor closes stdin.
                await _run_request(agent, request)
                break

            task = asyncio.create_task(_run_request(agent, request))
            running.add(task)
            task.add_done_callback(running.discard)
    finally:
        if running:
            # A stop-video still in flight is finishing the exchange that ends the phone's media
            # session. Cancelling it outright -- which is what used to happen the moment `stop`
            # arrived -- could land inside the stopAll and lose it, leaving the media daemon
            # holding a session nothing will end. Bounded, so a wedged request still cannot stop
            # the process exiting.
            _, pending = await asyncio.wait(running, timeout=5.0)
            for task in pending:
                task.cancel()
            if pending:
                await asyncio.gather(*pending, return_exceptions=True)
        try:
            # A tunnel that died with the cable cannot be closed politely. Exiting promptly
            # matters more than a clean CoreDevice goodbye, but this is also the last chance for
            # a stopAll that the supervisor abandoned, so it is not the 1.5 s it once was.
            await asyncio.wait_for(agent.session.disconnect(), timeout=2.5)
        except (asyncio.TimeoutError, Exception):
            LOGGER.info("session teardown did not complete; exiting anyway")
    return 0


def main() -> int:
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s")
    try:
        return asyncio.run(run())
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
