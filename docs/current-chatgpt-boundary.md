# Current ChatGPT Desktop Integration Boundary

This document defines the fail-closed integration contract for the current-PC version of Harness Profile Switcher. It records path and process identity only; credential values are never part of the contract.

## Verified environment

Verified on 2026-07-27 against:

- Store package: `OpenAI.Codex`
- Package version: `26.721.4979.0`
- Package family: `OpenAI.Codex_2p2nqsd0c76g0`
- Manifest application ID: `App`
- Stable AppUserModelID: `OpenAI.Codex_2p2nqsd0c76g0!App`
- Manifest executable: `app/ChatGPT.exe`
- Entry point: `Windows.FullTrustApplication`

The installed version directory is not a stable launch contract. Harness Profile Switcher must rediscover the package and executable after every Store update and launch through the registered AppUserModelID when possible.

## Process ownership

The visible desktop process is the single package-scoped `ChatGPT.exe` whose command line has no Chromium `--type=` argument. Its main window currently has the title `Codex`; window title is not an identity check.

The main process owns multiple package-scoped `ChatGPT.exe` Chromium children and one `app\resources\codex.exe` app-server helper. Harness Profile Switcher must scope process management to the installed package directory and process tree. It must never terminate every process named `codex.exe`, because unrelated Codex CLI sessions may exist.

## Shared state

With `CODEX_HOME` unset, the shared harness root is:

`C:\Users\Jokwa\.codex`

Everything under this root remains live and shared between accounts except `auth.json`. This includes projects, sessions, configuration, rules, skills, plugins, memories, logs, attachments, and other harness state. A switch operation must not move, replace, or clone the shared root.

## Profile-specific state

The profile-specific authentication file is:

`C:\Users\Jokwa\.codex\auth.json`

The profile-specific Chromium user-data directory is:

`C:\Users\Jokwa\AppData\Roaming\Codex\web\Codex`

Running package processes currently confirm that directory through `--user-data-dir`. The complete directory is considered account state for the initial reliable implementation. Narrow allowlists are explicitly rejected because earlier versions omitted required OAuth/session databases.

Windows package data under `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0` remains shared. The current package stores only small package settings there; it is not part of a profile slot.

## Compatibility guards

Switching must be disabled when any of these conditions is true:

1. `OpenAI.Codex` is not installed for the current user.
2. Application ID `App` or the package family changes.
3. The manifest executable is no longer `ChatGPT.exe`.
4. The entry point is no longer `Windows.FullTrustApplication`.
5. The stable AppUserModelID is not registered.
6. `CODEX_HOME` is set instead of using the verified default root.
7. The live authentication or browser-state path is missing during an operation that requires an active account.
8. Running package processes advertise a different Chromium user-data directory.
9. More than one package-scoped main desktop process is detected.

The read-only verifier is `scripts\inspect-chatgpt-boundary.ps1`. The application performs the checks through typed C# components. Store package metadata is currently discovered with one fixed, encoded, read-only Windows PowerShell command; normal switching does not invoke the diagnostic script.

## Process-control implementation

`PowerShellPackageDiscovery` rediscovers and validates the current-user Store package, manifest identity, registered AppUserModelID, install root, and resolved executable on each application run. `WmiPackageProcessInventory` accepts only executable paths strictly beneath that discovered install root.

Graceful shutdown targets the sole package-scoped main `ChatGPT.exe`. If package processes remain, force-close requires a one-use in-memory confirmation token that expires after one minute. Immediately before every forced termination, the process image is reopened and verified against both its WMI path and the package root. The controller never selects a process by name alone.

Relaunch uses `explorer.exe shell:AppsFolder\<AppUserModelID>` and is successful only after exactly one package-scoped main process is observed. A launch failure occurs after the account-state commit boundary and therefore must be surfaced for retry rather than rolling account data back.

## Desktop orchestration

The Windows UI derives its available actions from the ledger, filesystem relationships, and unfinished journals; it does not trust a stored profile-status flag. Before every mutation it rediscovers the Store package, captures a fresh package-scoped process inventory, enforces the single-main-process and browser-path guards, then attempts graceful shutdown. Force-close is shown only after graceful shutdown fails and uses the controller's expiring one-use confirmation.

The UI runs exactly one journaled engine action after shutdown, relaunches through the registered AppUserModelID, and records launch observation. When a non-terminal journal exists, switching, registration, and onboarding controls are removed and only operation-specific recovery is offered. Unknown or multiple unfinished operations remain fail-closed.

## Current limitation

These are unsupported product internals and may change after a ChatGPT Desktop update. A new version is not accepted merely because the executable still starts; the full compatibility contract must pass before account state can be changed.
