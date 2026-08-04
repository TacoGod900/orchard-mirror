# Orchard Mirror keybag and identity findings

Date: 2026-07-30

> **Superseded — closed 2026-08-01.** This is the document that closed the keybag track: its
> conclusion stands, and it is the reason the track stopped. See [README.md](README.md).

## Outcome

The current official iPhone Mirroring enrollment path does **not** turn a
self-generated local key into a Mac identity when the user enters their iPhone
passcode.

On current iOS 26 sharingd, the enablement entry point takes an IDS device
identifier and attempts to resolve it to an existing IDS device. If that lookup
fails, the operation exits with the diagnostic:

> IDS device not found for SFAutoUnlock device. Is the device in the same
> iCloud account?

Only after that lookup succeeds does the implementation convert and consume the
passcode and create the pairing-lock session.

For the official path, the answer to the spike's central question is therefore:

> The Mac identity must already exist as a same-account IDS peer. The iPhone
> passcode authorizes pairing with that peer; it is not a generic fresh-device
> enrollment mechanism.

There is a second independent gate. The MacUnlockPhone pairing operation maps
to authentication type `5`, and type `5` is explicitly included in sharingd's
`requiresAttestation` set when it creates its Apple KeyStore pairing session.
The supported implementation generates and stores `localAttestedLTK`, and
rejects a phone LTK that is not signed by that attested local key.

This makes a software-only, freshly generated Orchard identity incompatible
with the supported iPhone Mirroring path. It does not prove that no undisclosed
Apple path or future vulnerability can ever exist, but neither is a defensible
product dependency.

## Control-flow result

The relevant current iOS enablement order is:

```text
enable(authenticationType, IDS device ID, passcode, session ID)
                         |
                         v
            validate supported feature type
                         |
                         v
           idsDeviceForUniqueID(device ID)
                  /              \
               missing           found
                 |                 |
                 v                 v
       return same-account     decode passcode
       IDS-device error            |
                                   v
                         create pairing-lock session
                                   |
                                   v
                       AKS pairing with attestation
```

The ordering matters. A passcode cannot promote a random identity past the IDS
gate because the implementation returns before it uses the passcode.

## How authentication type 5 was identified

The current Sharing framework groups types `5`, `6`, and `7` as the
MacUnlockPhone family. The current sharingd enablement entry point accepts
types `3`, `5`, `9`, and `17`. The only overlap with the MacUnlockPhone family
is type `5`, identifying it as the MacUnlockPhone pairing/enrollment type.

The AKS pairing-session constructor computes its attestation requirement from a
bit set containing types `3`, `4`, `5`, `6`, `7`, `19`, and `20`. Type `5` is
therefore an attested operation, rather than merely being adjacent to unrelated
attestation code.

## Recovered pre-pairing wire facts

The clean-room probe models the following current wire structure:

| Layer | MacUnlockPhone pre-pairing value |
|---|---|
| Authentication feature type | `5` |
| SDAuthentication protocol version | `1` |
| Transport message type | `9` |
| IDSProtobuf type | `2015` (`2006 + 9`) |
| Rapport event ID | `authentication pre-pairing request` |
| Rapport event dictionary key | `data` |
| Protobuf fields | `1: version`, `2: authentication type`, `3: session ID` |

The six recovered protobuf messages are:

| Message | Fields |
|---|---|
| `SDAuthenticationPrePairingRequest` | `1 version`, `2 type`, `3 sessionID` |
| `SDAuthenticationPairingRequest` | `1 version`, `2 type`, `3 sessionID`, `4 longTermKey` |
| `SDAuthenticationPairingResponse` | `1 version`, `2 type`, `3 sessionID`, `4 longTermKey`, `5 token`, `6 errorCode`, `7 requestArmingUI` |
| `SDAuthenticationPairingCreateSecret` | `1 version`, `2 sessionID`, `3 token` |
| `SDAuthenticationPairingCreateRecord` | `1 version`, `2 sessionID`, `3 errorCode`, `4 token`, `5 requestArmingUI` |
| `SDAuthenticationPairingDisable` | `1 version`, `2 pairingID`, `3 type`, `4 sessionID` |

The probe can build and decode these messages offline. It deliberately does
not accept a passcode, activate IDS/Rapport, request an Apple KeyStore session,
or attempt to unlock a keybag.

## Evidence correlation

### Apple security design

Apple documents that:

- iPhone Mirroring requires both devices to be signed in to the same Apple
  Account with two-factor authentication;
- setup records the Mac's cryptographic identity after iPhone-passcode
  authorization;
- the private key is protected by the Mac Secure Enclave;
- the remote-unlock protocol negotiates a mutually authenticated
  Station-to-Station tunnel; and
- the Mac remotely unlocks the iPhone keybag while the iPhone Lock Screen
  remains locked.

Sources:

- [Apple Platform Security: Automatically unlock Apple devices](https://support.apple.com/en-ph/guide/security/sec6ab47ebfc/web)
- [Apple Platform Security: iPhone Mirroring security](https://support.apple.com/pa-in/guide/security/sec6ab47ebfc/1/web/1)
- [Apple: iPhone Mirroring requirements](https://support.apple.com/en-asia/120421)
- [Apple Developer: Protecting keys with the Secure Enclave](https://developer.apple.com/documentation/Security/protecting-keys-with-the-secure-enclave)
- [Apple Developer TN3210: iPhone Mirroring](https://developer.apple.com/documentation/technotes/tn3210-optimizing-your-app-for-iphone-mirroring)

Apple's general hardware-attestation documentation also explains how an
attested Secure Enclave key can be tied to genuine device hardware and Apple
records. That document establishes the meaning of Apple hardware attestation,
but it is not treated here as proof that the private `localAttestedLTK` path
uses every detail of the managed-device attestation flow:

- [Apple Platform Security: Managed Device Attestation](https://support.apple.com/en-gb/guide/security/sec97eb9e2f2/web)

### Current implementation control flow

Independent public firmware extracts expose the current iOS sharingd and
Sharing-framework control flow used for the analysis:

- [iOS 26.1 sharingd extract](https://github.com/EthanArbuckle/iPhone18-3_26.1_23B85_Restore/tree/90aa0cfe59d9682b4265e1354c8b19ec3c7823ab/usr/libexec/sharingd)
- [iOS 26.1 Sharing framework extract](https://github.com/EthanArbuckle/iPhone18-3_26.1_23B85_Restore/tree/90aa0cfe59d9682b4265e1354c8b19ec3c7823ab/System/Library/PrivateFrameworks/Sharing.framework/Sharing)
- [iOS 26.3.1 sharingd extract](https://github.com/lechium/iPhone_OS_26.3.1_23D8133/tree/main/usr/libexec/sharingd)

These are independent decompilations, not Apple source code. Orchard uses them
only to recover protocol facts and independently reimplements the message
schema.

### macOS static evidence

The macOS 15 and macOS 26 evidence packs independently contain:

- explicit missing-IDS-device failures for pairing, key registration, and lock
  registration;
- `generateLocalLTKWithAttestation` and `localAttestedLTK`;
- a rejection when the phone's remote LTK is not signed by
  `localAttestedLTK`;
- `forceDCRTRetrievalWithCompletion`;
- the private `com.apple.keystore.device.remote-session`,
  `com.apple.private.ids.continuity`, and Octagon entitlements; and
- keychain access groups for `com.apple.continuity.unlock`.

### iPhone runtime evidence

The redacted sysdiagnose baseline contains seven synchronized
`RPIdentity-SameAccountDevice` records, including a Mac-class identity. Those
records exist separately from the Continuity Unlock records and from any
iPhone Mirroring enrollment.

At capture time, sharingd reported no valid cloud-paired peer. It did not start
AWDL or enter the mirroring enrollment flow. This runtime result matches the
control-flow requirement: account identity exists before feature-specific
pairing, and a currently eligible/cloud-paired peer is needed to proceed.

The local scanner processed 40 text files and 1,745,537 bytes, recording 128
hash-only findings. It rated the pre-existing-account identity,
attested-long-term-key, and private-Apple-service gates as strongly supported.
The raw sysdiagnose remains local and is excluded from Git.

## What is proved

1. Passcode-only enrollment of an arbitrary fresh key is not supported by the
   current official iOS enablement path.
2. The peer must first resolve as an existing same-account IDS device.
3. MacUnlockPhone pairing is authentication type `5`.
4. Type `5` requests attestation from the Apple KeyStore pairing session.
5. The supported implementation relies on a stored attested LTK and verifies
   the remote phone LTK against it.
6. A Windows peer with only freshly generated software keys cannot satisfy
   these supported-path prerequisites.

## What remains unproved

- The precise certificate-chain policy used internally for
  `localAttestedLTK`/DCRT validation has not been captured end to end.
- A live first-pair packet capture would still be valuable for regression
  tests and for mapping the subsequent encrypted messages.
- There could be an undisclosed alternative enrollment path or exploitable bug.
  There is currently no evidence of one, and relying on one would be unsuitable
  for a legal commercial product.
- A genuine Apple device acting as a credential/relay endpoint may be
  technically viable, but it violates the pure no-Mac/no-Apple-hardware product
  requirement.

## Product decision

Treat locked-screen, no-phone-app iPhone Mirroring from a pure Windows identity
as **no-go on the official protocol**.

Continue Orchard Mirror only under one of these scopes:

1. unlocked-phone AirPlay plus accessibility input, accepting its platform
   limits;
2. a phone-side Orchard app using public APIs;
3. an Apple-authorized integration;
4. genuine Apple hardware as an explicit relay/credential device; or
5. research-only monitoring for a newly discovered enrollment path, without
   representing it as the product plan.

The useful outcome of the spike is not that the protobuf was impossible to
recover—it was recovered. The blocker is the identity and attestation trust
root surrounding that protobuf.
