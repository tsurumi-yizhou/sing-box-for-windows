# Upstream checkouts

This directory is intentionally ignored except for this README and
`sources.lock.json`. Populate it with `eng/Sync-Upstream.ps1`, which
clones the pinned revisions recorded in the lock file. Do not develop
parity features from an unpinned branch.

## Layout

| Checkout              | Purpose                                             |
| --------------------- | --------------------------------------------------- |
| `sing-box`            | Builds the boxdd daemon (`eng/build-daemon.ps1`) and is the source of the mirrored gRPC protos in `Protos/`. |
| `sing-box-for-desktop`| The official desktop client; the primary parity reference (boxdd service architecture, Electron shell). |
| `sing-box-for-apple`  | Parity reference for UI behavior and features.      |
| `sing-box-for-android`| Parity reference for UI behavior and features.      |

## Common tasks

```powershell
# Verify all checkouts match the lock file (offline, read-only)
eng\Sync-Upstream.ps1 -Check

# Clone/repair checkouts to the pinned revisions
eng\Sync-Upstream.ps1

# Advance a pin, then rebuild the daemon
eng\Sync-Upstream.ps1 -Pin singbox=<full-commit-sha>
eng\build-daemon.ps1
```

The lock file also records the pinned Go toolchain under `toolchain.go`;
`build-daemon.ps1` warns when the local `go` version differs.
