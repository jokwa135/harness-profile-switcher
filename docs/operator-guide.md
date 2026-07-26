# Harness Profile Switcher operator guide

## Before first use

This is a private, unsigned local build for the current Windows PC. Version `1.0.0-local` has passed the repository's automated validation, and live first-account registration and profile switching have been confirmed on this PC.

The supported environment is:

- Windows x64 and the current Windows user.
- ChatGPT Desktop installed from the Microsoft Store as package family `OpenAI.Codex_2p2nqsd0c76g0`.
- The default `%USERPROFILE%\.codex` directory with no custom `CODEX_HOME`.
- A single main ChatGPT Desktop process.
- Durable storage available on `D:`.

Do not manually move, copy, edit, or delete `auth.json`, profile folders, junctions, transaction journals, or quarantine files while using this utility.

## What is shared and what is switched

Shared across every account:

- Codex projects and local conversation history.
- Configuration and preferences stored in the default `.codex` home.
- Skills and plugins installed on this PC.
- The Harness Profile Switcher profile list and transaction history.

Switched per account:

- `.codex\auth.json`.
- The complete ChatGPT Chromium browser-state directory.

The application never parses credentials to obtain an email address. Profile names are local labels chosen by the operator.

## First account registration

1. Leave ChatGPT signed in to the account that should become the first profile.
2. Open `HarnessProfileSwitcher.exe`.
3. Confirm that the screen offers **Register current account**.
4. Select **Register current account**, enter a local display name, and confirm.
5. The utility closes ChatGPT, moves its browser state into the protected D: profile slot, creates the live browser junction, and reopens ChatGPT.
6. Confirm that the profile appears as **CURRENT** and ChatGPT still shows the same signed-in account.

The registration changes only the browser-state location. The shared `.codex` projects, history, configuration, skills, and plugins remain in place.

## Add another account

1. Select **Add another account** and enter a local display name.
2. Harness Profile Switcher closes ChatGPT, parks the current account credentials and browser junction, creates a clean profile slot, and reopens ChatGPT at sign-in.
3. Complete OAuth once in ChatGPT for the new account.
4. Return to Harness Profile Switcher and select **Check again** if **Finish setup** is still disabled.
5. Select **Finish setup**. ChatGPT closes and reopens with the new account registered as the active profile.

If the sign-in should not be kept, select **Cancel setup**. The prior account is restored, while any partial candidate browser or authentication state is moved to `D:\fordeletion\HarnessProfileSwitcher` rather than discarded.

## Switch accounts

1. Open Harness Profile Switcher.
2. Select **Switch** on an inactive profile.
3. Confirm the account change.
4. The utility closes ChatGPT, journals the switch, exchanges the authentication file and browser junction, commits the active profile, and reopens ChatGPT.
5. Verify the expected account in ChatGPT before beginning sensitive work.

If ChatGPT does not close gracefully, the utility asks before force-closing only processes verified inside the installed OpenAI Codex package directory. Unrelated Codex terminals are not selected.

## Interrupted operation and recovery

If a transaction was interrupted, normal profile actions are disabled and the application displays **Recover safely**.

1. Do not manually repair files or relaunch repeated profile actions.
2. Select **Recover safely** and confirm.
3. Recovery reads the transaction journal and the committed ledger, validates every stored path and fingerprint, and then deterministically rolls forward or rolls back.
4. ChatGPT reopens only after a safe arrangement has been established.
5. Confirm the displayed current profile and the visible ChatGPT account.

If the application displays **Check again** without **Recover safely**, automatic recovery has been disabled because the state is structurally ambiguous or corrupted. Preserve all files and stop. Do not delete journals, vault credentials, or quarantine data.

## Storage locations

| Purpose | Location |
| --- | --- |
| Portable executable | Wherever the local package is kept on `D:` |
| Runtime ledger and profiles | `D:\AI\Data\HarnessProfileSwitcher` |
| Protected diagnostics | `D:\AI\Data\HarnessProfileSwitcher\logs` |
| Preserved cancellation/recovery artifacts | `D:\fordeletion\HarnessProfileSwitcher` |
| Shared Codex home | `%USERPROFILE%\.codex` |
| Live authentication slot | `%USERPROFILE%\.codex\auth.json` |
| Live ChatGPT browser junction | `%APPDATA%\Codex\web\Codex` |

Diagnostics use a closed JSON schema containing event codes, operation names, outcomes, transaction IDs, package versions, and controlled error categories. They do not contain credential contents, hashes, profile display names, arbitrary exception messages, or filesystem paths.

## Troubleshooting

### Compatibility validation failed

Close extra ChatGPT windows, reopen Harness Profile Switcher, and select **Refresh**. The utility supports no more than one main ChatGPT Desktop process and blocks unexpected `--user-data-dir` layouts.

For a read-only technical check, run the packaged inspection script from PowerShell:

```powershell
powershell.exe -NoProfile -File .\tools\inspect-chatgpt-boundary.ps1
```

The expected result is `Compatible : True`. The script reads package, process, and path metadata but does not modify account state.

### Windows denied access

Do not change ownership or grant broad permissions manually. Preserve the runtime directories and stop. The application expects protected NTFS storage on D: for the current user, SYSTEM, and Administrators.

### ChatGPT did not reopen

Open Harness Profile Switcher again before manually launching repeated switches. If the ledger is committed but launch observation is pending, the application will offer recovery. Use **Recover safely** first when shown.

### A Microsoft Store update changes ChatGPT

The package family is checked on every operation. If compatibility validation starts failing after an update, stop account mutations until the live boundary is re-inspected and the build is retested.

## Portable removal and decommissioning

There is no installer. The portable executable and package archive can be moved to `D:\fordeletion` when no longer needed.

Do not remove `D:\AI\Data\HarnessProfileSwitcher` or the live browser junction merely to uninstall the executable. Once the first profile is registered, ChatGPT depends on the managed browser target under D:. Full decommissioning requires a deliberate restore operation and is not automated in version `1.0.0-local`.
