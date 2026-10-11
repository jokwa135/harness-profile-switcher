# Harness Profile Switcher

Harness Profile Switcher is a private Windows desktop utility for switching ChatGPT Desktop accounts without repeating OAuth for an account that has already been registered on this PC.

It keeps Codex projects, conversation history, configuration, skills, and plugins in the shared default `.codex` home. Only `auth.json` and the complete ChatGPT Chromium browser state are switched per profile.

Contributor acknowledgments are recorded in [CONTRIBUTORS.md](CONTRIBUTORS.md).

## Validation status

Version metadata remains `1.1.0-local`. The source update passed 48 fixture suites (including 8 dashboard suites), the isolated non-live end-to-end journey, and synthetic native preview rendering. Native regular and bold font checks resolved to Tahoma. This validation did not inspect live account state, register or switch a live account, or refresh authentication. Prior live registration/switching validation is historical, not a claim that the redesigned UI was revalidated against live accounts. The installed ChatGPT package boundary is checked independently because Store updates can change it.

## Dashboard and profile labels

- Compact account dashboard with an account-header Add action, direct Switch actions, and a More menu for Rename and eligible inactive-profile deletion.
- Rename changes only the saved local display label. Profile identity, account folders, sign-in, shared local work, and activation timestamps remain unchanged; it does not stop, switch, or relaunch ChatGPT.
- Renaming requires healthy saved profiles and no pending operation. The dialog prefills the name, supports Enter/Escape, and rejects empty names and control characters.
- The functional UI font prefers native-verified Tahoma, with Microsoft Sans Serif as a fallback. Fixture checks cover keyboard contracts, long Unicode labels, and simulated 100%, 150%, and 200% layouts.
- Codex limits are explicitly **Unavailable** in production. No auth-write-free live retrieval path has been verified, and inactive accounts are never queried or activated. Cached/stale quota examples and response validation are synthetic fixtures only, not a live quota implementation.

## Current scope

- Windows x64 on this PC.
- Microsoft Store package family `OpenAI.Codex_2p2nqsd0c76g0`.
- Default `%USERPROFILE%\.codex` location; custom `CODEX_HOME` is blocked.
- Unlimited local profiles, subject to available disk space.
- One initial OAuth sign-in for each newly added account.
- No installer, updater, or automated decommissioning yet.

## Safety model

- Durable application state is stored under `D:\AI\Data\HarnessProfileSwitcher`.
- Cancelled or ambiguous artifacts are preserved under `D:\fordeletion\HarnessProfileSwitcher`.
- Runtime and credential storage is restricted to the current Windows user, SYSTEM, and Administrators.
- Profile changes are journaled and recoverable.
- Inactive profile deletion is permanent, journaled, and recoverable if interrupted.
- ChatGPT is stopped before account files or browser junctions are changed.
- The live `.codex` history, projects, configuration, skills, and plugins are never profile-switched.

See the [operator guide](docs/operator-guide.md) before using the executable against the live account.

## Local development

Run the isolated dashboard verification from `D:\AI\Tools\codex-profile-switcher`:

```powershell
.\scripts\verify-dashboard.ps1
```

This compiles into a fresh `artifacts\Development` session, uses an explicit project-local fixture root and synthetic process helpers, runs the regression/dashboard suites and non-live end-to-end journey, and renders synthetic preview captures. It preserves the existing Release executable and observes existing process identities without controlling them. State-inspection fixtures do not inspect the current user's live account. The existing `artifacts\Release\HarnessProfileSwitcher.exe` is required for the preservation hash check.

The older `test-engine.ps1` and `validate-nonlive-e2e.ps1` entrypoints are not the isolated verification path: the fixture suites now require `HPS_TEST_ROOT` and matching session-local helpers supplied by `verify-dashboard.ps1`.

Build and packaging remain available through `scripts\build.ps1 -Configuration Release` and `scripts\package-local.ps1`; they are not part of isolated verification. Build, test, package, cache, and runtime outputs remain on `D:`. The portable package is created under `artifacts\Packages` and is intentionally excluded from Git. No new release version or GitHub binary release is declared by this source update.
