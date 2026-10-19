# Dedicated Server Studio (DSS)

Dedicated Server Studio is a self-hosted control panel for installing, configuring, and operating dedicated game servers from one place. Its goal is to make dedicated servers easier to manage as your game library grows, with shared profiles, clear server controls, live status, and practical tools for mods, players, and troubleshooting. Valheim is the first supported game; the project is designed to add other dedicated-server games over time.

<p align="center"><img src="https://files.catbox.moe/aoltov.png" alt="Dedicated Server Studio profile and server management" style="width: 90%; max-width: 1200px; height: auto;"></p>

See [CHANGELOG.md](CHANGELOG.md) for additions and adjustments in each release.

## What DSS can do

- Organize servers into profiles. A profile can contain multiple Valheim servers that share one game installation and mod setup while keeping each world's public name, world, port, saves, startup options, and console separate.
- Install and update Valheim Dedicated Server through SteamCMD, including supported server branches. The panel shows installation progress and the installed build when it can be read from the game files.
- Search the Thunderstore, Hexium, Nexus Mods, and CurseForge catalogs; review dependency and version requirements; install mods; import r2modman profiles; and edit BepInEx configuration files in text or structured-fields mode.
- Start, stop, restart, and monitor servers, with resource and player status where the server exposes that information.
- View each server's recent console output, copy it, or download the complete current log. The on-screen console keeps the latest 1,000 lines to stay responsive.
- Review per-profile activity, compare configuration changes, and inspect mod versions and managed files.
- Manage recognized players, access lists, and supported live actions, including admin, permitted, banned, and kick operations.
- Keep the player roster focused on named characters or entries with recorded playtime; inactive ID-only records with no playtime are hidden.
- Check local and public addresses and test whether a running server answers a network query.
- Create DSS user accounts and grant profile-specific permissions: editors have administrator-equivalent access within assigned profiles, while operators can manage server operations and view profile information without changing profile configuration. The built-in `admin` account always remains an administrator.
- Choose an interface language for each account: English, Brazilian Portuguese, Spanish, French, German, Russian, or Simplified Chinese. English is used until an account saves a different preference under **Settings → Preferences**.
- Export and import profile settings and installed mod metadata. BepInEx files themselves are not included in that export.

## Get started

### Requirements

- Windows x64.
- **ASP.NET Core Runtime 10 for Windows x64.** DSS requires the `Microsoft.AspNetCore.App` 10.x framework. Installing only the .NET Runtime (`Microsoft.NETCore.App`) or an x86 runtime is not enough.
- A writable folder for the application and a separate location with enough disk space for SteamCMD and server files.

Download it from the [official .NET 10 downloads page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Under **Run apps**, select **ASP.NET Core Runtime 10** and the **Windows x64** installer. The .NET SDK is not required. If you host IIS on this machine, Microsoft's Hosting Bundle also includes the ASP.NET Core Runtime; for a normal DSS installation, the runtime installer is sufficient.

To confirm it is installed, run this in PowerShell:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' --list-runtimes
```

The output must include `Microsoft.AspNetCore.App 10.x` under `C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App`. Seeing only `Microsoft.NETCore.App 10.x` means the ASP.NET Core Runtime is still missing.

### First launch

1. Extract the production ZIP into a folder the Windows account can write to. Avoid protected folders such as `Program Files` unless permissions are configured.
2. Run `DSS.cmd`. Keep its terminal open while the panel is running; it owns the DSS process and shows startup errors if something goes wrong.
3. Open `http://127.0.0.1:5089` in a browser and create the administrator account. The built-in `admin` account cannot be changed to a regular user.
4. Create a profile, then use its settings to choose the server installation folder and other profile options.
5. Open the profile's server controls and install the game through SteamCMD. Create or select a server, set its public name, world, port, and save options, then start it.
6. Add mods from the profile's mod page or import a r2modman profile. Mods and BepInEx configuration are shared by the servers in that profile.

If startup fails, the launcher leaves the terminal open and prints the startup log location. Logs are also stored under the data folder described below. If the error names `Microsoft.AspNetCore.App`, check the runtime list above; a .NET Runtime 10 installation alone will not start this web application.

## Nexus Mods downloads

The Nexus catalog can be searched with a personal API key, but that key does not override the download permissions on your Nexus account. If Nexus allows your account to use a direct download, DSS uses that official API flow. Otherwise, authorize the exact file before installing it. Other connected catalogs can be used without a Nexus account:

1. In **Integrations → Nexus**, configure and validate your personal API key.
2. On the Nexus page for the required Valheim mod file, open the context menu on **Download with Manager** and choose **Copy link address**. The copied address should start with `nxm://` and contain `key` and `expires` values.
3. Paste that link into **Authorize download with NXM** in the Nexus integration card and click **Authorize file**.
4. Select the same file/version in DSS and install it before the authorization expires. The temporary authorization is held in memory and is consumed when DSS requests its download URL.

This uses the short-lived authorization generated by Nexus for that account and file. DSS does not bypass Premium, API, rate-limit, or other Nexus permissions. A fresh NXM link is needed after it expires or has been consumed.
## Profiles and multiple servers

A profile represents a shared game installation and mod environment. Add multiple servers to that profile to run separate worlds from the same downloaded game files and mods. Each server has its own public name, world name, port range, save location, process, and console. DSS suggests the next available port when a new server is created. This avoids downloading another full server installation just to host another world.

## Profile access

Administrators can assign people to profiles with `operate` or `edit` access. A `User` with `operate` access can start, stop, restart, or force-stop assigned servers; update an already installed game server through SteamCMD; view the game console; search mod catalogs and check installed-mod updates; view profile history; and copy server addresses. The user can view the profile's associated-user list, but cannot change profile, server, mod, BepInEx, or access settings, install a missing game server, or send console commands. A `User` with `edit` access has the same tools as an administrator within that profile, including managing profile access and removing the profile. Global account management remains administrator-only. The built-in `admin` account always retains the administrator role. Interface language is saved per user. Login sessions survive DSS restarts and updates until their 12-hour expiry.

When importing a r2modman profile, each supported configuration file can be up to 10 MB; the .r2z archive remains limited to 25 MB, with a 30 MB total configuration limit.

## BepInEx configuration editor

The editor supports syntax-colored text and structured fields for recognized `.cfg` settings. In fields mode, hover over a setting to read its adjacent comments as a tooltip. Text search selects and scrolls to each match, wraps to the start of the file, and keeps the match highlighted until you edit the file. Configurations over 512 KB or 4,000 lines open in lightweight text mode; syntax coloring, line-by-line comparison, and structured fields are disabled for those files to keep browser memory use manageable. Saving and text search remain available.

## Administrator mode and network access

DSS starts in standard Windows mode by default and does not request administrator privileges. In standard mode, it can manage and start servers, but Windows may block incoming game connections until the firewall is configured.

The permission status in the top bar shows whether DSS is running as administrator. Click it to choose a mode. If administrator mode is selected, Windows asks for UAC approval and DSS restarts elevated. The choice is saved in the editable `dss-config.json` beside the application; set `runAsAdministrator` to `false` to use standard mode on the next launch.

Administrator mode lets DSS create or update Windows Defender Firewall inbound rules for the configured game-server executable and ports when you choose network authorization in a server profile. Rules use private networks by default; public networks are optional. This can prevent the first-launch Windows Firewall prompt for that server executable. It does not install the game, start the server, or configure port forwarding on your router. UAC and any Windows security prompts still require approval by someone with access to the desktop.

The dashboard listens on this computer only by default. An administrator can change this in **Settings → DSS settings**; the selected mode is saved in the editable `dss-config.json` beside the application and takes effect after a restart. LAN mode lets other devices on the same network connect. Public mode listens on all network interfaces, but does not create a public Firewall rule or configure router port forwarding; those steps remain manual.

Public access is risky. DSS currently uses HTTP without TLS, so credentials and session traffic are not encrypted. Anyone who can reach the dashboard can attempt to sign in, and a compromised administrator account can control profiles and servers. Public exposure also attracts automated scans, password attempts, and denial-of-service traffic. Prefer a VPN. If direct internet access is necessary, put DSS behind an HTTPS reverse proxy, use a strong unique administrator password, restrict inbound traffic, and keep Windows and DSS updated.

## Console, commands, and logs

The game and SteamCMD consoles display the latest 1,000 lines for a responsive view. Use the copy button for the visible console text or the download button to save the complete current log. The game server's standard console does not accept typed commands unless a compatible server mod is installed; for Valheim, DSS supports [DreamWraith ConsoleStdinHeeler](https://thunderstore.io/c/valheim/p/DreamWraith/ConsoleStdinHeeler/).

Profiles, accounts, saves, and mods are kept outside the application folder, in `%LOCALAPPDATA%\DedicatedServerStudio` by default. New DSS logs are stored under the application folder's `logs` directory: startup and application logs at the root, SteamCMD logs in `logs\steamcmd`, and server console logs in `logs\servers\<server-id>`. Recognized player history is kept with DSS data so replacing or moving the application folder does not erase the roster. Existing player history from older installs is migrated automatically when its previous logs folder is still available. Existing console logs are copied from the old data folder when a server is next started under this version; the old copy is kept. A server already running during an upgrade keeps using its existing console log path until it is stopped and started again. Set the `DSS_DATA_DIR` environment variable to use a different data folder for profiles and saves. SteamCMD is downloaded on first use and can be moved in Preferences.

Removing a profile keeps its game files and saves unless you explicitly select those items in the removal dialog. Back up important worlds before making destructive changes.

## Current scope

Valheim is the only game adapter currently available. Some status and player details depend on the game server's output and may not be available when a server is stopped. DSS is a local management panel; it does not provide game hosting, a public relay, or router configuration.

## DSS updates

The GitHub package checks the latest public GitHub Release for this repository when an administrator signs in. The update button beside the DSS logo opens the release details and live progress through download, extraction, validation, staging, backup, installation, restart, and startup confirmation. DSS prepares and verifies the complete new version before stopping the panel, then launches the updated version in a PowerShell window.

The Nexus package does not connect to GitHub or use its release API. Its update button checks only for official versioned ZIP files in `.dss-builds` beside `DSS.cmd`. Place `DedicatedServerStudio-<version>-production.zip` or `DedicatedServerStudio-<version>-nexus.zip` in that folder; the panel lists available versions newer than the installed build so you can choose a ZIP. The selected package uses the same validation, backup, progress, and final-step restart flow. It never downloads an update from GitHub.

Both update flows preserve `dss-config.json`, profiles, user sessions, server files, credentials, and logs. Servers with a persistent console bridge remain running while the panel restarts; DSS also attempts to reconnect to existing server processes after a panel restart or reinstallation. If an older server process has no persistent bridge, DSS asks you to restart it once before updating. During a panel restart, DSS can close the verified previous host if it fails to exit, provided no other process owns the listening port. Wait for SteamCMD operations to finish before confirming an update.

### Manual update from a local ZIP

Place one official `DedicatedServerStudio-<version>-production.zip` or `DedicatedServerStudio-<version>-nexus.zip` in the `.dss-builds` folder beside `DSS.cmd`, then run `DSS-ManualUpdate.cmd`. The command asks for confirmation, verifies and prepares the package while the dashboard is still available, and then restarts DSS to replace the application files. If DSS is running, the updater closes the verified DSS host during the final replacement. Keep the update ZIP in `.dss-builds`; the command preserves it after the update. Profile data, settings, server files, and logs are kept.

Releases use semantic-version tags and attach a matching production package named `DedicatedServerStudio-<version>-production.zip` (for example, tag `v0.4.4` pairs with `DedicatedServerStudio-0.4.4-production.zip`). The production package omits machine-specific `dss-config.json`; a fresh install creates that file with `runAsAdministrator: false` on first launch, while an existing configuration is left untouched during manual extraction. DSS checks the package SHA-256 when GitHub provides its release-asset digest, validates the runtime manifest, and keeps a temporary copy of application files so it can restore them if the new build does not start. Run `DSS-ForceStop.cmd` as Administrator to force-stop the DSS process listening on its default port (5089).

### Publishing packages

Build the GitHub package with GitHub Release checks using `.\publish.ps1 -PackageFlavor GitHub`. Build the Nexus package with local-only update checks and no GitHub release client/API using `.\publish.ps1 -PackageFlavor Nexus`; both packages include `DSS-ManualUpdate.cmd` as an offline fallback. Use `.\publish.ps1 -PackageFlavor Both` to create both ZIPs locally; the script does not publish or upload them. The GitHub ZIP is named `DedicatedServerStudio-<version>-production.zip`, and the Nexus ZIP is named `DedicatedServerStudio-<version>-nexus.zip`.

## License

DSS is proprietary software under the [DSS Proprietary Use License](LICENSE). You may install and run unmodified official releases for personal or internal business use. You may not modify DSS or distribute copies without permission. The source repository is public to support release downloads; GitHub's Terms of Service still permit viewing and forking repositories through GitHub. A public repository should not be treated as confidential.
