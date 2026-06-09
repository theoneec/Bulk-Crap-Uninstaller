# Fork changes

This is a **personal fork** of [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller)
by Marcin Szeniak (Apache License 2.0). This file is the single, maintained
record of how this fork diverges from upstream — keep it current whenever you
change something that upstream doesn't have.

- **Upstream:** `Klocman/Bulk-Crap-Uninstaller` (remote `upstream`)
- **Fork origin:** `luadmin/Bulk-Crap-Uninstaller` (Forgejo), default branch `master`
- **Forked from:** upstream `master` at `4ecea11b` (3 commits past tag `v6.1`)
- **Wiki:** [helper + RMM docs](http://192.168.70.165:3000/luadmin/Bulk-Crap-Uninstaller/wiki) — per-helper reference, the invocation protocol, and the CLI/RMM improvement roadmap.

---

## Summary of divergences

### 1. Headless / single-file engine patches  (`source/UninstallTools`)
The `UninstallTools` engine couldn't run in a self-contained single-file /
headless build. Three changes fix that:

| File | Change |
|------|--------|
| `Factory/InfoAdders/FastSizeGenerator.cs` | Replaced the `Scripting.FileSystemObjectClass` COM dependency (fails under single-file/headless) with a managed directory walk. |
| `UninstallToolsGlobalConfig.cs` | Falls back to `AppContext.BaseDirectory` when `Assembly.Location` is empty (as under single-file publish) so bundled helper EXEs are still found. |
| `UninstallTools.csproj` | Removed the `Scripting` COMReference (breaks single-file publish). |

> Previously tracked in the `bcu-cli` repo as `bcu-engine-headless-patches.patch`;
> now committed directly here.

### 2. Complete command-line front-end  (`source/BCU-console`)
Upstream `BCU-console` was a 3-command stub (`list` / `export` / `uninstall`),
could only uninstall from a pre-built `.bcul` list, and blocked on
`Console.ReadKey()` — it hung even when asked to show help. It has been replaced
with the full `bcu-cli` engine CLI.

- **New/added files** (merged from `bcu-cli`, namespace `BcuCli`):
  `Engine.cs`, `Output.cs`, `BulkUninstall.cs`, `EntryActions.cs`,
  `StartupCommands.cs`, `Exporters.cs`, `AppRecord.cs`, `CliArgs.cs`.
- **Rewritten:** `Program.cs` — full command dispatcher (top-level statements),
  replacing the upstream stub.
- **Capabilities added:** by-name uninstall, bulk uninstall, repair/modify/
  rename/delete-entry, startup management, info dump, junk scan/clean,
  multi-format export (json/csv/xml/bat/ps1), list import, certificate
  verification, RMM-safe source defaults, dry-run-by-default safety model,
  fully non-blocking I/O.
- **Back-compat preserved:** `uninstall <list.bcul>` still works, and the legacy
  `/Q /U /V /J[=Level]` switches are mapped onto the new model.
- **Build/output changes** (`BCU-console.csproj`): `AssemblyName=bcu` (outputs
  **`bcu.exe`**), a post-build **`BCU-console.exe`** alias for back-compat,
  top-level-statement entry point (removed `<StartupObject>`), and
  `ImplicitUsings` + `Nullable` enabled.
- **Helper wiring** (`BCU-console.csproj`): added `ProjectReference`s
  (`ReferenceOutputAssembly=false`) to the engine helper EXEs so they build next
  to `bcu.exe` when the project is built on its own — `SteamHelper`,
  `StoreAppHelper`, `OculusHelper`, `ScriptHelper`, `UninstallerAutomatizer`
  (quiet/automated-uninstall + bulk quiet daemon), and `WinUpdateHelper`.
  `WinUpdateHelper` is gated to Framework MSBuild via
  `Condition="'$(MSBuildRuntimeType)' != 'Core'"` because its `WUApiLib` COM
  reference can't be resolved by `dotnet build` (MSB4803). Upstream relied on a
  full-solution build to co-locate these; this makes the CLI self-sufficient.
- **Docs:** `source/BCU-console/README.md` — full user + dev reference (new file).

### 3. Licensing / attribution
- Apache-2.0 change notices added to all modified upstream files (§4b), original
  copyright retained.
- Apache-2.0 headers added to the new `BCU-console` source files (`theoneec`).
- `Licence.txt` and `NOTICE` retained unmodified, and bundled into every release
  zip (§4a/§4d for binary redistribution).

### 4. Releases
Published to the Forgejo repo under tag `v6.1.0-cli.1` as a single **portable**
asset — download, unzip, run; no installer and no .NET runtime required:

- `BulkCrapUninstaller-v6.1.0-cli.1-win-x64-portable.zip` (~80 MB) — a
  **self-contained win-x64** bundle of the whole app: GUI (`BCUninstaller.exe`)
  + CLI (`bcu.exe` / `BCU-console.exe`) + all six engine helpers (incl.
  `WinUpdateHelper`) + the bundled runtime, plus `Licence.txt`, `NOTICE`,
  `PrivacyPolicy.txt`, `README.md`, `READ-ME-FIRST.txt`.

Built by Framework-MSBuild `/t:Publish /p:SelfContained=True /p:RuntimeIdentifier=win-x64`
of the .NET projects (GUI + BCU-console + 6 helpers) into one folder — mirrors
`publish.bat` but **without the native `BCU-launcher` (C++/v143)**, which isn't
installed here and isn't needed for a single-arch portable zip (run the exes
directly). Build commands are in this file's history.

---

### 5. CLI hardening (P0) + JSON-RPC API daemon
Tracked as Forgejo issues #1–#5 (closed), with #6 for follow-ups.

- **Exit codes** (#1): `ExitCodes.cs` — documented table (Success/Error/BadUsage/
  NotFound/PartialFailure/NeedsElevation/NeedsUserSession/Timeout/Cancelled),
  mapped across every command. `1` stays a generic catch-all.
- **Helper timeouts** (#2): `FactoryTools` reads stdout async + bounds helpers with
  `UninstallToolsGlobalConfig.HelperProcessTimeout` (default 120 s, kills the tree);
  CLI `--source-timeout <secs>`.
- **Cancellation** (#3): `Console.CancelKeyPress` → token → scan callback
  `ThrowIfCancellationRequested` → exit `Cancelled`. In-flight uninstalls not
  interrupted by design.
- **Headless errors** (#4): `PremadeDialogs.HeadlessErrorHandler` — engine errors go
  to stderr instead of a modal dialog / stdout (keeps `--format json` clean).
- **`bcu serve`** (#5): named-pipe JSON-RPC daemon (`ServeCommand.cs`) — `ping`,
  `inventory.list` (cached), `app.info`, `app.uninstall`, `bulk.uninstall`,
  `junk.scan`/`junk.clean`, `shutdown`; dry-run unless `confirm:true`. Async jobs +
  SYSTEM→user-session broker deferred to #6.

---

## Known TODOs / not-yet-done

- **Windows Update scanning under `dotnet build`** — `WinUpdateHelper` is
  excluded from `dotnet`/Core MSBuild builds (COM reference, MSB4803). Build with
  Framework MSBuild (`msbuild.exe`, as `publish.bat` does) to include it.
  The other five helpers build under both toolchains.
- **Reproducible packaging script** — the v6.1.0-cli.1 portable zip was produced
  with ad-hoc publish commands. A committed `build-release.ps1` (self-contained
  publish of the .NET projects, stage license/docs/alias/read-me, zip) would make
  releases repeatable.
- **Native launcher / multi-arch** — `BCU-launcher` (C++) is skipped (no v143
  toolset here). To ship the upstream-style multi-arch portable (one
  `BCUninstaller.exe` launcher + `win-x64`/`win-arm64` subfolders), install the
  VS C++ workload and run the full `publish.bat` flow.
- **Self-contained _single-file_ `bcu.exe`** is still not wired up. The shipped
  self-contained package is a folder (bcu.exe + helpers + runtime), not a single
  bundled exe. The old standalone `bcu-cli` produced a single file via
  `ExcludeFromSingleFile=true` for the helpers; porting that is outstanding.

---

## Pulling upstream updates

```bash
git fetch upstream
git merge upstream/master        # or: git rebase upstream/master
# Resolve conflicts (most likely in the 3 patched UninstallTools files
# and source/BCU-console/*), then update this file if the divergence changed.
```

---

## Changelog (fork commits, newest first)

| Commit | Description |
|--------|-------------|
| `0f315e77` | feature: `bcu serve` named-pipe JSON-RPC API helper (#5) |
| `8c8b1fae` | P0: headless-safe engine errors, no UI dialogs (#4) |
| `423d4322` | P0: cooperative Ctrl+C cancellation of scans (#3) |
| `0fde8e39` | P0: helper invocation timeout (#2) |
| `581e6051` | P0: structured, documented CLI exit codes (#1) |
| `v6.1.0-cli.1` | **Release** — portable self-contained win-x64 bundle (GUI + CLI + 6 helpers) on Forgejo |
| `98346135` | Wire engine helper EXEs into the BCU-console build |
| `cbd495ce` | License compliance: Apache-2.0 headers and change notices |
| `1aa6b90f` | Document the merged BCU-console CLI |
| `87ae2971` | Merge bcu-cli into BCU-console: complete the command-line front-end |
| `41d03829` | Apply headless/single-file engine patches to UninstallTools |
| `4ecea11b` | *(upstream base — last commit shared with Klocman/master)* |

> When you add a fork-specific change: update the relevant section above and add
> a row here. Keep `4ecea11b` as the marker for the upstream fork point until you
> re-sync with upstream.
