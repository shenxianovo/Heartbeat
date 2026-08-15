# macOS Release notes

The `macos-release` job runs for version tags and publishes an unsigned Apple Silicon Release. It does not require Apple Developer certificates, notarization credentials, a protected GitHub environment, or macOS-specific secrets.

Velopack still produces the per-user Setup package, portable archive, full and (when a previous Release is available) delta packages, and stable-channel feed. The shared client update lifecycle continues to discover, download, apply, and relaunch from GitHub Releases.

Because the application has no Developer ID identity and is not notarized, first installation on a downloaded build requires the user to allow Heartbeat in macOS Privacy & Security. Gatekeeper acceptance and Accessibility/Input Monitoring continuity are operating-system behavior, not guarantees provided by Velopack. Before promoting a Release, rehearse a real `vA -> vB` update on Apple Silicon and record whether either permission needs to be granted again.

Do not commit generated Release directories. See [ADR-039](../../../../docs/adr/039-unsigned-macos-velopack-release.md) for the accepted trust and update tradeoff.
