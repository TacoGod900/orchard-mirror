# Security

Project Orchard is an early proof of concept. It has not had a security audit and should not be used as a security boundary or to process untrusted projects in a privileged environment.

If you find a vulnerability, please report it privately through **Security → Report a vulnerability** on GitHub rather than opening a public issue.

Current trust model:

- Project manifests and SwiftUI-shaped source are read as untrusted data and compiled to JSON IR; no arbitrary source is executed as Swift.
- Project and entry-point paths must stay inside the selected project directory.
- IR node names and properties map through explicit allowlists.
- The preview runs as a normal desktop process with the current user's privileges and is not sandboxed.
- Diagnostics and IR can contain local paths; review them before sharing.
