/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

using System.IO.Pipes;
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
/// CLI's --yes model. The pipe is local (no network surface).
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

    public static int Run(CliArgs args)
    {
        var pipeName = string.IsNullOrWhiteSpace(args.PipeName) ? "bcu" : args.PipeName!;
        Console.Error.WriteLine($"bcu serve: listening on \\\\.\\pipe\\{pipeName}  (JSON-RPC, schema {SchemaVersion})");
        Console.Error.WriteLine("Methods: ping, inventory.list, app.info, app.uninstall, bulk.uninstall, junk.scan, junk.clean, shutdown");
        Console.Error.WriteLine("State-changing methods require \"confirm\": true. Ctrl+C to stop.");

        var stop = false;
        while (!stop && !Engine.CancelToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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

        if (!confirm)
        {
            result["dryRun"] = true;
            return result;
        }

        var proc = entry.RunUninstaller(quiet, false, false);
        proc?.WaitForExit();
        result["executed"] = true;
        result["exitCode"] = proc?.ExitCode ?? 0;
        InvalidateCache();
        return result;
    }

    private static JsonObject BulkUninstallMethod(JsonNode? p)
    {
        var confirm = p?["confirm"]?.GetValue<bool>() ?? false;
        var quiet = p?["quiet"]?.GetValue<bool>() ?? true;
        var ids = (p?["ids"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList()
                  ?? new List<string?>();
        var all = GetInventory(false, false);

        var items = new JsonArray();
        foreach (var id in ids)
        {
            var entry = ResolveById(all, id!);
            var item = new JsonObject { ["id"] = id, ["found"] = entry != null };
            if (entry != null)
            {
                item["name"] = entry.DisplayName;
                if (confirm)
                {
                    try
                    {
                        var proc = entry.RunUninstaller(quiet, false, false);
                        proc?.WaitForExit();
                        item["executed"] = true;
                        item["exitCode"] = proc?.ExitCode ?? 0;
                    }
                    catch (Exception ex) { item["error"] = ex.Message; }
                }
            }
            items.Add(item);
        }
        if (confirm) InvalidateCache();
        return new JsonObject { ["dryRun"] = !confirm, ["count"] = ids.Count, ["items"] = items };
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

        var items = new JsonArray();
        var deleted = 0; var failed = 0;
        foreach (var j in junk)
        {
            var item = new JsonObject
            {
                ["category"] = j.Source?.CategoryName,
                ["name"] = j.GetDisplayName(),
                ["confidence"] = j.Confidence.GetConfidence().ToString()
            };
            if (clean && confirm)
            {
                try { j.Delete(); item["deleted"] = true; deleted++; }
                catch (Exception ex) { item["error"] = ex.Message; failed++; }
            }
            items.Add(item);
        }

        return new JsonObject
        {
            ["dryRun"] = !(clean && confirm),
            ["count"] = junk.Count,
            ["deleted"] = deleted,
            ["failed"] = failed,
            ["items"] = items
        };
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

    private static string Ok(JsonNode? id, JsonNode result) =>
        new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString(Wire);

    private static string Err(JsonNode? id, int code, string message) =>
        new JsonObject { ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }
            .ToJsonString(Wire);
}
