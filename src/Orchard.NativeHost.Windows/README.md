# Orchard native Windows host

`Orchard.NativeHost.Windows` is the headless process adapter for native Swift Orchard applications. It launches one child without a shell, establishes a fresh current-user-only `Orchard.Transport.Windows` endpoint, and sends the initial device configuration after the authenticated protocol handshake.

Endpoint credentials are transferred through the child's redirected standard-input handle using the three-line `ORCHARD-LIVE-V1` bootstrap. The pipe name and token never enter arguments, environment variables, stdout, stderr, exceptions, or host logging. Standard output and error are continuously drained into independent bounded captures so a noisy or compromised child cannot deadlock the host or allocate unbounded memory.

The headless session enforces the application protocol state machine:

1. the first published render must be revision 1;
2. an event cannot be dispatched before that render is consumed;
3. accepted events must advance the current revision and be followed by a render at the exact result revision;
4. rejected events must preserve the current revision and cannot publish a render;
5. replayed, rolled-back, mismatched, or otherwise invalid protocol state faults the session permanently; and
6. a faulted or unresponsive child is killed and reaped instead of receiving further protocol messages.

`NativeHostLaunchOptions.RuntimeSearchPaths` accepts only existing absolute directories. Development launches use it for the pinned Swift runtime DLL directory. A packaged release should instead place signed runtime dependencies according to the distribution layout established by the packaging phase.

The dependency-free integration executable launches the real `NativeHello.exe` and covers stateful change and press events, revisioned rerendering, stale-event rejection, ping/pong, graceful shutdown, stdout separation, authentication failure, child replacement, bounded connection timeout, first-revision enforcement, replay and rollback rejection, and terminal fault containment:

```powershell
dotnet run --project tests/Orchard.NativeHost.Windows.Tests/Orchard.NativeHost.Windows.Tests.csproj --configuration Release
```
