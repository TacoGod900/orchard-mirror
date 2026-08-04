# Orchard Mirror: macOS 15 versus macOS 26

> Update, 2026-07-30: Current-iOS control-flow recovery subsequently resolved
> the identity ordering and tied attestation to MacUnlockPhone pairing type
> `5`. See
> [`KEYBAG_IDENTITY_FINDINGS_2026-07-30.md`](KEYBAG_IDENTITY_FINDINGS_2026-07-30.md).
> This document remains the static macOS evidence comparison.

Run: [GitHub Actions 30437725600](https://github.com/TacoGod900/Orchard/actions/runs/30437725600)

The metadata-only collector completed successfully on both GitHub-hosted
Apple-silicon runners:

| Runner | OS build | iPhone Mirroring |
| --- | --- | --- |
| `macos-15` | macOS 15.7.7 (24G720) | 1.3 (50.6.2) |
| `macos-26` | macOS 26.4 (25E246) | 1.6 (98.5) |

## Result

The evidence strongly favors this model:

1. The Mac is already an Apple Account/IDS trusted device.
2. iPhone Mirroring asks `sharingd` to perform the
   `MacUnlockPhone` remote-unlock pairing operation.
3. The user's iPhone passcode approves and pins that existing Mac identity.
4. The unlock record is bound to an attested long-term key protected by the
   Mac's Apple Key Store/Secure Enclave path.

It does **not** look like the passcode screen is a generic enrollment ceremony
that will accept any newly generated public key.

This is strong static evidence, not yet a wire-level proof of the iPhone's
rejection behavior.

## Evidence shared by both releases

### The app requires Apple Account state

The app references:

- `AccountStorePrimitives.isiCloudSignedIn`;
- UI states and instructions requiring both devices to use the same iCloud
  account; and
- `RemoteAuthenticationPrimitives.isDeviceAvailableForPairing` and
  `isMacUnlockiPhonePairingSupported`.

Its entitlements include private access to all accounts, PairingManager,
CoreAuthentication, local credential extraction, and
`com.apple.private.sharing.unlock-manager`.

### The pairing path requires an IDS device

The `sharingd` binary contains operation-specific failure messages:

- `Missing IDS device ID for Mac requesting pairing`
- `Missing IDS device ID for key registration`
- `Missing IDS device ID for lock registration`
- `IDS device not found ... Is the device in the same iCloud account?`

It also references `IDSDevice`, `RPIdentity`, AppleAccount, and same-account
device identification.

The most direct interpretation is that the iPhone passcode step authorizes an
already addressable IDS peer. It does not create the peer's Apple Account
identity from scratch.

### The remote-unlock key is attested

The `MacUnlockPhone` implementation in `sharingd` contains:

- `generateLocalLTKWithAttestation`;
- `localAttestedLTK`;
- `SDAuthenticationAKSPairingSession`;
- `aksPairingSessionForDeviceID:...requiresAttestation:...`;
- `Sending localAttestedLTK`;
- `sendSetupSessionCreatedWithLocalAttestedKey`; and
- `Remote LTK for phone ... was not signed by localAttestedLTK ... Unpairing`.

The daemon has private entitlements for the device keystore, remote keystore
sessions, IDS continuity and unlock messaging, Octagon, Rapport identity
regeneration, the system keychain, and Continuity unlock keychain groups.

This makes a freely generated software key an unlikely substitute. What is not
yet established is whether iOS validates this attestation against an
Apple-manufacturing/server trust root or only against material already
provisioned through the user's account.

## What changed in macOS 26

The authentication architecture stayed materially consistent. Of 149 critical
identity/authentication lines normalized from macOS 15 `sharingd`, almost all
remain in macOS 26; the small differences are additions and signature changes,
not removal of IDS or attested-key requirements.

macOS 26 added a stronger visible iCloud trust check:

- the iPhone Mirroring executable gained `com.apple.private.octagon`;
- it gained a mach lookup for `com.apple.security.octagon`;
- it instantiates `CDPStateControllerBackediCloudHealthPrimitives`; and
- the UI gained `Checking if iCloud is in a healthy state`,
  `iCloud Isn't Syncing`, and a remediation path through System Settings.

This is evidence against Apple relaxing the requirement to permit arbitrary
local identities. The newer release checks not merely whether the user is
signed in, but whether end-to-end iCloud trust is healthy.

## Updated feasibility assessment

| Hypothesis | Static result | Current estimate |
| --- | --- | --- |
| Passcode locally enrolls an otherwise unknown Orchard key | Strongly disfavored | 2–5% |
| A valid Apple Account/IDS identity is required | Strongly supported | 90–98% |
| The pairing LTK uses an attested Apple keystore path | Strongly supported | 90–98% |
| That attestation is necessarily verified against genuine Apple manufacturing records | Unresolved | 55–80% |
| Pure Windows remains possible by reproducing account provisioning and cryptography, without Apple hardware | Not disproved, but now less likely | 3–8% |

These are engineering estimates, not mathematical probabilities.

## What proves or disproves the remaining question

The next probe should stop at the first pairing response:

1. Generate a fresh Orchard identity outside Apple's account/keychain stack.
2. Reach the iPhone's `MacUnlockPhone` pairing endpoint.
3. Supply the correct message framing but omit an IDS device identifier and
   attested LTK.
4. Record whether iOS offers the passcode UI, returns a missing-identity error,
   or requests attestation.
5. Repeat with a syntactically valid fake IDS identifier.
6. If possible, repeat with a real IDS identifier but a software-generated LTK.

The outcomes isolate the gates:

| Result | Meaning |
| --- | --- |
| Passcode UI appears with no IDS identity | Fresh local enrollment remains possible |
| Rejected until an IDS identity is supplied | Account provisioning is mandatory |
| IDS is accepted but software LTK is rejected | Attested key is mandatory |
| Only a genuine Mac attested LTK is accepted | Pure no-Apple-hardware path is dead |

A physical Mac is useful only to reveal the exact successful message sequence
and provide a control sample. It is not part of the intended Windows product.
GitHub-hosted macOS runners cannot run this dynamic test because the iPhone must
be physically nearby over Bluetooth/local peer-to-peer networking.
