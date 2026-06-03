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
| `help` | Show help |

**Targeting** (uninstall/repair/modify/rename/delete-entry/info/junk): by name
(partial, or `--exact`), `--registry-path <path>`, or `--rating-id <id>`. Stable
identifiers (`--registry-path` / `--rating-id`) are recommended for RMM
automation because display names change between versions.

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
| `--yes, -y` | Delete (without it: dry-run lists items) |
| `--dry-run` | Show what would be deleted |

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

| Code | Meaning |
|------|---------|
| `0` | Success (or dry-run completed) |
| non-zero (typically `1`) | Invalid usage, target not found, or one or more operations failed |

State-changing commands (`bulk`, `junk`, …) return non-zero if **any** target
failed, so RMM jobs can detect partial failures.

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
