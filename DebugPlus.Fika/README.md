# DebugPlus Fika companion

Install matching DebugPlus, DebugPlus.Common, and DebugPlus.Fika assemblies on every participant and the host/headless host. The server's administrator profile allowlist grants access; the host need not be an administrator to execute a remote administrator's command. No local permission setting overrides that policy.

The host identifies a requester through Fika 2.4.3's connection-bound `NetPeer.Player`. A GUID session, version handshake, current live player roster, and periodic companion heartbeat gate commands. Player commands execute on the owning player's controller. World commands execute on the host, with acknowledged client updates for doors, time, and weather. Equipment repair and malfunction component updates are also acknowledged on the host and observers. Scene teardown, transit, host loss, protocol mismatch, and companion loss suspend commands and gameplay overrides. Packet and lifecycle events enter a queue and execute on Unity's main thread.

Joining and reconnecting peers receive the host's current time/weather overrides before debugging becomes available. Running daylight uses the current host hour. A peer that cannot acknowledge this environment snapshot remains unavailable until it reconnects.

Inventory creation uses a native EFT descriptor delta, preserving nested item state and generated IDs. The owner sends that delta for an in-flight host-authorized command. The host validates its request/session/target binding, approved item structure, and allowed equipment removals before applying it to the observed inventory. It relays the update to the other peers and waits for acknowledgements. Observers accept replicas only from the host connection. Duplicate command and replica IDs are retained for the raid. A timeout or disconnect reports an uncertain outcome and never automatically retries a mutation.

Build without deployment:

```powershell
dotnet build DebugPlus.Fika/DebugPlus.Fika.csproj -c Release -p:DeployDebugPlus=false
dotnet run --project DebugPlus.Fika/Tests/WireChecks.csproj -c Release
```

The wire checks cover envelope round-trip, protocol/version mismatch, bounded incoming and outgoing payloads, truncation, and inventory request/session/target binding. Live solo/listen-host/headless verification is separate: test an administrator and ordinary peer, remote targeting, unauthorized requests, disconnects, reconnects, transit, nested inventory spawning, replacement, and visible world updates. Do not treat compilation or wire checks as gameplay acceptance.
