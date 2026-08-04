# Orchard diagnostic catalogue

Diagnostic codes are stable public interfaces. Automation may suppress or elevate a code, but must not parse English message text. Codes are never silently reassigned to a different meaning.

| Code | Severity | Meaning | Remediation |
| --- | --- | --- | --- |
| ORC0001 | Error | Project I/O, access, or manifest failure | Check the named path, manifest, permissions, and file encoding |
| ORC0002 | Error | Unexpected CLI failure | Re-run with `ORCHARD_TRACE=1`, remove secrets, and attach the trace to a defect |
| ORC0003 | Error | Missing compiled document for `inspect` | Pass an existing `.orchard.json` application IR path |
| ORC0004 | Error | Unknown CLI command | Run `orchard help` |
| ORC0005 | Error | Unknown simulator device profile | Run `orchard devices` and select an exact device identifier |
| ORC1001 | Error | Unterminated Swift string literal | Add the closing quote |
| ORC1002 | Error | Unterminated block comment | Add `*/` and rebuild |
| ORC1003 | Error | Source exceeds the compiler byte budget | Reduce or split the source file; the bootstrap limit is 4 MiB |
| ORC1004 | Error | Source exceeds the lexer token budget | Reduce generated or repeated syntax; the bootstrap limit is 250,000 tokens |
| ORC1100 | Error | No `var body: some View` declaration | Add a SwiftUI-style root body |
| ORC1101 | Warning | `@State` declaration could not be parsed | Use a simple stored state declaration in the bootstrap profile |
| ORC1102 | Warning | State has no local initial value | Provide a deterministic preview initial value |
| ORC1103 | Error | Body has no opening brace | Correct the declaration syntax |
| ORC1104 | Error | Body has no view expression | Return a view from the body |
| ORC1105 | Warning | Control flow is outside the bootstrap parser profile | Extract a dedicated view or wait for the native Swift result-builder lane |
| ORC1106 | Error | Modifier name missing after `.` | Correct the modifier chain |
| ORC1107 | Error | Unterminated view closure | Add the closing brace |
| ORC1108 | Error | Unterminated argument list | Add the closing parenthesis |
| ORC1109 | Error | Unterminated `body` closure | Add the closing brace for `var body` |
| ORC1110 | Error | Multiple root expressions or unsupported trailing body syntax | Wrap siblings in a supported container or remove the trailing expression |
| ORC1111 | Error | More than one candidate root `body` declaration | Compile one bootstrap root view per source file |
| ORC1112 | Error | View tree exceeds the node budget | Split the view; the bootstrap limit is 5,000 nodes |
| ORC1113 | Error | View tree exceeds the nesting budget | Flatten the view; the bootstrap limit is 128 levels |
| ORC1114 | Error | Unterminated action or modifier closure | Add the matching closing brace |
| ORC2001 | Warning | Symbol is unavailable locally | Review `orchard compatibility`; use a supported alternative or official validation |
| ORC2002 | Warning | Capability requires Apple validation | Add the official validation workflow for the affected journey |
| ORC2003 | Error | Run blocked by unsupported local symbols | Remove them or explicitly use `--allow-unsupported` for diagnostic placeholders |

Future diagnostics must document severity, category, remediation, profile applicability, first release, and deprecation/replacement code. Security-sensitive failures must never be downgraded to a warning solely to increase a compatibility score.
