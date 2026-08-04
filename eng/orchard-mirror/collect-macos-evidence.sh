#!/usr/bin/env bash
#
# Collect static, non-secret metadata relevant to Orchard Mirror feasibility.
# This script intentionally does not copy Apple binaries, inspect user keychains,
# enumerate environment variables, or attempt account/device authentication.

set -uo pipefail

output_root="${1:-artifacts/orchard-mirror/local}"
mkdir -p "${output_root}/targets"

keywords='iPhone.?Mirroring|ScreenContinuity|MacUnlockPhone|MacUnlockPhonePairing|RPIdentity|SameAccount(Device)?|IDS|iCloud|CloudKit|Secure.?Enclave|attest|SecKeyCreateAttestation|kSecAttrTokenIDSecureEnclave|DeviceIdentity|UIK|SIK|ucrt|scrt|Pair.Verify|pairing|passcode|remote.?unlock|rapport|oneness'

write_command() {
  local destination="$1"
  local heading="$2"
  shift 2

  {
    printf '## %s\n\n' "${heading}"
    printf '$'
    printf ' %q' "$@"
    printf '\n\n'
    "$@"
    local status=$?
    printf '\n[exit status: %s]\n' "${status}"
  } >>"${destination}" 2>&1
}

safe_label() {
  printf '%s' "$1" | tr -cs 'A-Za-z0-9._-' '_'
}

record_target() {
  local label="$1"
  local path="$2"
  local safe
  safe="$(safe_label "${label}")"
  local destination="${output_root}/targets/${safe}.txt"

  {
    printf '# %s\n\n' "${label}"
    printf 'Path: `%s`\n\n' "${path}"
  } >"${destination}"

  if [[ ! -e "${path}" ]]; then
    printf 'Status: not present as a standalone filesystem object.\n' >>"${destination}"
    return
  fi

  write_command "${destination}" "Filesystem metadata" stat -f \
    'mode=%Sp size=%z bytes inode=%i modified=%Sm path=%N' "${path}"
  write_command "${destination}" "File type" file "${path}"
  write_command "${destination}" "SHA-256" shasum -a 256 "${path}"
  write_command "${destination}" "Code signature" codesign -dvvv "${path}"
  write_command "${destination}" "Code-signing entitlements" codesign -d \
    --entitlements :- "${path}"
  write_command "${destination}" "Linked images" otool -L "${path}"

  {
    printf '\n## Selected Mach-O load commands\n\n'
    otool -l "${path}" 2>&1 |
      grep -E -A5 'LC_(BUILD_VERSION|VERSION_MIN|LOAD_DYLIB|LOAD_WEAK_DYLIB|RPATH|CODE_SIGNATURE)' ||
      true
    printf '\n## Selected symbols\n\n'
    nm -m "${path}" 2>&1 | grep -Ei "${keywords}" | sort -u | head -n 2500 || true
    printf '\n## Selected strings\n\n'
    strings -a "${path}" 2>&1 | grep -Ei "${keywords}" | sort -u | head -n 5000 || true
  } >>"${destination}"
}

system_report="${output_root}/system.txt"
: >"${system_report}"
write_command "${system_report}" "macOS version" sw_vers
write_command "${system_report}" "Kernel and architecture" uname -a
write_command "${system_report}" "Machine architecture" uname -m
write_command "${system_report}" "Hardware overview" system_profiler SPHardwareDataType
write_command "${system_report}" "Hardware model" sysctl -n hw.model
write_command "${system_report}" "Optional features" system_profiler SPSoftwareDataType

tools_report="${output_root}/tools.txt"
: >"${tools_report}"
for tool in codesign file nm otool plutil shasum strings xcrun; do
  write_command "${tools_report}" "Tool: ${tool}" command -v "${tool}"
done
write_command "${tools_report}" "Xcode path" xcode-select -p
write_command "${tools_report}" "Xcode version" xcodebuild -version
for tool in dyld_info dyld_shared_cache_util dwarfdump swift-demangle; do
  write_command "${tools_report}" "Xcode tool lookup: ${tool}" xcrun --find "${tool}"
done

declare -a target_specs=(
  "iPhone Mirroring executable|/System/Applications/iPhone Mirroring.app/Contents/MacOS/iPhone Mirroring"
  "sharingd|/usr/libexec/sharingd"
  "Sharing framework|/System/Library/PrivateFrameworks/Sharing.framework/Versions/A/Sharing"
  "Sharing framework (unversioned)|/System/Library/PrivateFrameworks/Sharing.framework/Sharing"
  "Rapport framework|/System/Library/PrivateFrameworks/Rapport.framework/Versions/A/Rapport"
  "Rapport framework (unversioned)|/System/Library/PrivateFrameworks/Rapport.framework/Rapport"
  "ScreenContinuityServices framework|/System/Library/PrivateFrameworks/ScreenContinuityServices.framework/Versions/A/ScreenContinuityServices"
  "ScreenContinuityServices framework (unversioned)|/System/Library/PrivateFrameworks/ScreenContinuityServices.framework/ScreenContinuityServices"
  "ScreenSharingKit framework|/System/Library/PrivateFrameworks/ScreenSharingKit.framework/Versions/A/ScreenSharingKit"
  "ScreenSharingKit framework (unversioned)|/System/Library/PrivateFrameworks/ScreenSharingKit.framework/ScreenSharingKit"
  "ScreenContinuityUI embedded framework|/System/Applications/iPhone Mirroring.app/Contents/Frameworks/ScreenContinuityUI.framework/Versions/A/ScreenContinuityUI"
)

for spec in "${target_specs[@]}"; do
  IFS='|' read -r label path <<<"${spec}"
  record_target "${label}" "${path}"
done

metadata_report="${output_root}/service-metadata.txt"
: >"${metadata_report}"
for plist in \
  "/System/Library/LaunchAgents/com.apple.sharingd.plist" \
  "/System/Library/LaunchDaemons/com.apple.rapportd.plist" \
  "/System/Applications/iPhone Mirroring.app/Contents/Info.plist"; do
  if [[ -e "${plist}" ]]; then
    write_command "${metadata_report}" "Property list: ${plist}" plutil -p "${plist}"
  else
    printf 'Not present: %s\n' "${plist}" >>"${metadata_report}"
  fi
done

discovery_report="${output_root}/discovery.txt"
: >"${discovery_report}"
{
  printf '# Relevant filesystem entries\n\n'
  find \
    "/System/Applications" \
    "/System/Library/Frameworks" \
    "/System/Library/PrivateFrameworks" \
    "/System/Library/LaunchAgents" \
    "/System/Library/LaunchDaemons" \
    \( -iname '*iPhone*Mirror*' -o -iname '*ScreenContinuity*' \
       -o -iname '*ScreenSharing*' -o -iname '*Rapport*' \
       -o -iname '*Sharing*' \) \
    -print 2>&1 | sort -u | head -n 5000
} >>"${discovery_report}"

dyld_report="${output_root}/dyld-cache.txt"
: >"${dyld_report}"
{
  printf '# dyld shared-cache inventory\n\n'
  for root in \
    "/System/Library/dyld" \
    "/System/Volumes/Preboot/Cryptexes/OS/System/Library/dyld" \
    "/System/Volumes/Preboot/Cryptexes/OS/System/Library/Caches/com.apple.dyld"; do
    printf '\n## %s\n\n' "${root}"
    if [[ -d "${root}" ]]; then
      find "${root}" -type f -name 'dyld_shared_cache*' \
        -exec stat -f 'mode=%Sp size=%z bytes modified=%Sm path=%N' {} \; 2>&1 |
        sort -u | head -n 5000
    else
      printf 'Directory is not present.\n'
    fi
  done
} >>"${dyld_report}"

cat >"${output_root}/SUMMARY.md" <<EOF
# Orchard Mirror static macOS evidence

- Runner label requested: \`${RUNNER_OS:-unknown} ${RUNNER_ARCH:-unknown}\`
- Collection time (UTC): \`$(date -u '+%Y-%m-%dT%H:%M:%SZ')\`
- Host macOS: \`$(sw_vers -productVersion 2>/dev/null || printf unknown)\`
- Machine architecture: \`$(uname -m 2>/dev/null || printf unknown)\`

This artifact contains metadata and filtered text only. It contains no copied
Apple executable/framework/cache, user keychain data, Apple Account data,
authentication tokens, private keys, packet captures, or environment dump.

Interpretation:

1. Entitlements and linked frameworks identify the privileged boundaries.
2. RPIdentity/SameAccount/IDS references support or weaken the hypothesis that
   an already provisioned Apple Account identity is mandatory.
3. Explicit attestation APIs or UIK/SIK/ucrt/scrt references support the
   hardware-attestation hypothesis; their absence does not prove it is absent.
4. A hosted runner cannot perform the decisive nearby-iPhone enrollment test.
EOF

printf 'Generated files:\n' >>"${output_root}/SUMMARY.md"
find "${output_root}" -type f -print |
  sed "s#^${output_root}/#- \`#" |
  sed 's#$#`#' |
  sort >>"${output_root}/SUMMARY.md"
