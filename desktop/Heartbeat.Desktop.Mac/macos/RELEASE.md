# macOS Release prerequisites

The `macos-release` job runs only for a version tag and is attached to the protected `release` GitHub environment. Configure that environment before the first public Release and require reviewer approval.

Environment secrets:

- `MACOS_APP_CERTIFICATE_BASE64`: base64 Developer ID Application `.p12`
- `MACOS_INSTALLER_CERTIFICATE_BASE64`: base64 Developer ID Installer `.p12`
- `MACOS_CERTIFICATE_PASSWORD`: password shared by the imported `.p12` files
- `APPLE_ID`: Apple account used by `notarytool`
- `APPLE_APP_PASSWORD`: app-specific password for that account
- `APPLE_TEAM_ID`: Apple Developer team identifier
- `MACOS_KEYCHAIN_PASSWORD`: ephemeral CI keychain password

Environment variables:

- `MACOS_APP_SIGN_IDENTITY`: `Developer ID Application: ...` certificate subject
- `MACOS_INSTALLER_SIGN_IDENTITY`: `Developer ID Installer: ...` certificate subject

The workflow materializes credentials only in the runner temporary directory, packages and verifies the Release, and deletes the temporary keychain in an `always()` cleanup step. Do not commit certificates, passwords, notary profiles, or generated unsigned Release directories.
