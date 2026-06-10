/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UninstallTools;
using UninstallTools.Junk;
using UninstallTools.Junk.Confidence;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// `bcu serve` — a resident JSON-RPC endpoint over a named pipe, for an RMM agent
/// or API helper to drive the engine without re-scanning per call.
///
/// Transport: newline-delimited JSON. One request object per line:
///   {"id":1,"method":"inventory.list","params":{...}}
/// Response per line:
///   {"id":1,"result":{...}}   or   {"id":1,"error":{"code":N,"message":"..."}}
///
/// Safety: state-changing methods (app.uninstall, bulk.uninstall, junk.clean) only
/// execute with "confirm": true — otherwise they return a dry-run plan. Mirrors the
/// CLI's --yes model. The pipe is local and ACL'd to the current user + SYSTEM.
///
/// Methods: ping, context, inventory.list, app.info, app.uninstall, bulk.uninstall,
/// junk.scan, junk.clean, job.status, job.list, session.launch, shutdown.
///
/// Async jobs: confirmed state-changing ops return {jobId,state:"running"} at once
/// and run on a background task; poll job.status {jobId} / job.list for progress
/// (done/total/failed) and the final result. Under SYSTEM, ops needing an
/// interactive session (Store/UWP, GUI-automation) return needsUserSession and can
/// be brokered into the active console session via session.launch.
/// </summary>
public static class ServeCommand
{
    public const string SchemaVersion = "1.0";

    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Cached inventory so repeated queries are cheap. Keyed by scan mode.
    private static readonly object Gate = new();
    private static List<ApplicationUninstallerEntry>? _cache;
    private static bool _cacheRmmSafe;

    // Async jobs: state-changing ops return a jobId immediately and run on a
    // background task; clients poll job.status / job.list.
    private sealed class Job
    {
        public string Id = "";
        public string Method = "";
        public volatile string State = "running";   // running | completed | failed
        public int Total;
        public int Done;
        public int Failed;
        public JsonNode? Result;
        public string? Error;
        public readonly object Sync = new();
    }

    private static readonly ConcurrentDictionary<string, Job> Jobs = new();
    private static int _jobSeq;

    public static int Run(CliArgs args)
    {
        var pipeName = string.IsNullOrWhiteSpace(args.PipeName) ? "bcu" : args.PipeName!;
        Console.Error.WriteLine($"bcu serve: listening on \\\\.\\pipe\\{pipeName}  (JSON-RPC, schema {SchemaVersion})");
        Console.Error.WriteLine("Methods: ping, context, inventory.list, app.info, app.uninstall, bulk.uninstall,");
        Console.Error.WriteLine("         junk.scan, junk.clean, job.status, job.list, shutdown");
        Console.Error.WriteLine("State-changing methods require \"confirm\": true. Ctrl+C to stop.");
        var ctx = SessionContext();
        Console.Error.WriteLine($"Context: session {ctx.SessionId}, {(ctx.IsSystem ? "SYSTEM" : ctx.UserName)}, " +
                                $"{(ctx.IsElevated ? "elevated" : "not elevated")}, active console session {ctx.ActiveConsoleSession}.");

        var stop = false;
        while (!stop && !Engine.CancelToken.IsCancellationRequested)
        {
            using var server = CreatePipe(pipeName);
            try
            {
                server.WaitForConnectionAsync(Engine.CancelToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[serve] accept failed: {ex.Message}"); continue; }

            using var reader = new StreamReader(server, new UTF8Encoding(false));
            using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

            try
            {
                string? line;
                while (!stop && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var (resp, shouldStop) = Handle(line);
                    writer.WriteLine(resp);
                    stop = shouldStop;
                }
            }
            catch (IOException) { /* client disconnected; accept the next one */ }
        }

        Console.Error.WriteLine("bcu serve: stopped.");
        return ExitCodes.Success;
    }

    // ── Dispatch ────────────────────────────────────────────────────────────────

    private static (string json, bool stop) Handle(string line)
    {
        JsonNode? req;
        try { req = JsonNode.Parse(line); }
        catch { return (Err(null, -32700, "Parse error (invalid JSON)"), false); }

        var idNode = req?["id"];
        var method = req?["method"]?.GetValue<string>();
        var p = req?["params"];

        try
        {
            switch (method)
            {
                case "ping":
                    return (Ok(idNode, new JsonObject { ["pong"] = true, ["schemaVersion"] = SchemaVersion }), false);
                case "context":
                    return (Ok(idNode, ContextJson()), false);
                case "inventory.list":
                    return (Ok(idNode, InventoryList(p)), false);
                case "app.info":
                    return (Ok(idNode, AppInfo(p)), false);
                case "app.uninstall":
                    return (Ok(idNode, AppUninstall(p)), false);
                case "bulk.uninstall":
                    return (Ok(idNode, BulkUninstallMethod(p)), false);
                case "junk.scan":
                    return (Ok(idNode, JunkMethod(p, clean: false)), false);
                case "junk.clean":
                    return (Ok(idNode, JunkMethod(p, clean: true)), false);
                case "job.status":
                    return (Ok(idNode, JobStatus(p)), false);
                case "job.list":
                    return (Ok(idNode, JobList()), false);
                case "session.launch":
                    return (Ok(idNode, SessionLaunch(p)), false);
                case "shutdown":
                    return (Ok(idNode, new JsonObject { ["stopping"] = true }), true);
                default:
                    return (Err(idNode, -32601, $"Unknown method: {method ?? "(none)"}"), false);
            }
        }
        catch (Exception ex)
        {
            return (Err(idNode, 1, ex.Message), false);
        }
    }

    // ── Methods ─────────────────────────────────────────────────────────────────

    private static JsonObject InventoryList(JsonNode? p)
    {
        var refresh = p?["refresh"]?.GetValue<bool>() ?? false;
        var rmmSafe = p?["rmmSafe"]?.GetValue<bool>() ?? false;
        var verifyCerts = p?["verifyCerts"]?.GetValue<bool>() ?? false;
        var filter = p?["filter"]?.GetValue<string>();

        var all = GetInventory(rmmSafe, refresh);
        IEnumerable<ApplicationUninstallerEntry> apps = all;
        if (!string.IsNullOrWhiteSpace(filter))
            apps = apps.Where(e =>
                (e.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        var records = apps.Select(e => new AppRecord(e, verifyCerts)).ToList();
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["count"] = records.Count,
            ["items"] = JsonSerializer.SerializeToNode(records, Wire)
        };
    }

    private static JsonObject AppInfo(JsonNode? p)
    {
        var all = GetInventory(false, false);
        var entry = Resolve(all, p, out var matchCount);
        if (entry == null)
            return new JsonObject { ["found"] = false, ["matchCount"] = matchCount };
        return new JsonObject
        {
            ["found"] = true,
            ["app"] = JsonSerializer.SerializeToNode(new AppRecord(entry, false), Wire)
        };
    }

    private static JsonObject AppUninstall(JsonNode? p)
    {
        var confirm = p?["confirm"]?.GetValue<bool>() ?? false;
        var quiet = p?["quiet"]?.GetValue<bool>() ?? true;
        var all = GetInventory(false, false);
        var entry = Resolve(all, p, out var matchCount);
        if (entry == null)
            return new JsonObject { ["found"] = false, ["matchCount"] = matchCount };

        var result = new JsonObject
        {
            ["found"] = true,
            ["name"] = entry.DisplayName,
            ["registryPath"] = entry.RegistryPath,
            ["command"] = quiet ? (entry.QuietUninstallString ?? entry.UninstallString) : entry.UninstallString
        };

        result["uninstallerKind"] = entry.UninstallerKind.ToString();

        if (!confirm)
        {
            result["dryRun"] = true;
            return result;
        }

        // Session-broker guard: Store/GUI-automation uninstalls need an interactive
        // user session; under SYSTEM they silently no-op, so refuse with a clear signal.
        var ctx = SessionContext();
        if (ctx.IsSystem && NeedsUserSession(entry))
        {
            result["needsUserSession"] = true;
            result["exitCodeHint"] = ExitCodes.NeedsUserSession;
            result["canBrokerToUserSession"] = ctx.ActiveConsoleSession > 0;
            result["message"] = "This uninstaller (Store/GUI-automation) needs an interactive user session; " +
                                "serve is running as SYSTEM. Use session.launch to run it in the active console session.";
            return result;
        }

        var job = NewJob("app.uninstall");
        job.Total = 1;
        RunJob(job, () =>
        {
            var proc = entry.RunUninstaller(quiet, false, false);
            proc?.WaitForExit();
            var code = proc?.ExitCode ?? 0;
            job.Done = 1;
            if (code != 0) job.Failed = 1;
            job.Result = new JsonObject { ["name"] = entry.DisplayName, ["exitCode"] = code };
            InvalidateCache();
        });
        result["jobId"] = job.Id;
        result["state"] = job.State;
        return result;
    }

    private static JsonObject BulkUninstallMethod(JsonNode? p)
    {
        var confirm = p?["confirm"]?.GetValue<bool>() ?? false;
        var quiet = p?["quiet"]?.GetValue<bool>() ?? true;
        var ids = (p?["ids"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList()
                  ?? new List<string?>();
        var all = GetInventory(false, false);
        var resolved = ids.Select(id => (id, entry: ResolveById(all, id!))).ToList();

        if (!confirm)
        {
            var preview = new JsonArray();
            foreach (var (id, entry) in resolved)
                preview.Add(new JsonObject { ["id"] = id, ["found"] = entry != null, ["name"] = entry?.DisplayName });
            return new JsonObject { ["dryRun"] = true, ["count"] = ids.Count, ["items"] = preview };
        }

        var job = NewJob("bulk.uninstall");
        job.Total = resolved.Count(r => r.entry != null);
        RunJob(job, () =>
        {
            var items = new JsonArray();
            foreach (var (id, entry) in resolved)
            {
                var item = new JsonObject { ["id"] = id, ["found"] = entry != null };
                if (entry != null)
                {
                    item["name"] = entry.DisplayName;
                    try
                    {
                        var proc = entry.RunUninstaller(quiet, false, false);
                        proc?.WaitForExit();
                        var code = proc?.ExitCode ?? 0;
                        item["exitCode"] = code;
                        job.Done++;
                        if (code != 0) job.Failed++;
                    }
                    catch (Exception ex) { item["error"] = ex.Message; job.Done++; job.Failed++; }
                }
                items.Add(item);
            }
            job.Result = new JsonObject { ["items"] = items };
            InvalidateCache();
        });
        return new JsonObject { ["jobId"] = job.Id, ["state"] = job.State, ["total"] = job.Total };
    }

    private static JsonObject JunkMethod(JsonNode? p, bool clean)
    {
        var confirm = p?["confirm"]?.GetValue<bool>() ?? false;
        var levelStr = p?["level"]?.GetValue<string>() ?? "Good";
        if (!Enum.TryParse<ConfidenceLevel>(levelStr, true, out var level)) level = ConfidenceLevel.Good;

        var all = GetInventory(false, false);
        var targets = all;
        var name = p?["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
            targets = Engine.FindByName(all, name, false);

        var junk = JunkManager.FindJunk(targets, all, _ => { })
            .Where(j => j.Confidence.GetConfidence() >= level)
            .ToList();

        // Preview list (always returned for scan; for clean dry-run too).
        JsonArray Preview() =>
            new(junk.Select(j => (JsonNode)new JsonObject
            {
                ["category"] = j.Source?.CategoryName,
                ["name"] = j.GetDisplayName(),
                ["confidence"] = j.Confidence.GetConfidence().ToString()
            }).ToArray());

        if (!(clean && confirm))
            return new JsonObject { ["dryRun"] = true, ["count"] = junk.Count, ["items"] = Preview() };

        // clean + confirm => async deletion job
        var job = NewJob("junk.clean");
        job.Total = junk.Count;
        RunJob(job, () =>
        {
            var items = new JsonArray();
            foreach (var j in junk)
            {
                var item = new JsonObject
                {
                    ["category"] = j.Source?.CategoryName,
                    ["name"] = j.GetDisplayName(),
                    ["confidence"] = j.Confidence.GetConfidence().ToString()
                };
                try { j.Delete(); item["deleted"] = true; }
                catch (Exception ex) { item["error"] = ex.Message; job.Failed++; }
                job.Done++;
                items.Add(item);
            }
            job.Result = new JsonObject { ["deleted"] = job.Done - job.Failed, ["failed"] = job.Failed, ["items"] = items };
        });
        return new JsonObject { ["jobId"] = job.Id, ["state"] = job.State, ["total"] = job.Total };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static List<ApplicationUninstallerEntry> GetInventory(bool rmmSafe, bool refresh)
    {
        lock (Gate)
        {
            if (_cache == null || refresh || _cacheRmmSafe != rmmSafe)
            {
                var scanArgs = new CliArgs { Quiet = true };
                if (rmmSafe)
                {
                    scanArgs.RmmSafe = true;
                    scanArgs.ScanDrives = scanArgs.ScanSteam = scanArgs.ScanStore = scanArgs.ScanChoco =
                        scanArgs.ScanScoop = scanArgs.ScanFeatures = scanArgs.ScanUpdates = scanArgs.ScanOculus = false;
                }
                _cache = Engine.Scan(scanArgs).ToList();
                _cacheRmmSafe = rmmSafe;
            }
            return _cache;
        }
    }

    private static void InvalidateCache()
    {
        lock (Gate) _cache = null;
    }

    /// <summary>Resolve a target from request params (registryPath / ratingId / name).</summary>
    private static ApplicationUninstallerEntry? Resolve(IList<ApplicationUninstallerEntry> all, JsonNode? p, out int matchCount)
    {
        matchCount = 0;
        var registryPath = p?["registryPath"]?.GetValue<string>();
        var ratingId = p?["ratingId"]?.GetValue<string>();
        var name = p?["name"]?.GetValue<string>();
        var exact = p?["exact"]?.GetValue<bool>() ?? false;

        if (!string.IsNullOrWhiteSpace(registryPath))
        {
            var hit = all.FirstOrDefault(e => string.Equals(e.RegistryPath, registryPath, StringComparison.OrdinalIgnoreCase));
            matchCount = hit == null ? 0 : 1;
            return hit;
        }
        if (!string.IsNullOrWhiteSpace(ratingId))
        {
            var hit = all.FirstOrDefault(e => string.Equals(e.RatingId, ratingId, StringComparison.Ordinal));
            matchCount = hit == null ? 0 : 1;
            return hit;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            var matches = Engine.FindByName(all, name, exact);
            matchCount = matches.Count;
            return matches.Count == 1 ? matches[0] : null;   // ambiguous -> null (caller sees matchCount)
        }
        return null;
    }

    /// <summary>Resolve a single id that may be a registry path, rating id, or exact name.</summary>
    private static ApplicationUninstallerEntry? ResolveById(IList<ApplicationUninstallerEntry> all, string id)
    {
        var hit = all.FirstOrDefault(e => string.Equals(e.RegistryPath, id, StringComparison.OrdinalIgnoreCase))
                  ?? all.FirstOrDefault(e => string.Equals(e.RatingId, id, StringComparison.Ordinal));
        if (hit != null) return hit;
        var matches = Engine.FindByName(all, id, true);
        return matches.Count == 1 ? matches[0] : null;
    }

    // ── Jobs ────────────────────────────────────────────────────────────────────

    private static Job NewJob(string method)
    {
        var id = "job-" + Interlocked.Increment(ref _jobSeq);
        var job = new Job { Id = id, Method = method };
        Jobs[id] = job;
        return job;
    }

    private static void RunJob(Job job, Action work)
    {
        Task.Run(() =>
        {
            try { work(); job.State = "completed"; }
            catch (Exception ex) { job.Error = ex.Message; job.State = "failed"; }
        });
    }

    private static JsonObject JobJson(Job j)
    {
        lock (j.Sync)
            return new JsonObject
            {
                ["jobId"] = j.Id,
                ["method"] = j.Method,
                ["state"] = j.State,
                ["total"] = j.Total,
                ["done"] = j.Done,
                ["failed"] = j.Failed,
                ["error"] = j.Error,
                ["result"] = j.Result?.DeepClone()
            };
    }

    private static JsonObject JobStatus(JsonNode? p)
    {
        var id = p?["jobId"]?.GetValue<string>();
        if (id == null || !Jobs.TryGetValue(id, out var j)) return new JsonObject { ["found"] = false };
        var o = JobJson(j);
        o["found"] = true;
        return o;
    }

    private static JsonObject JobList()
    {
        var arr = new JsonArray();
        foreach (var j in Jobs.Values) arr.Add(JobJson(j));
        return new JsonObject { ["count"] = Jobs.Count, ["jobs"] = arr };
    }

    // ── Session / context ───────────────────────────────────────────────────────

    private readonly record struct SessionCtx(int SessionId, bool IsSystem, bool IsElevated, string UserName, int ActiveConsoleSession);

    private static SessionCtx SessionContext()
    {
        var sid = 0;
        try { sid = Process.GetCurrentProcess().SessionId; } catch { /* best effort */ }

        bool isSystem = false, elevated = false;
        var user = "";
        try
        {
            using var wi = WindowsIdentity.GetCurrent();
            isSystem = wi.IsSystem;
            user = wi.Name ?? "";
            elevated = new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { /* best effort */ }

        var activeConsole = -1;
        try
        {
            var s = WTSGetActiveConsoleSessionId();
            activeConsole = s == 0xFFFFFFFF ? -1 : (int)s;   // 0xFFFFFFFF == no active console session
        }
        catch { /* best effort */ }

        return new SessionCtx(sid, isSystem, elevated, user, activeConsole);
    }

    private static JsonObject ContextJson()
    {
        var c = SessionContext();
        var warnings = new JsonArray();
        if (c.IsSystem || c.SessionId == 0)
        {
            warnings.Add("Store-app (UWP) inventory/uninstall is per-user and is empty/unavailable under SYSTEM.");
            warnings.Add("GUI-automated (UninstallerAutomatizer) uninstall needs an interactive desktop and won't work in Session 0.");
        }
        if (!c.IsElevated)
            warnings.Add("Not elevated: some uninstalls and Windows Update removal require administrator.");

        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = c.SessionId,
            ["isSystem"] = c.IsSystem,
            ["isElevated"] = c.IsElevated,
            ["userName"] = c.UserName,
            ["activeConsoleSession"] = c.ActiveConsoleSession,
            ["canBrokerToUserSession"] = c.IsSystem && c.ActiveConsoleSession > 0,
            ["warnings"] = warnings
        };
    }

    /// <summary>True for uninstalls that require an interactive user session (Store/UWP, GUI-automation).</summary>
    private static bool NeedsUserSession(ApplicationUninstallerEntry e) =>
        e.UninstallerKind == UninstallerType.StoreApp ||
        (e.QuietUninstallString?.Contains("UninstallerAutomatizer", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Broker primitive: launch a `bcu` command in the active console (user) session.
    /// Intended for use when serve runs as SYSTEM and needs per-user / GUI-automation work.
    /// NOTE: only functional under SYSTEM with SeTcbPrivilege; verified by review (cannot be
    /// exercised from a normal user session). Fire-and-forget (no cross-session output capture yet).
    /// </summary>
    private static JsonObject SessionLaunch(JsonNode? p)
    {
        var argsLine = p?["args"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(argsLine))
            return new JsonObject { ["ok"] = false, ["message"] = "Missing \"args\" (the bcu command line to run in the user session)." };

        var ctx = SessionContext();
        if (!ctx.IsSystem)
            return new JsonObject { ["ok"] = false, ["message"] = "session.launch is only needed/usable when serve runs as SYSTEM. Run the command directly otherwise." };
        if (ctx.ActiveConsoleSession <= 0)
            return new JsonObject { ["ok"] = false, ["exitCodeHint"] = ExitCodes.NeedsUserSession, ["message"] = "No active console (user) session to broker into." };

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        var (ok, pid, err) = UserSessionLauncher.TryLaunchInActiveSession(exe ?? "bcu", argsLine);
        return new JsonObject { ["ok"] = ok, ["pid"] = pid, ["error"] = err, ["session"] = ctx.ActiveConsoleSession };
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    // ── Pipe ────────────────────────────────────────────────────────────────────

    /// <summary>Create the server pipe restricted to the current user (+ LocalSystem), falling back to the default ACL.</summary>
    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid != null)
            {
                var ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[serve] pipe ACL unavailable ({ex.Message}); using default ACL.");
        }
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private static string Ok(JsonNode? id, JsonNode result) =>
        new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString(Wire);

    private static string Err(JsonNode? id, int code, string message) =>
        new JsonObject { ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }
            .ToJsonString(Wire);
}
