# Harness Profile Switcher

Harness Profile Switcher is a private Windows desktop utility for switching ChatGPT Desktop accounts without repeating OAuth for an account that has already been registered on this PC.

It keeps Codex projects, conversation history, configuration, skills, and plugins in the shared default `.codex` home. Only `auth.json` and the complete ChatGPT Chromium browser state are switched per profile.

## Validation status

Version `1.0.0-local` has passed the automated recovery, security, process-control, state-inspection, and non-live end-to-end tests included in this repository. Live first-account registration and profile switching have also been confirmed on this PC. The installed ChatGPT package boundary is checked independently because Store updates can change it.

## Current scope

- Windows x64 on this PC.
- Microsoft Store package family `OpenAI.Codex_2p2nqsd0c76g0`.
- Default `%USERPROFILE%\.codex` location; custom `CODEX_HOME` is blocked.
- Unlimited local profiles, subject to available disk space.
- One initial OAuth sign-in for each newly added account.
- No installer, updater, profile deletion, or automated decommissioning yet.

## Safety model

- Durable application state is stored under `D:\AI\Data\HarnessProfileSwitcher`.
- Cancelled or ambiguous artifacts are preserved under `D:\fordeletion\HarnessProfileSwitcher`.
- Runtime and credential storage is restricted to the current Windows user, SYSTEM, and Administrators.
- Profile changes are journaled and recoverable.
- ChatGPT is stopped before account files or browser junctions are changed.
- The live `.codex` history, projects, configuration, skills, and plugins are never profile-switched.

See the [operator guide](docs/operator-guide.md) before using the executable against the live account.

## Local development

```powershell
.\scripts\test-engine.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\package-local.ps1
```

Build, test, package, cache, and runtime outputs remain on `D:`. The portable package is created under `artifacts\Packages` and is intentionally excluded from Git.
