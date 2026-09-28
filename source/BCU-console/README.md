# BCU-console — Bulk Crap Uninstaller command-line interface

**`bcu.exe`** (alias **`BCU-console.exe`**) — a full-featured Windows application
manager driven entirely from the terminal, powered by BCU's battle-tested
`UninstallTools` engine. Everything the BCU GUI detects and cleans is reachable
from the command line, built for RMM and unattended use.

This project is the merged successor to the old 3-command `BCU-console` stub and
the standalone [`bcu-cli`](http://192.168.70.165:3000/luadmin/bcu-cli) tool. Its
command surface now matches the GUI's capabilities.

---

## Contents
- [What changed from the old BCU-console](#what-changed-from-the-old-bcu-console)
- [Safety model](#safety-model)
- [Commands](#commands)
- [Options](#options)
- [Sources](#sources)
- [Junk confidence levels](#junk-confidence-levels)
- [Legacy back-compatibility](#legacy-back-compatibility)
- [Exit codes](#exit-codes)
- [Examples](#examples)
- [Building from source](#building-from-source)
- [Architecture](#architecture)
- [Credits](#credits)

---

## What changed from the old BCU-console

The upstream `BCU-console` exposed only three commands (`list`, `export`,
`uninstall`), could only uninstall via a **pre-built `.bcul` list file**, and
blocked on `Console.ReadKey()` — it would *hang waiting for a keypress even to
show its help screen*, making it unusable in a non-interactive shell.

The merged CLI:

- **Full command set** — by-name uninstall, bulk, repair/modify/rename,
  delete-entry, startup management, info dump, junk scan/clean, multi-format
  export, list import, certificate verification.
- **Non-blocking** — no interactive `ReadKey`; safe in pipelines, services and
  RMM agents.
- **Dry-run by default** — every state-changing command prints what it *would*
  do and changes nothing unless given `--yes`.
- **Two executables** — outputs `bcu.exe` plus a `BCU-console.exe` alias so
  existing scripts that call `BCU-console.exe` keep working.
- **Old behaviour preserved** — `uninstall <list.bcul>` and the legacy
  `/Q /U /V /J` switches still work (see [Legacy back-compatibility](#legacy-back-compatibility)).

---

## Safety model

State-changing commands — `uninstall`, `bulk`, `repair`, `modify`, `rename`,
`delete-entry`, `startup enable/disable`, and `junk` — **only execute when given
`--yes`** (and not `--dry-run`).

Without `--yes`, every one of them prints exactly what it *would* do and
**changes nothing**. This is deliberate for non-interactive/RMM use: a missing
flag never causes an accidental change. There is no interactive prompt.

```
bcu uninstall "Git"            # prints the plan, exits — no change
bcu uninstall "Git" --yes      # actually uninstalls
bcu uninstall "Git" --dry-run  # forces dry-run even with --yes present
```

---

## Commands

| Command | Description |
|---------|-------------|
| `list` | List installed applications (default command) |
| `export [file]` | Export list to JSON, CSV, XML, BAT, or PS1 |
| `uninstall <target>` | Uninstall one app (use `--bulk` for many) |
| `uninstall <list.bcul>` | **Legacy**: uninstall everything a saved BCU list matches |
| `bulk <name...>` | Bulk-uninstall multiple apps |
| `repair <target>` | Repair via MSI maintenance or the app's modify command |
| `modify <target>` | Run the app's modify/change command |
| `rename <target> <new-name>` | Rename the registry entry |
| `delete-entry <target>` | Delete the registry entry only (keep installed files) |
| `startup list\|enable\|disable` | Manage system startup entries |
| `info <target>` | Detailed properties + certificate dump |
| `junk [<name>]` | Find and clean leftover junk |
| `import-list <file>` | Load a previously exported BCU `.xml` list |
| `serve [--pipe <name>]` | Run a JSON-RPC API daemon over a named pipe (see below) |
| `tui` (aliases `ui`, `interactive`) | Interactive full-screen terminal UI (see below) |
| `help` | Show help |

### `tui` — interactive terminal UI

`bcu tui` launches an old-school, full-screen, keyboard-driven app for hands-on
use (no RMM). Hand-rolled on `System.Console` — no extra dependencies. It refuses
to run if stdin/stdout is redirected (use `bcu list` for scripting).

| Key | Action |
|-----|--------|
| `↑` `↓` / `j` `k`, `PgUp/PgDn`, `Home/End` | Navigate |
| `Space` | Toggle-select the highlighted app |
| `a` / `A` | Select all (current filter) / clear all |
| `/` or `s` | Live search (type to filter; `Esc` clears) |
| `Enter` | Details panel for the highlighted app |
| `u` | Uninstall selected (or highlighted) — shows a confirm screen |
| `d` | Toggle **dry-run** (`u` only simulates) |
| `o` | Cycle sort (name / publisher / size / source / date) |
| `r` | Rescan · `?`/`F1` help · `q`/`Esc` quit |

`u` always confirms first; with dry-run on it changes nothing.

### `serve` — JSON-RPC API daemon (for RMM / API helpers)

`bcu serve` hosts a resident JSON-RPC endpoint over a **named pipe** (default
`\\.\pipe\bcu`, override with `--pipe <name>`) so an agent can drive the engine
without re-scanning per call. Local only — no network surface.

**Framing:** newline-delimited JSON, one object per line.
Request `{"id":1,"method":"<m>","params":{...}}` →
response `{"id":1,"result":{...}}` or `{"id":1,"error":{"code":N,"message":"..."}}`.

**Methods:** `ping` · `context` ·
`inventory.list` `{refresh?,rmmSafe?,verifyCerts?,filter?}` ·
`app.info` `{name|registryPath|ratingId,exact?}` ·
`app.uninstall` `{<target>,quiet?,confirm?}` ·
`bulk.uninstall` `{ids:[...],quiet?,confirm?}` ·
`junk.scan` / `junk.clean` `{name?,level?,confirm?}` ·
`job.status` `{jobId}` · `job.list` · `session.launch` `{args}` · `shutdown`.

**Safety:** state-changing methods (`app.uninstall`, `bulk.uninstall`,
`junk.clean`) only execute with `"confirm": true` — otherwise they return a
**dry-run plan** (mirrors `--yes`). Inventory is cached; pass `refresh:true` to
rescan. The pipe is **ACL'd to the current user + SYSTEM**. `Ctrl+C` stops the
daemon. Every response carries a `schemaVersion`.

**Async jobs:** a confirmed `app.uninstall` / `bulk.uninstall` / `junk.clean`
returns `{jobId, state:"running"}` immediately and runs on a background task; poll
`job.status {jobId}` (or `job.list`) for `done`/`total`/`failed` progress and the
final `result`. Dry-runs and reads stay synchronous.

**Context / SYSTEM:** `context` reports `sessionId`, `isSystem`, `isElevated`,
`activeConsoleSession`, and warnings (e.g. Store apps are empty under SYSTEM).
When `serve` runs as SYSTEM, ops needing an interactive session (Store/UWP,
GUI-automation) return `needsUserSession`; `session.launch {args}` brokers a `bcu`
command into the active console session (`WTSQueryUserToken` + `CreateProcessAsUser`
— only functional under SYSTEM).

**Targeting** (uninstall/repair/modify/rename/delete-entry/info/junk): by name
(partial, or `--exact`), `--registry-path <path>`, or `--rating-id <id>`. Stable
identifiers (`--registry-path` / `--rating-id`) are recommended for RMM
automation because display names change between versions.

---

## GUI-parity commands (v6.3.0-cli.2)

These cover the GUI features the earlier CLI lacked. They all follow the same
safety model: they dry-run unless you pass `--yes`, and none of them ever waits for
keyboard input, so they're safe under an RMM agent running as SYSTEM.

| Command | GUI feature | Notes |
|---------|-------------|-------|
| `bcu msi <target> [--mode configure\|uninstall\|quiet]` | *Uninstall using MsiExec* (`/I`, `/X`, `/qb /X`) | Target by name or `--msi-guid {GUID}` |
| `bcu uninstall-dir <dir>` | *Uninstall from directory* | Runs any real uninstaller it finds there, then removes the rest as leftovers |
| `bcu target <pid\|process\|file\|dir> [--uninstall]` | *Target* (uninstall by window, process or file) | Lists the owning apps; `--uninstall` sends them to the bulk flow (quiet) |
| `bcu manual-uninstall <name...>` | *Manual uninstall* | Removes leftovers and the registry entry without running the uninstaller |
| `bcu clean-program-files` | *Clean up Program Files* | Orphaned folders (same as `bcu junk` with no name) |
| `bcu notes list\|get\|set\|clear` | *Custom notes* (upstream v6.3) | Stored in `CustomNotes.xml` next to the exe, shared with the GUI |
| `bcu restore-point [desc]` | *Create restore point* | Also available as `--restore-point` on uninstall, bulk and msi |
| `bcu reg-backup <name...> -o f.reg` | *Create registry backup* | Also available as `--reg-backup f.reg`, which aborts the uninstall if the backup fails |
| `bcu make-list <name...> -o f.bcul` | *Include/Exclude in advanced filters* and save the list | `--exclude`, `--append`; also accepts `--filter` / `--preset` |
| `bcu open <target> --what install\|uninstaller\|source\|web\|registry` | *Open …* | Prints the path; `--launch` opens it (interactive only) |
| `bcu search-online <target> --site …` | *Search online* | Prints the URL; `--launch` opens it |
| `bcu run <target> [--index N]` | *Run* submenu | Interactive only |
| `bcu take-ownership <target>` | *Take ownership* | `takeown` + `icacls`; needs elevation |
| `bcu tools netfx3\|features\|disk-cleanup\|troubleshoot\|programs-and-features\|system-restore` | *Tools* menu | `netfx3` and `features` work headless; the rest open windows |
| `bcu startup delete\|backup\|all-users\|current-user\|move-to-registry` | Startup Manager | `--type normal\|task\|service\|browser` |
| `bcu export --format store-ps1` | *Export Store apps removal script* | |

**New options on uninstall / bulk / msi / uninstall-dir / manual-uninstall.** They
replicate the GUI's uninstall wizard and settings:

| Option | GUI equivalent |
|--------|----------------|
| `--with-related` | Wizard's related-applications step |
| `--close-apps` | "Close running applications" dialog: kills processes loaded from the app's folders |
| `--restore-point` | Settings › *Create restore point* |
| `--reg-backup <file.reg>` | Registry backup before the change |
| `--pre-command` / `--post-command <cmd>` (repeatable) | Settings › *External commands* |
| `--simulate` | Settings › *Simulate* (engine-level dry run) |
| `--no-intelligent-sort` | Turns off *Intelligent uninstaller sorting* (on by default, like the GUI) |
| `--junk` on `bulk` | Post-uninstall leftover scan of completed entries |
| `--format json` | Machine-readable plan/result, one JSON document on stdout (for RMM) |

**New list filters.** They match the GUI's sidebar and View menu: `--preset
basic|advanced|everything|system|startup|browsers|tweaks|orphaned|updates|invalid|features|store|protected`,
`--kind Msiexec,Nsis,…`, `--hide-microsoft`, `--invalid`, and `--list <file.bcul>`.
JSON output also gained `customNote`, `isInvalid`, `isTweak`, `isWebBrowser`,
`msiProductCode`, `uninstallerLocation` and `installSource`.

**New scan settings.** They match the GUI's Settings › Folders / Quiet / Cache
pages: `--no-predefined`, `--custom-folders "a;b"`, `--no-folder-autodetect`,
`--scan-removable`, `--quiet-automation`, `--quiet-automation-kill-stuck`,
`--use-daemon` and `--cache`.

**Not ported:**
- User ratings: they talk to BCU's web rating service from GUI-only code.
- Clipboard copy: use `bcu info` or `--format json` instead.
- GUI-only settings, the updater and the setup wizard.

### RMM usage (ConnectWise Automate, Datto RMM, …)

RMM agents run scripts as **SYSTEM**, with no desktop and no stdin, and judge
the result by exit code and output. Recommended pattern:

```bat
:: Inventory: registry-only, fast, no helper EXEs
bcu.exe list --rmm-safe --format json --quiet > "%TEMP%\apps.json"

:: Remove an app silently; JSON result on stdout, progress on stderr
bcu.exe uninstall "7-Zip" --quiet-uninstall --close-apps --junk --format json --quiet --yes
if %ERRORLEVEL%==9 echo Reboot required

:: Remove several apps with the same engine the GUI uses
bcu.exe bulk "WildTangent" "McAfee WebAdvisor" --prefer-quiet --auto-kill-stuck --retry-failed ^
        --restore-point --junk --format json --yes

:: MSI by product code
bcu.exe msi --msi-guid {23170F69-40C1-2702-2301-000001000000} --mode quiet --yes
```

- Always pass `--yes` for the change to happen. Without it you get a dry-run plan
  and exit code 0.
- Loud (non-quiet) uninstallers can't show a window under SYSTEM. Prefer
  `--quiet-uninstall` / `--prefer-quiet` and `--auto-kill-stuck`. The `serve`
  daemon can broker a run into the logged-on user's session (see below).
- `--launch`, `run`, `tools disk-cleanup` and similar commands open windows, so
  they are for interactive use only.

---

## Options

### List / Export
| Flag | Description |
|------|-------------|
| `--format table\|json\|csv\|xml\|bat\|ps1` | Output format. `xml` = native BCU list (needs `--output`); `bat`/`ps1` = uninstall scripts |
| `--filter <text>` | Filter by name or publisher |
| `--sort name\|publisher\|date\|size\|source` | Sort order (default: name) |
| `--wide` | Show extra columns (type, date, size) |
| `--verify-certs` | Check digital signatures (adds `Signed`/`Valid` columns and JSON fields) |
| `--quiet, -q` | Suppress progress text (sent to stderr) |
| `--json-errors` | Emit machine-readable error JSON |
| `--system` / `--updates` / `--orphaned` / `--all` | Include hidden categories |
| `--output <file>, -o` | Write output to file |

> `--verify-certs` fetches and validates each entry's signing certificate, which
> is slow on a full list. Combine with `--filter` when possible.

### Uninstall
| Flag | Description |
|------|-------------|
| `--registry-path <path>` | Target by registry uninstall key path |
| `--rating-id <id>` | Target by BCU rating id |
| `--exact` | Exact name match only |
| `--quiet-uninstall` | Use silent uninstaller if available |
| `--safe-mode` | Don't rewrite the uninstall command (disables the NSIS workaround) |
| `--yes, -y` | Actually execute (without it: dry-run) |
| `--dry-run` | Force dry-run even with `--yes` |
| `--junk` | Also clean leftover junk after uninstall |
| `--junk-level <level>` | Minimum confidence threshold |

### Bulk  (`bcu bulk <name...>` or `bcu uninstall <name...> --bulk`)
Multiple names without `--bulk` is an error (guards against fat-fingered mass uninstall).

| Flag | Description |
|------|-------------|
| `--prefer-quiet` | Prefer silent uninstallers |
| `--concurrent <N>` | Run up to N uninstallers at once (default 1) |
| `--auto-kill-stuck` | Force-kill stuck quiet uninstallers |
| `--retry-failed` | Retry failed quiet uninstalls loudly |
| `--ignore-protected` | Include protected entries (default: skip them) |
| `--no-loud-limit` | Allow multiple visible uninstallers at once |
| `--yes` | Execute (otherwise dry-run lists the targets) |

### Junk
| Flag | Description |
|------|-------------|
| `--junk-level VeryGood\|Good\|Questionable\|Bad\|Unknown` | Confidence threshold (default: Good) |
| `--backup <dir>` | Back up each item before deleting (registry keys export a `.reg`); an item is **skipped** if its backup fails. File/dir junk also goes to the **Recycle Bin** regardless. |
| `--format json\|csv` | Machine-readable junk preview/result (with `--output <file>`) for RMM review-before-delete |
| `--yes, -y` | Delete (without it: preview only) |
| `--dry-run` | Force preview even with `--yes` |

> Cleanup reports **accurate** deleted/failed counts — a delete that silently fails
> (locked file, denied registry key, failed cleanup command) is now surfaced as a
> failure, not counted as success. The `junk` command, `serve junk.*`, and the TUI
> `c` key all share one cleanup path.

### Execution context
| Flag | Description |
|------|-------------|
| `--run-as system` | Hint: service-context inventory and quiet uninstall work |
| `--run-as active-user` | Hint: UI/session-bound launches via an RMM agent wrapper |

---

## Sources

Registry scanning is enabled by default. Drive scanning is opt-in because it is
slow and unreliable in headless RMM contexts.

| Flag | Description |
|------|-------------|
| `--rmm-safe` | Registry-only scan for service-context use (disables all other sources) |
| `--drives` | Enable drive-based directory detection |
| `--oculus` | Enable Oculus scanning |
| `--no-registry` / `--no-drives` / `--no-steam` / `--no-store` / `--no-choco` / `--no-scoop` / `--no-features` / `--no-updates` | Skip a specific source |
| `--source-timeout <secs>` | Max seconds to wait for any detection helper EXE (Steam/Store/WinUpdate/Oculus/Script) before killing it and treating that source as empty. Default 120; `0` = wait indefinitely. Prevents a hung helper from hanging the whole scan. |

> **Source helpers ship with the build.** The engine invokes helper EXEs by path
> from the app folder (each `File.Exists`-guarded). `BCU-console.csproj`
> references them so they build next to `bcu.exe` automatically:
> `SteamHelper.exe`, `StoreAppHelper.exe`, `OculusHelper.exe`, `ScriptHelper.exe`,
> and `UninstallerAutomatizer.exe` (the quiet/automated-uninstall engine).
> **`WinUpdateHelper.exe` is the exception** — it has a COM reference and only
> builds under Framework MSBuild (see [Building from source](#building-from-source)),
> so under `dotnet build` Windows Update scanning is unavailable and degrades
> gracefully. Any missing helper just means that one source returns nothing.

---

## Junk confidence levels

| Level | Meaning |
|-------|---------|
| `VeryGood` | Very high confidence — safe to delete |
| `Good` | High confidence (default threshold) |
| `Questionable` | May be shared with other apps — review carefully |
| `Bad` | Likely false positive — not recommended |
| `Unknown` | Insufficient data |

---

## Legacy back-compatibility

For scripts written against the original `BCU-console`:

- **`bcu uninstall <list.bcul>`** — if the uninstall argument is an existing file
  that parses as a BCU uninstall list, every installed app the list matches is
  uninstalled (the old behaviour), routed through the same plan/dry-run/`--yes`
  flow as the by-name path. A non-file argument stays on the by-name path.
- **Legacy switches** (recognised on any command, mapped onto the new model):

  | Legacy | Maps to |
  |--------|---------|
  | `/Q` | prefer quiet uninstallers (`--quiet-uninstall` / `--prefer-quiet`) |
  | `/U` | unattended — **execute** (equivalent to `--yes`) |
  | `/V` | verbose (progress already streams to stderr; no-op) |
  | `/J[=Level]` | clean junk after uninstall (default level `VeryGood`) |

> Behaviour change worth knowing: the old console *prompted* `[Y]/[N]` when `/U`
> was absent. The merged CLI never blocks — without `/U` (or `--yes`) it
> **dry-runs** instead of prompting. Safer, and won't hang an automated shell.

---

## Exit codes

Stable, documented codes (defined in `ExitCodes.cs`). Rule of thumb: **`0` =
fully successful; any non-zero = not fully successful.** `1` stays a generic
catch-all so existing `exit != 0` checks keep working.

| Code | Name | Meaning |
|------|------|---------|
| `0` | Success | Completed — including a dry-run that printed its plan |
| `1` | Error | Generic / unexpected failure |
| `2` | BadUsage | Invalid arguments or command usage |
| `3` | NotFound | The requested target app/entry was not found |
| `4` | PartialFailure | A multi-item op (`bulk`/`junk`/`startup`) finished but one or more items failed |
| `5` | NeedsElevation | Requires administrator/elevation that wasn't available *(reserved)* |
| `6` | NeedsUserSession | Requires an interactive user session — e.g. Store-app or GUI-automated uninstall under SYSTEM/Session 0 *(reserved)* |
| `7` | Timeout | A source scan or operation exceeded its timeout |
| `8` | Cancelled | Cancelled by the user (Ctrl+C) |
| `9` | RebootRequired | The uninstaller/msiexec succeeded but asked for a reboot (3010 / 1641) |

A single `uninstall`, `msi`, `repair` or `modify` maps the uninstaller's own exit
code onto this table (0 → 0, 3010/1641 → 9, 1602/1223 → 8, anything else → 1), and
prints the raw code. Earlier builds returned 0 for a single uninstall even when the
uninstaller failed.

State-changing multi-item commands (`bulk`, `junk`, `startup enable/disable`)
return `PartialFailure` (4) if **any** item failed, so RMM jobs can distinguish
"all good" from "some failed". Codes `5`/`6` are defined and reserved for the
upcoming context-aware work (see the repo wiki's RMM page).

### Cancellation
`Ctrl+C` cancels the **scan** cooperatively and exits `Cancelled` (8). A second
`Ctrl+C` force-quits. An **in-flight uninstall is not interrupted** — that is
deliberate, so a partially-removed application can't be left behind.

---

## Examples

```powershell
# Inventory
bcu list --format json --quiet          # RMM-safe installed app inventory
bcu list --filter chrome                # Filter by name or publisher
bcu list --wide --verify-certs          # Extra columns + signature check
bcu list --sort size --wide             # Sort by size

# Export
bcu export apps.json                    # JSON to file
bcu export --format csv apps.csv
bcu export --format xml -o apps.xml     # Native BCU list (requires -o)
bcu export --format ps1 -o wipe.ps1     # PowerShell uninstall script
bcu export --format bat -o wipe.bat     # Batch uninstall script

# Uninstall (one)
bcu uninstall "Git" --quiet-uninstall --yes
bcu uninstall --registry-path "HKLM\..." --yes
bcu uninstall notepad++ --junk --yes    # Uninstall + clean leftovers

# Uninstall (many)
bcu bulk "Toolbar" "Ask" "Weather" --prefer-quiet --concurrent 2 --yes
bcu uninstall "A" "B" "C" --bulk --yes  # equivalent

# Legacy list form (back-compat)
bcu uninstall removelist.bcul /Q /U     # unattended, quiet, from a saved list

# Maintenance actions
bcu repair "Microsoft Office" --yes
bcu modify "Some MSI App" --yes
bcu rename "App (x64)" "App" --yes
bcu delete-entry "Broken Entry" --yes   # registry entry only; files remain

# Startup
bcu startup list
bcu startup disable Spotify --yes
bcu startup enable  Spotify --yes

# Info + junk
bcu info "Visual Studio Code"
bcu junk "Discord" --junk-level VeryGood --yes
bcu junk --junk-level VeryGood          # orphaned files across all apps

# Load a saved BCU list
bcu import-list apps.xml --format json

bcu help
```

---

## Building from source

**Prerequisites**
- .NET 8 SDK (the project targets `net8.0-windows10.0.18362.0`; a 9.x SDK builds
  it fine via the pinned TFM).
- Windows (uses WinForms-hosted helper tooling).

**Build with `dotnet` (CI-friendly; everything except Windows Update):**

```powershell
# from the repository root
dotnet build source/BCU-console/BCU-console.csproj -c Release
```

Output: `bin\Release\bcu.exe` + `bin\Release\BCU-console.exe` (alias), with
`SteamHelper.exe`, `StoreAppHelper.exe`, `OculusHelper.exe`, `ScriptHelper.exe`
and `UninstallerAutomatizer.exe` co-located via project references. `dotnet`
uses Core MSBuild, which **cannot** resolve `WinUpdateHelper`'s COM reference
(`MSB4803`), so that helper is conditionally excluded — Windows Update scanning
is unavailable in this build.

**Build with Framework MSBuild (full — includes Windows Update):**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\<Edition>\MSBuild\Current\Bin\MSBuild.exe" `
    source/BCU-console/BCU-console.csproj /t:Build /p:Configuration=Release
```

Framework MSBuild resolves the `WUApiLib` COM reference, so all six helpers —
including `WinUpdateHelper.exe` — are produced. This is the same toolchain
upstream's `publish.bat` uses.

> The headless/single-file engine patches required for this CLI are already baked
> into `source/UninstallTools` on this fork (they were previously tracked in
> bcu-cli as `bcu-engine-headless-patches.patch`):
> - **`FastSizeGenerator.cs`** — managed directory walk instead of the
>   `Scripting.FileSystemObjectClass` COM dependency (which fails under
>   single-file/headless).
> - **`UninstallTools.csproj`** — drops the `Scripting` COMReference.
> - **`UninstallToolsGlobalConfig.cs`** — falls back to `AppContext.BaseDirectory`
>   when `Assembly.Location` is empty (single-file publish) so helper EXEs are found.

**Self-contained single-file `bcu.exe`** is *not yet wired up* in this project.
The old standalone bcu-cli published one by copying helpers from
`..\BCU\bin\Release\` with `ExcludeFromSingleFile=true`. Porting that into this
csproj is a known TODO — until then, ship `bcu.exe` with its sibling DLLs and
helper EXEs (a normal build output folder), not as a lone file.

---

## Architecture

```
source/BCU-console/
├── Program.cs          # Command dispatch (top-level statements) + list/export/uninstall/junk/help
│                       #   incl. legacy .bcul list-uninstall path
├── Engine.cs           # Scan/filter/sort/target-resolution over UninstallTools
├── Output.cs           # Table/JSON/CSV rendering + certificate columns
├── BulkUninstall.cs    # Bulk uninstall: Run (by-name) + RunForEntries (shared executor)
├── EntryActions.cs     # repair / modify / rename / delete-entry / info
├── StartupCommands.cs  # startup list / enable / disable
├── Exporters.cs        # xml / bat / ps1 export + import-list
├── CliArgs.cs          # Argument parser (+ legacy /Q /U /V /J switches)
├── AppRecord.cs        # JSON/CSV serialization DTO
└── BCU-console.csproj  # OutputType Exe, AssemblyName=bcu, BCU-console.exe alias target
```

> Note: the merged sources keep the `BcuCli` namespace. The project's
> `RootNamespace` is still `BCU_console`; the two coexist without issue.

A thin CLI layer over BCU's `UninstallTools` library:
- **Scanning**: `ApplicationUninstallerFactory.GetUninstallerEntries()`
- **Junk detection**: `JunkManager.FindJunk()` / `FindProgramFilesJunk()`
- **Uninstalling**: `entry.RunUninstaller(silent, simulate, safeMode)`
- **Bulk uninstall**: `UninstallManager.CreateBulkUninstallTask()` + `BulkUninstallTask`
- **Repair/Modify**: `entry.UninstallUsingMsi(MsiUninstallModes.InstallModify)` / `entry.Modify()`
- **Rename**: `entry.Rename()` · **Delete entry**: `RegistryTools.RemoveRegistryKey()`
- **Startup**: `StartupManager.GetAllStartupItems()` + `StartupEntryBase.Disabled`
- **Certificates**: `entry.GetCertificate()` / `entry.IsCertificateValid()`
- **List import/export**: `ApplicationEntrySerializer` (native XML) / `UninstallList`
- **Config**: `UninstallToolsGlobalConfig` flags control which sources are scanned

---

## Credits

- [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) by
  Marcin Szeniak — Apache License 2.0. This is a personal fork; the CLI extends
  the project's existing `BCU-console` front-end.
