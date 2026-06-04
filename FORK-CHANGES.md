# Fork changes

This is a **personal fork** of [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller)
by Marcin Szeniak (Apache License 2.0). This file is the single, maintained
record of how this fork diverges from upstream — keep it current whenever you
change something that upstream doesn't have.

- **Upstream:** `Klocman/Bulk-Crap-Uninstaller` (remote `upstream`)
- **Fork origin:** `luadmin/Bulk-Crap-Uninstaller` (Forgejo), default branch `master`
- **Forked from:** upstream `master` at `4ecea11b` (3 commits past tag `v6.1`)

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
Published to the Forgejo repo under tag `v6.1.0-cli.1`. Two win-x64 zips, each
also containing `BCU-console.exe`, `Licence.txt`, `NOTICE`, `README.md`:
- **self-contained** — `dotnet publish` of bcu + 5 helpers into one folder
  (shared runtime dedups); no .NET runtime needed on target; no `WinUpdateHelper`.
- **framework-dependent** — Framework-MSBuild `Publish` of all 7 projects; all
  six helpers incl. `WinUpdateHelper`; needs the .NET 8 Desktop Runtime.

Build commands are in this file's history; consider scripting them (see TODOs).

---

## Known TODOs / not-yet-done

- **Windows Update scanning under `dotnet build`** — `WinUpdateHelper` is
  excluded from `dotnet`/Core MSBuild builds (COM reference, MSB4803). Build with
  Framework MSBuild (`msbuild.exe`, as `publish.bat` does) to include it.
  The other five helpers build under both toolchains.
- **Reproducible packaging script** — the v6.1.0-cli.1 zips were produced with
  ad-hoc publish commands. A committed `build-release.ps1` (publish both flavours,
  stage license/docs/alias, zip) would make releases repeatable.
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
| `v6.1.0-cli.1` | **Release** — self-contained + framework-dependent win-x64 zips on Forgejo |
| `98346135` | Wire engine helper EXEs into the BCU-console build |
| `cbd495ce` | License compliance: Apache-2.0 headers and change notices |
| `1aa6b90f` | Document the merged BCU-console CLI |
| `87ae2971` | Merge bcu-cli into BCU-console: complete the command-line front-end |
| `41d03829` | Apply headless/single-file engine patches to UninstallTools |
| `4ecea11b` | *(upstream base — last commit shared with Klocman/master)* |

> When you add a fork-specific change: update the relevant section above and add
> a row here. Keep `4ecea11b` as the marker for the upstream fork point until you
> re-sync with upstream.
