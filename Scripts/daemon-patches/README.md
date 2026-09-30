# SFW daemon patches

`build-daemon.ps1` applies these patches to the pinned sing-box checkout before
building. Application is idempotent and stops on conflicting upstream changes.
Keep patches in filename order and review them when updating `sources.lock.json`.

## Non-TUN Windows system proxy

For a profile without a TUN inbound, the Windows daemon automatically selects
the first eligible HTTP/mixed listener, or the single listener explicitly marked
`set_system_proxy: true`. Eligible listeners bind to loopback or a wildcard and
have no authentication, enabled TLS, detour, or network namespace. SOCKS-only
profiles do not expose a system HTTP proxy. Multiple explicit choices or an
incompatible explicit choice fail with a configuration error.

Selection modifies only the parsed runtime configuration. The listener registers
its actual bound port with the daemon after TCP bind succeeds. The daemon uses
its existing authenticated owner impersonation and WinINet implementation, so
the proxy applies to the desktop user rather than LocalSystem. Existing RPCs,
persisted enable/disable preference (enabled by default), stop/reload/failure
cleanup, and session handling own this same proxy. TUN profiles do not receive
automatic listener selection.

The change reuses upstream cleanup semantics: disabling clears the proxy; it
does not snapshot and restore a pre-existing third-party proxy configuration.

Validation (run in the patched sing-box checkout with the build script's Go
cache environment and Windows release tags minus `tfogo_checklinkname0`):

```powershell
go test ./experimental/boxdd ./common/listener -run TestSFW -count=1
```

These tests never enable the host's system proxy. A live acceptance check should
start a non-TUN loopback mixed profile, confirm Windows proxy points to its port,
toggle it off/on in SFW, and confirm stopping clears it. Repeat with TUN and a
SOCKS-only profile to check that neither gets automatic HTTP proxy selection.
