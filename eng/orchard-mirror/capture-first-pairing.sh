#!/usr/bin/env bash
#
# Capture one first-time iPhone Mirroring pairing attempt on a physical Mac.
# Run as the signed-in desktop user, not as root. The script asks sudo only for
# packet capture and privileged unified-log access.

set -uo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf 'error: this capture script must run on macOS.\n' >&2
  exit 2
fi

if [[ "${EUID}" -eq 0 ]]; then
  printf 'error: run this script as the signed-in desktop user, without sudo.\n' >&2
  printf '       The script will request sudo for individual capture commands.\n' >&2
  exit 2
fi

app_path="/System/Applications/iPhone Mirroring.app"
app_executable="${app_path}/Contents/MacOS/iPhone Mirroring"
if [[ ! -x "${app_executable}" ]]; then
  printf 'error: iPhone Mirroring is not installed at the expected path.\n' >&2
  printf '       A supported Mac running macOS Sequoia 15 or later is required.\n' >&2
  exit 2
fi

if [[ "${1:-}" == "--self-test" ]]; then
  missing_tools=0
  for required_tool in \
    awk codesign defaults dns-sd file ifconfig lsof log networksetup open \
    pgrep route scutil shasum stat sysctl tar tcpdump; do
    if ! command -v "${required_tool}" >/dev/null 2>&1; then
      printf 'missing: %s\n' "${required_tool}" >&2
      missing_tools=1
    fi
  done
  printf 'app: %s\n' "${app_executable}"
  printf 'architecture: %s\n' "$(uname -m)"
  printf 'hardware model: %s\n' "$(sysctl -n hw.model 2>/dev/null || printf unknown)"
  printf 'interfaces: %s\n' "$(ifconfig -l 2>/dev/null || printf unknown)"
  if [[ "${missing_tools}" -ne 0 ]]; then
    exit 4
  fi
  printf 'self-test: passed\n'
  exit 0
fi

output_parent="${1:-${PWD}/artifacts/orchard-mirror/captures}"
session_id="$(date -u '+%Y%m%dT%H%M%SZ')-$$"
capture_root="${output_parent}/first-pairing-${session_id}"
raw_root="${capture_root}/private-raw"
summary_root="${capture_root}/summary"

umask 077
mkdir -p "${raw_root}" "${summary_root}"
chmod 700 "${capture_root}" "${raw_root}" "${summary_root}"

capture_pids=()
cleanup_started=0
watchdog_pid=""

stop_captures() {
  if [[ "${cleanup_started}" -eq 1 ]]; then
    return
  fi
  cleanup_started=1

  printf '\nStopping capture processes...\n'
  if [[ -n "${watchdog_pid}" ]]; then
    kill "${watchdog_pid}" >/dev/null 2>&1 || true
  fi
  for capture_pid in "${capture_pids[@]}"; do
    sudo -n kill -INT "${capture_pid}" >/dev/null 2>&1 ||
      kill -INT "${capture_pid}" >/dev/null 2>&1 ||
      true
  done
  sleep 1
  for capture_pid in "${capture_pids[@]}"; do
    wait "${capture_pid}" >/dev/null 2>&1 || true
  done
}

handle_interrupt() {
  stop_captures
  printf 'Capture interrupted. Partial data remains at:\n%s\n' "${capture_root}" >&2
  exit 130
}

trap handle_interrupt INT TERM HUP

capture_command() {
  local destination="$1"
  local heading="$2"
  shift 2
  {
    printf '## %s\n\n' "${heading}"
    printf '$'
    printf ' %q' "$@"
    printf '\n\n'
    "$@"
    local command_status=$?
    printf '\n[exit status: %s]\n' "${command_status}"
  } >>"${destination}" 2>&1
}

collect_snapshot() {
  local phase="$1"
  local destination="${raw_root}/${phase}-state.txt"
  : >"${destination}"

  capture_command "${destination}" "UTC time" date -u '+%Y-%m-%dT%H:%M:%SZ'
  capture_command "${destination}" "macOS version" sw_vers
  capture_command "${destination}" "machine architecture" uname -m
  capture_command "${destination}" "hardware model" sysctl -n hw.model
  capture_command "${destination}" "network reachability" scutil --nwi
  capture_command "${destination}" "network hardware ports" networksetup -listallhardwareports
  capture_command "${destination}" "interfaces" ifconfig -a
  capture_command "${destination}" "ScreenContinuity preferences" \
    defaults read com.apple.ScreenContinuity
  capture_command "${destination}" "ScreenContinuityUI preferences" \
    defaults read com.apple.ScreenContinuityUI
  capture_command "${destination}" "iPhone Mirroring app signature" \
    codesign -dvvv "${app_executable}"
  capture_command "${destination}" "iPhone Mirroring app entitlements" \
    codesign -d --entitlements :- "${app_executable}"
}

start_root_capture() {
  local label="$1"
  shift
  local stderr_path="${raw_root}/${label}.stderr.txt"
  local stdout_path="${raw_root}/${label}.stdout.txt"

  sudo -n "$@" >"${stdout_path}" 2>"${stderr_path}" &
  local capture_pid=$!
  capture_pids+=("${capture_pid}")
  printf '%s\t%s\n' "${capture_pid}" "$*" >>"${raw_root}/capture-processes.tsv"
}

start_user_capture() {
  local label="$1"
  shift
  local stderr_path="${raw_root}/${label}.stderr.txt"
  local stdout_path="${raw_root}/${label}.stdout.txt"

  "$@" >"${stdout_path}" 2>"${stderr_path}" &
  local capture_pid=$!
  capture_pids+=("${capture_pid}")
  printf '%s\t%s\n' "${capture_pid}" "$*" >>"${raw_root}/capture-processes.tsv"
}

interface_exists() {
  ifconfig "$1" >/dev/null 2>&1
}

printf 'Orchard Mirror first-pairing capture\n'
printf '====================================\n\n'
printf 'This records private packet data and system logs from your Mac.\n'
printf 'It does NOT inspect keychains, dump credentials, or upload anything.\n'
printf 'Treat the resulting private-raw directory and archive as sensitive.\n\n'
printf 'Before continuing:\n'
printf '  1. Sign the Mac and iPhone into the same Apple Account with 2FA.\n'
printf '  2. Turn on Wi-Fi and Bluetooth on both devices.\n'
printf '  3. On iPhone, revoke this Mac under Settings > General >\n'
printf '     AirPlay & Continuity > iPhone Mirroring, if it is already listed.\n'
printf '  4. Quit iPhone Mirroring on the Mac and lock the nearby iPhone.\n'
printf '  5. Close unrelated network-heavy or privacy-sensitive applications.\n\n'
read -r -p 'Press Enter when those steps are complete, or Ctrl-C to stop. ' _

printf '\nAuthorizing privileged capture commands...\n'
if ! sudo -v; then
  printf 'error: sudo authorization failed.\n' >&2
  exit 3
fi

collect_snapshot "before"
: >"${raw_root}/capture-processes.tsv"

log_predicate='process == "iPhone Mirroring" OR process == "sharingd" OR process == "rapportd" OR process == "identityservicesd" OR process == "securityd" OR process == "trustd" OR process == "wifip2pd" OR process == "bluetoothd" OR process == "apsd" OR subsystem CONTAINS[c] "ScreenContinuity" OR eventMessage CONTAINS[c] "MacUnlockPhone"'

start_root_capture "unified-log" \
  /usr/bin/log stream --style ndjson --level debug --predicate "${log_predicate}"

if interface_exists "awdl0"; then
  start_root_capture "network-awdl0" \
    /usr/sbin/tcpdump -n -i awdl0 -B 4096 -s 0 -U -C 64 -W 2 \
    -w "${raw_root}/network-awdl0.pcap"
else
  printf 'awdl0 was not present before launch.\n' >"${raw_root}/network-awdl0.stderr.txt"
fi

if interface_exists "llw0"; then
  start_root_capture "network-llw0" \
    /usr/sbin/tcpdump -n -i llw0 -B 4096 -s 0 -U -C 64 -W 2 \
    -w "${raw_root}/network-llw0.pcap"
else
  printf 'llw0 was not present before launch.\n' >"${raw_root}/network-llw0.stderr.txt"
fi

primary_interface="$(route -n get default 2>/dev/null |
  awk '/interface:/{print $2; exit}')"
if [[ -n "${primary_interface}" ]] && interface_exists "${primary_interface}"; then
  start_root_capture "network-discovery-${primary_interface}" \
    /usr/sbin/tcpdump -n -i "${primary_interface}" -B 4096 -s 0 -U \
    -C 32 -W 2 -w "${raw_root}/network-discovery-${primary_interface}.pcap" \
    'udp port 5353 or udp port 5354'
fi

pktap_filter='proc =sharingd || eproc =sharingd || proc =rapportd || eproc =rapportd || proc =identityservicesd || eproc =identityservicesd || proc =wifip2pd || eproc =wifip2pd'
start_root_capture "network-relevant-processes" \
  /usr/sbin/tcpdump -n -i pktap,all -Q "${pktap_filter}" -B 4096 -s 0 -U \
  -C 64 -W 2 -w "${raw_root}/network-relevant-processes.pcap"

start_user_capture "dns-sd-service-types" \
  /usr/bin/dns-sd -B _services._dns-sd._udp local.
start_user_capture "dns-sd-companion-link" \
  /usr/bin/dns-sd -B _companion-link._tcp local.

(
  while true; do
    printf '\n===== %s =====\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    for process_name in \
      sharingd rapportd identityservicesd securityd trustd wifip2pd bluetoothd apsd; do
      for process_pid in $(pgrep -x "${process_name}" 2>/dev/null || true); do
        printf '\n--- %s pid=%s ---\n' "${process_name}" "${process_pid}"
        sudo -n lsof -nP -a -p "${process_pid}" -i 2>&1 || true
      done
    done
    for process_pid in $(pgrep -f \
      '/iPhone Mirroring.app/Contents/MacOS/iPhone Mirroring' 2>/dev/null || true); do
      printf '\n--- iPhone Mirroring pid=%s ---\n' "${process_pid}"
      sudo -n lsof -nP -a -p "${process_pid}" -i 2>&1 || true
    done
    sleep 1
  done
) >"${raw_root}/process-sockets.txt" 2>&1 &
capture_pids+=("$!")

printf '\nCapture is running. Opening iPhone Mirroring now.\n'
printf 'Complete the iPhone passcode/setup prompts.\n'
printf 'As soon as the phone screen appears OR an error is shown, return here.\n\n'
open -a "iPhone Mirroring"

capture_parent_pid="$$"
(
  sleep 600
  kill -TERM "${capture_parent_pid}" >/dev/null 2>&1 || true
) &
watchdog_pid="$!"

read -r -p 'Press Enter immediately after success or failure to stop capture. ' _
stop_captures
trap - INT TERM HUP

collect_snapshot "after"

printf '\nRecord what happened; do not enter passwords, passcodes, or account names.\n'
read -r -p 'iOS version (for example, 18.6; or unknown): ' ios_version
read -r -p 'Did an iPhone passcode prompt appear? [yes/no/unknown] ' prompt_result
read -r -p 'Final result? [success/error/cancelled/unknown] ' final_result
read -r -p 'Short error text, if any (or leave blank): ' error_text

{
  printf 'session_id=%s\n' "${session_id}"
  printf 'ios_version=%s\n' "${ios_version}"
  printf 'passcode_prompt=%s\n' "${prompt_result}"
  printf 'result=%s\n' "${final_result}"
  printf 'error_text=%s\n' "${error_text}"
} >"${summary_root}/operator-observations.txt"

script_directory="$(cd "$(dirname "$0")" && pwd)"
summary_script="${script_directory}/summarize-capture.sh"
if [[ -x "${summary_script}" ]]; then
  "${summary_script}" "${capture_root}"
else
  printf 'warning: summarize-capture.sh was not executable; skipping summary.\n' >&2
fi

archive_path="${capture_root}.tar.gz"
tar -czf "${archive_path}" -C "${output_parent}" "$(basename "${capture_root}")"
shasum -a 256 "${archive_path}" >"${archive_path}.sha256"

printf '\nCapture complete.\n'
printf 'Private directory: %s\n' "${capture_root}"
printf 'Private archive:   %s\n' "${archive_path}"
printf 'Archive checksum:  %s.sha256\n' "${archive_path}"
printf '\nDo not publish the archive or private-raw directory in GitHub Actions.\n'
