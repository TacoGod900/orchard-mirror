# Orchard IR 0.1 interoperability fixtures

These fixtures define the bootstrap contract shared by the Swift SDK and the .NET
`OrchardApplicationReader`.

Interoperability is **semantic**. A conforming consumer accepts every document listed in
`contract.json` under `accepted`, regardless of property order or insignificant whitespace,
and rejects every document under `rejected`. Property names and enum values remain exact and
case-sensitive.

The file under `canonical` defines Orchard's compact transport encoding for a fixed document:
UTF-8, sorted object keys, no insignificant whitespace, and unescaped slashes. Pretty-printed
output is intentionally not a cross-language byte-identity contract.

Reproducible `compiledAtUtc` values are derived from whole seconds in `SOURCE_DATE_EPOCH`. If
the variable is absent, the deterministic fallback is `1970-01-01T00:00:00Z`. Wall-clock build
times, machine names, process IDs, and other volatile provenance remain out of the IR and are
carried in a separate attestation or diagnostic record.
