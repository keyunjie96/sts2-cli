using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Track whether stdout is still writable (pipe not broken).</summary>
    private static volatile bool _stdoutAlive = true;

    static int Main(string[] args)
    {
        // ── Global crash prevention ──────────────────────────────────────
        // AppDomain.UnhandledException fires *after* the runtime decides to
        // terminate, so we can only log — but at least we leave a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
                Console.Error.Flush();
            }
            catch { /* stderr itself may be broken */ }
        };

        // Catch fire-and-forget Task exceptions so the finalizer thread
        // doesn't re-raise them and crash the process.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception}");
                Console.Error.Flush();
            }
            catch { }
            e.SetObserved();
        };

        // Graceful shutdown on Ctrl+C / SIGINT — log before exit.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false; // let the process terminate
            try
            {
                Console.Error.WriteLine("[INFO] Received SIGINT (Ctrl+C), shutting down");
                Console.Error.Flush();
            }
            catch { }
        };

        // SIGTERM handler (exit code 143 = 128+15) — .NET exposes this via
        // ProcessExit on non-Windows platforms.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Console.Error.WriteLine("[INFO] ProcessExit event fired, shutting down");
                Console.Error.Flush();
            }
            catch { }
        };

        // ── Assembly resolution ──────────────────────────────────────────
        var libDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "lib");
        if (!Directory.Exists(libDir))
            libDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib");
        if (!Directory.Exists(libDir))
            libDir = Path.Combine(AppContext.BaseDirectory, "lib");

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        // ── Main loop ────────────────────────────────────────────────────
        RunSimulator? sim = null;
        try
        {
            sim = new RunSimulator();
            WriteLine(new Dictionary<string, object?> { ["type"] = "ready", ["version"] = "0.2.0" });

            string? line;
            while (true)
            {
                try
                {
                    line = Console.ReadLine();
                }
                catch (IOException ex)
                {
                    // Stdin pipe broken (parent process died)
                    Console.Error.WriteLine($"[INFO] Stdin read failed (pipe broken): {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARN] Stdin read error: {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                if (line == null)
                {
                    // EOF on stdin — parent process closed the pipe
                    Console.Error.WriteLine("[INFO] Stdin EOF, exiting");
                    break;
                }

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                Dictionary<string, object?>? result;
                try
                {
                    var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                    result = HandleCommand(sim, cmd);
                }
                catch (JsonException ex)
                {
                    result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
                }
                catch (OutOfMemoryException)
                {
                    // OOM — log and exit immediately, don't try to allocate more
                    Console.Error.WriteLine("[FATAL] OutOfMemoryException processing command");
                    return 1;
                }
                catch (StackOverflowException)
                {
                    // This is normally uncatchable, but just in case the runtime
                    // routes it through managed code on some paths:
                    Console.Error.WriteLine("[FATAL] StackOverflowException processing command");
                    return 1;
                }
                catch (Exception ex)
                {
                    result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}" };
                    Console.Error.WriteLine($"[WARN] Command exception: {ex.GetType().Name}: {ex.Message}");
                }

                if (result != null)
                {
                    if (!TryWriteLine(result))
                    {
                        Console.Error.WriteLine("[INFO] Stdout write failed, exiting");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Catch-all for anything that escapes the main loop
            try
            {
                Console.Error.WriteLine($"[FATAL] Main loop crashed: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                Console.Error.Flush();
            }
            catch { }
            return 1;
        }
        finally
        {
            // Always clean up the simulator before exiting
            try
            {
                sim?.CleanUp();
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[WARN] CleanUp on exit: {ex.Message}"); } catch { }
            }

            try
            {
                Console.Error.WriteLine("[INFO] Process exiting normally");
                Console.Error.Flush();
            }
            catch { }
        }

        return 0;
    }

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        switch (cmdType)
        {
            case "start_run":
                sim.GodMode = cmd.TryGetProperty("god_mode", out var gm) && gm.GetBoolean();
                return sim.StartRun(
                    cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var s) ? s.GetString() : null,
                    cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en"
                );

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "get_map":
                return sim.GetFullMap();

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                return sim.SetPlayer(args);
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                return sim.EnterRoom(roomType, encounter, eventId);
            }

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                return sim.SetDrawOrder(cards);
            }

            case "quit":
                sim.CleanUp();
                return null;

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    /// <summary>Write a JSON line to stdout. Returns false if the pipe is broken.</summary>
    static bool TryWriteLine(Dictionary<string, object?> data)
    {
        if (!_stdoutAlive) return false;
        try
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
            Console.Out.Flush();
            return true;
        }
        catch (IOException)
        {
            _stdoutAlive = false;
            return false;
        }
        catch (ObjectDisposedException)
        {
            _stdoutAlive = false;
            return false;
        }
    }

    /// <summary>Backward-compatible Write for code that doesn't check the return value.</summary>
    static void WriteLine(Dictionary<string, object?> data)
    {
        TryWriteLine(data);
    }
}
