#!/usr/bin/env bash
#
# Build a compact inventory and keyword index for a capture created by
# capture-first-pairing.sh. This does not delete or claim to anonymize raw data.

set -uo pipefail

capture_root="${1:-}"
if [[ -z "${capture_root}" || ! -d "${capture_root}/private-raw" ]]; then
  printf 'usage: %s /path/to/first-pairing-capture\n' "$0" >&2
  exit 2
fi

raw_root="${capture_root}/private-raw"
summary_root="${capture_root}/summary"
mkdir -p "${summary_root}"
chmod 700 "${summary_root}"

inventory="${summary_root}/inventory.txt"
: >"${inventory}"
{
  printf '# File inventory\n\n'
  find "${capture_root}" -type f -print | sort | while IFS= read -r item; do
    stat -f 'size=%z bytes modified=%Sm path=%N' "${item}"
  done
  printf '\n# SHA-256\n\n'
  find "${capture_root}" -type f ! -path "${inventory}" -print |
    sort | while IFS= read -r item; do
      shasum -a 256 "${item}"
    done
} >>"${inventory}" 2>&1

packet_summary="${summary_root}/packet-summary.txt"
: >"${packet_summary}"
{
  printf '# Packet captures\n\n'
  find "${raw_root}" -type f -name '*.pcap*' -print | sort |
    while IFS= read -r packet_file; do
      printf '\n## %s\n' "$(basename "${packet_file}")"
      file "${packet_file}"
      packet_count="$(tcpdump -n -r "${packet_file}" 2>/dev/null | wc -l |
        tr -d '[:space:]')"
      printf 'decoded_packets=%s\n' "${packet_count:-0}"
      printf '\nSelected discovery records:\n'
      tcpdump -n -vvv -r "${packet_file}" 2>/dev/null |
        grep -Ei 'PTR|SRV|TXT|_tcp|_udp|Bonjour|mDNS' |
        head -n 300 || true
    done
} >>"${packet_summary}" 2>&1

keyword_index="${summary_root}/authentication-keyword-index.txt"
: >"${keyword_index}"
keywords='MacUnlockPhone|IDS|RPIdentity|SameAccount|attest|localAttestedLTK|LTK|AKS|pairing|passcode|ScreenContinuity|remote.?unlock|Octagon|iCloud|Rapport'

{
  printf '# Authentication keyword counts\n\n'
  for log_file in \
    "${raw_root}/unified-log.stdout.txt" \
    "${raw_root}/unified-log.stderr.txt" \
    "${raw_root}/process-sockets.txt" \
    "${raw_root}/before-state.txt" \
    "${raw_root}/after-state.txt"; do
    if [[ -f "${log_file}" ]]; then
      printf '\n## %s\n' "$(basename "${log_file}")"
      grep -Eio "${keywords}" "${log_file}" 2>/dev/null |
        tr '[:upper:]' '[:lower:]' |
        sort | uniq -c | sort -nr || true
    fi
  done

  printf '\n# Selected unified-log lines\n\n'
  if [[ -f "${raw_root}/unified-log.stdout.txt" ]]; then
    grep -Ei "${keywords}" "${raw_root}/unified-log.stdout.txt" |
      head -n 4000 || true
  fi
} >>"${keyword_index}" 2>&1

cat >"${summary_root}/PRIVATE_DATA_WARNING.txt" <<'EOF'
THIS CAPTURE IS PRIVATE

The private-raw directory and its archive can contain:

- device identifiers, local addresses, and account-related opaque identifiers;
- nearby-device discovery traffic;
- encrypted session payloads and timing information;
- process names, socket endpoints, and system log messages.

The summary directory is an index, not a guaranteed anonymized export. Review
all files before sharing them. Never commit the capture to Git, attach it to a
public issue, or upload it as a public GitHub Actions artifact.

The capture script does not intentionally read keychains, passwords, passcodes,
authentication tokens, private keys, or environment variables.
EOF

printf 'Summary written to %s\n' "${summary_root}"
