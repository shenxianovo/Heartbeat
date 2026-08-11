# 08 — Replace WPF with Avalonia on Windows

**What to build:** Replace the Windows-only WPF presentation with the shared Avalonia desktop experience while keeping the Windows Agent continuously active and preserving all operational controls. The Windows platform head remains one tray process hosting both Agent and UI, and WPF is removed rather than retained as a second long-lived implementation.

**Blocked by:** 01 — Extract portable Hub.Core and Desktop.Core; 05 — Cut Windows collection over to the strict AppIdentity protocol; 07 — Ship cross-platform physical-key statistics on Windows.

**Status:** ready-for-agent

- [ ] The Windows desktop experience runs through Avalonia and continues hosting the Agent and UI in one process.
- [ ] The tray lifecycle, open-settings action, close-to-hide behavior, explicit quit behavior, and continuous background collection match the existing Windows product behavior.
- [ ] Current Activity, authentication/API-key setup, Device status, Collector panel, Active/enabled states, system Collector read-only behavior, and login-start controls retain functional parity.
- [ ] Update presentation preserves the Idle → UpdateAvailable → Downloading → ReadyToApply lifecycle and distinguishes UpToDate, UpdateFound, and CheckFailed results.
- [ ] The UI presents update-required, cache-migration failure, dead-letter availability, and capability/configuration states supplied by the portable cores.
- [ ] InputEvent Recording and Interaction Signal are presented as distinct settings with distinct consequences.
- [ ] Avalonia ViewModel tests cover state presentation and commands without requiring native windows, including close-to-hide, Collector management, update application gating, and degraded/error states.
- [ ] A Windows smoke test verifies tray startup, window reopening, login start, collection while hidden, Update behavior, and clean explicit shutdown.
- [ ] WPF is no longer shipped or maintained after Avalonia reaches parity, and Windows packaging/release behavior remains functional.
