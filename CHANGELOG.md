# Changelog

## 0.4.6 — 2026-10-17

### Added

- Save recognized player history in persistent profile data, with playtime tracked separately for each character.
- Group bulk mod activation and deactivation into one profile-history entry with a detail view listing the affected mods.

### Fixed

- Improve the Valheim connectivity check by retrying Steam UDP queries on the game and query ports, and show recent player connections as evidence when a direct query cannot confirm access.
- Match live character sessions more accurately when one platform ID is associated with multiple characters; clarify that Valheim's persistent ban list applies to the platform ID and all associated characters.

## 0.4.5 — 2026-10-16

### Added

- Add a Minecraft card to the home screen with a clear coming-soon status.

### Fixed

- Show progress while the updater releases the previous PowerShell launcher, and load required Windows PowerShell modules explicitly.

## 0.4.4 — 2026-10-16

### Fixed

- Report the version from the running assembly in `/api/health`, allowing staged update checks to confirm the downloaded version before replacing the active installation.

## 0.4.3 — 2026-10-16

### Added

- Add `DSS-ForceStop.cmd` to identify and force-stop a verified DSS process listening on port 5089.

### Fixed

- Verify the staged DSS build on an isolated loopback port before shutting down the active dashboard.
- Wait for the previous PowerShell launcher to release its startup lock before starting the replacement, and keep the update dialog on a restart status while DSS is unavailable.

## 0.4.2 — 2026-10-16

### Adjusted

- Prepare, validate, and back up every new application file before shutting down DSS; show live progress and an explicit restart status throughout the update.
- Restart the updated application through a visible PowerShell window and recover safely when an older launcher leaves the DSS startup lock behind.

## 0.4.1 — 2026-10-16

### Added

- Profile-specific `Editor` and `Operator` permissions, with an administrator-equivalent editor and read-only profile access for operators.
- Progress tracking for DSS updates, including package download, validation, backup, installation, restart, and startup confirmation; release notes render safely in the update dialog.
- A colored BepInEx text editor and a switch to the structured fields view for read-only access.

### Adjusted

- Allowed operators to start, stop, restart, and force-stop assigned servers; update an installed game through SteamCMD; view the game console; check mod updates; search catalogs; view history; copy server addresses; and use player management. Profile settings, mod changes, and access assignments remain read-only for operators.
- Kept the operator's server controls and console actions consistent with the server's running state, while leaving available checks and SteamCMD actions accessible.
- Kept profile editors' settings, mod management, user access, and removal controls equivalent to the administrator within the assigned profile.
- Preserved login sessions and recovered existing server processes across DSS restarts and updates; made permission-mode restarts wait for the correct DSS process and handle shutdown races safely.
- Raised the per-file r2modman configuration import limit to 10 MB, while retaining archive and total configuration limits.
- Corrected profile access, BepInEx, history, notifications, Nexus, and CurseForge translations and fixed profile and modal control layouts.
- Updated README setup and permission guidance and made its Catbox screenshot scale with the viewport.

## 0.4.0 — 2026-10-16

### Added

- Shared profiles that can host multiple servers with separate worlds, ports, saves, and consoles.
- Player management with recognized player history, access lists, and supported live actions.
- Mod catalog integrations with dependency and version selection, batch operations, and richer installation history.
- Network status checks for local and public addresses and server query reachability.
- Per-user interface language preferences, with English as the default for accounts without a saved choice.
- Release updates that validate the production package and preserve server processes when a persistent console bridge is available.

### Adjusted

- Expanded translations across dialogs, loading states, tooltips, provider labels, and profile controls.
- Kept the built-in `admin` account at the administrator role in both the interface and API.
- Refined profile settings alignment, the DSS update control, and the login logo.
- Reduced browser stalls during startup and large interface translations.
