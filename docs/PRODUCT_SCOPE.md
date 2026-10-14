# Dedicated Server Studio — Product Scope

DSS is a web dashboard hosted on the computer that runs the dedicated game servers. Administrators manage profiles in a browser; server processes, SteamCMD installations, saves, logs, and revisions remain on the host.

## Architecture

- A profile stores the game, installation path, saves, world, ports, protected password, additional launch arguments, and game-specific settings.
- `IGameAdapter` defines the executable, Steam App ID, launch arguments, and shutdown procedure for each game.
- `SteamCmdService` prepares the official client, installs or updates server files, and compares the local Build ID with the selected branch.
- Each server process has independent settings. Multiple Valheim servers can share game files while keeping worlds, ports, and saves separate.
- The browser communicates with the authenticated API hosted by DSS and receives live console and operation updates.

## Planned extensions

1. Add Steam adapters for other games, including App ID, executable, ports, and shutdown behavior.
2. Add alternative installation providers for games without a Steam dedicated-server release.
3. Add scheduled world backups and per-server health reports.
4. Add TLS through a trusted reverse proxy and support additional administrators.

Valheim-specific features such as BepInEx, world settings, and crossplay remain in the Valheim adapter until another game is integrated.
