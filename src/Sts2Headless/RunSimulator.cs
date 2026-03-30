using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
    private volatile bool _executing;

    /// <summary>
    /// Safety limit: if the queue grows beyond this during a single drain cycle,
    /// stop draining to prevent unbounded memory growth / pseudo-stack-overflow.
    /// </summary>
    private const int MaxDrainPerCycle = 10_000;

    /// <summary>Number of queued callbacks (for diagnostic logging).</summary>
    public int QueueCount => _queue.Count;

    /// <summary>
    /// Discard all queued callbacks. Used to prevent unbounded memory growth
    /// during long god-mode combats where abandoned callbacks accumulate.
    /// </summary>
    public void ClearQueue()
    {
        int cleared = _queue.Count;
        _queue.Clear();
        if (cleared > 0)
            Console.Error.WriteLine($"[INFO] SyncCtx.ClearQueue: discarded {cleared} queued callbacks");
    }

    /// <summary>
    /// Default per-callback timeout. Any single callback that takes longer
    /// than this is abandoned (the thread it runs on cannot be killed, but we stop waiting).
    /// Reduced from 8s to 5s since we now actively drain the queue while waiting,
    /// so legitimate callbacks complete faster and only true deadlocks hit this timeout.
    /// </summary>
    private const int DefaultPumpCallbackTimeoutMs = 5000;

    /// <summary>
    /// Dispatch a callback to ThreadPool and wait for it, while actively draining
    /// the queue to prevent deadlocks. The classic deadlock: callback awaits something
    /// that posts a continuation to this sync context, but Pump/Post is blocked on
    /// done.Wait() and can't drain the queue. Fix: interleave queue draining with waiting.
    /// Returns true if the callback completed within the timeout, false if abandoned.
    /// </summary>
    private bool RunCallbackWithDrain(SendOrPostCallback d, object? state, int timeoutMs, string caller)
    {
        var done = new ManualResetEventSlim(false);
        Exception? cbException = null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Set our sync context on this ThreadPool thread so that async
            // continuations from game code (especially async-void on-death
            // callbacks like Minion power effects) are posted to our queue
            // instead of running unhandled on the ThreadPool — which would
            // crash the process with an unobserved exception.
            SynchronizationContext.SetSynchronizationContext(this);
            try { d(state); }
            catch (Exception ex) { cbException = ex; }
            finally { done.Set(); }
        });

        // Instead of blocking on done.Wait(timeout), we poll with short waits
        // and drain newly-queued callbacks between polls. This allows async
        // continuations posted by the running callback to execute, breaking
        // the deadlock where the callback waits for a continuation that's
        // stuck in our queue.
        var timer = Stopwatch.StartNew();
        while (!done.IsSet && timer.ElapsedMilliseconds < timeoutMs)
        {
            // Short wait — gives the callback time to run, but doesn't block long
            if (done.Wait(5))
                break;

            // Drain any callbacks that were queued while the main callback ran.
            // These are typically async continuations that the main callback is
            // waiting on — executing them here breaks the deadlock.
            int innerDrained = 0;
            while (innerDrained < 100 && !done.IsSet && _queue.TryDequeue(out var item))
            {
                var (cb, st) = item;
                try
                {
                    cb(st); // Execute inline on THIS thread — safe because _executing is true
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARN] SyncCtx.{caller} inline drain threw: {ex.GetType().Name}: {ex.Message}");
                }
                innerDrained++;
            }
        }

        if (!done.IsSet)
        {
            Console.Error.WriteLine($"[WARN] SyncCtx.{caller}: callback timed out after {timeoutMs}ms (queue={_queue.Count})");
            if (cbException != null)
                Console.Error.WriteLine($"[WARN] SyncCtx.{caller} callback also threw: {cbException.GetType().Name}: {cbException.Message}");
            return false;
        }

        if (cbException != null)
            Console.Error.WriteLine($"[WARN] SyncCtx.{caller} callback threw: {cbException.GetType().Name}: {cbException.Message}");
        return true;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }

        // Execute inline immediately, then drain any nested posts.
        _executing = true;
        try
        {
            if (!RunCallbackWithDrain(d, state, DefaultPumpCallbackTimeoutMs, "Post"))
            {
                _executing = false;
                return;
            }
            // Drain any remaining callbacks that were queued during execution
            int drained = 0;
            while (drained < MaxDrainPerCycle && _queue.TryDequeue(out var postItem))
            {
                var (cb, st) = postItem;
                try
                {
                    if (!RunCallbackWithDrain(cb, st, DefaultPumpCallbackTimeoutMs, "Post.drain"))
                        break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARN] SyncCtx.Post drain threw: {ex.GetType().Name}: {ex.Message}");
                }
                drained++;
            }
            if (!_queue.IsEmpty)
                Console.Error.WriteLine($"[WARN] SyncCtx.Post drain hit limit ({MaxDrainPerCycle}), {_queue.Count} callbacks remaining");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] SyncCtx.Post threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        try
        {
            d(state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] SyncCtx.Send callback threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Pump()
    {
        // Drain queued callbacks. Each callback runs on ThreadPool but we actively
        // drain the queue while waiting, preventing the classic deadlock where a
        // callback's async continuation is stuck in our queue.
        int drained = 0;
        while (drained < MaxDrainPerCycle && _queue.TryDequeue(out var pumpItem))
        {
            var (cb, st) = pumpItem;
            _executing = true;
            try
            {
                if (!RunCallbackWithDrain(cb, st, DefaultPumpCallbackTimeoutMs, "Pump"))
                {
                    _executing = false;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] SyncCtx.Pump threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _executing = false;
            }
            drained++;
        }
        if (!_queue.IsEmpty)
            Console.Error.WriteLine($"[WARN] SyncCtx.Pump hit limit ({MaxDrainPerCycle}), {_queue.Count} callbacks remaining");
    }

    /// <summary>
    /// Pump with a per-callback timeout. If any single callback takes longer than
    /// <paramref name="perCallbackTimeoutMs"/>, we abandon it and skip remaining
    /// callbacks. Returns false if a timeout occurred.
    /// </summary>
    public bool PumpWithTimeout(int perCallbackTimeoutMs = 3000)
    {
        int drained = 0;
        bool timedOut = false;
        while (drained < MaxDrainPerCycle && _queue.TryDequeue(out var pwtItem))
        {
            var (cb, st) = pwtItem;
            _executing = true;
            try
            {
                if (!RunCallbackWithDrain(cb, st, perCallbackTimeoutMs, "PumpWithTimeout"))
                {
                    timedOut = true;
                    _executing = false;
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] SyncCtx.PumpWithTimeout threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _executing = false;
            }
            drained++;
        }
        return !timedOut;
    }
}

/// <summary>
/// Bilingual localization lookup — loads eng/zhs JSON files for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();
    private readonly Dictionary<string, Dictionary<string, string>> _zhs = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
        Load(Path.Combine(baseDir, "localization_zhs"), _zhs);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Get bilingual name: "English / 中文" or just the key if not found.</summary>
    public string Name(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
        var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
        if (en != null && zh != null && en != zh) return $"{en} / {zh}";
        return en ?? zh ?? key;
    }

    public string? En(string table, string key) => _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
    public string? Zh(string table, string key) => _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    internal static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>Language for JSON output: "en" or "zh". Default: "en".</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Return localized string for JSON output based on Lang setting.</summary>
    public string Bilingual(string table, string key)
    {
        if (Lang == "zh")
        {
            var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
            if (zh != null) return StripBBCode(zh);
        }
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    // Convenience helpers using ModelId
    public string Card(string entry) => Bilingual("cards", entry + ".title");
    public string Monster(string entry)
    {
        var result = Bilingual("monsters", entry + ".name");
        if (IsRawKey(result))
            return HumanizeId(entry);
        return result;
    }
    public string Relic(string entry) => Bilingual("relics", entry + ".title");
    public string Potion(string entry) => Bilingual("potions", entry + ".title");
    public string Power(string entry)
    {
        var result = Bilingual("powers", entry + ".title");
        if (IsRawKey(result))
            return HumanizeId(entry.EndsWith("_POWER") ? entry[..^6] : entry);
        return result;
    }
    public string PowerDescription(string entry, int amount)
    {
        // Try smartDescription first — it contains {Amount} references for dynamic values
        var smartKey = entry + ".smartDescription";
        var smart = Bilingual("powers", smartKey);
        if (smart != smartKey && !IsRawKey(smart))
        {
            smart = ResolvePowerTemplates(smart, amount);
            // If the smart description still has unresolved {Var:...} templates
            // (e.g. {DamageIncrease:percentMore()} or {OwnerName}), fall back to
            // the .description which has hardcoded values for those fields.
            if (!smart.Contains('{'))
                return smart;
        }
        // Fall back to .description (has hardcoded values but may still have template patterns)
        var result = Bilingual("powers", entry + ".description");
        if (!IsRawKey(result))
        {
            result = ResolvePowerTemplates(result, amount);
            return result;
        }
        // Fallback: construct basic description from amount
        var name = Power(entry);
        if (amount != 0) return $"{name}: {amount}";
        return name;
    }
    public string Event(string entry) => Bilingual("events", entry + ".title");
    public string Act(string entry) => Bilingual("acts", entry + ".title");

    /// <summary>Check if a localization result is still a raw/unresolved key.</summary>
    private static bool IsRawKey(string result)
    {
        return result.Contains(".title") || result.Contains(".name") || result.Contains(".description");
    }

    /// <summary>Convert a raw ID like "SYNCHRONIZE_POWER" to "Synchronize".</summary>
    internal static string HumanizeId(string id)
    {
        // Strip common suffixes
        if (id.EndsWith("_POWER")) id = id[..^6];
        if (id.EndsWith("_BOSS")) id = id[..^5];
        // Title-case: split on underscores, capitalize first letter of each word
        return string.Join(" ", id.Split('_')
            .Where(s => s.Length > 0)
            .Select(s => char.ToUpper(s[0]) + s[1..].ToLower()));
    }

    /// <summary>Resolve power/card/relic description templates and strip BBCode.
    /// Handles SmartFormat patterns: energyIcons, plural, diff, cond, abs, singleStarIcon.</summary>
    internal static string ResolvePowerTemplates(string desc, int amount)
    {
        var vars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Amount"] = amount,
        };
        return ResolveDescriptionWithStats(desc, vars);
    }

    /// <summary>
    /// Find the index of the closing '}' that matches an opening '{' at position openIdx,
    /// correctly handling nested brace pairs. Returns -1 if not found.
    /// </summary>
    internal static int FindMatchingCloseBrace(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>
    /// Parse a top-level SmartFormat block starting at the '{' at position start.
    /// Returns (varName, formatSpec, endIdx) where endIdx is the index of the matching '}'.
    /// formatSpec is everything after the first ':' (if any), with balanced braces.
    /// Returns null if the block can't be parsed.
    /// </summary>
    internal static (string varName, string? formatSpec, int endIdx)? ParseSmartBlock(string s, int start)
    {
        int end = FindMatchingCloseBrace(s, start);
        if (end < 0) return null;
        var inner = s.Substring(start + 1, end - start - 1);
        // Split on first ':'
        int colonIdx = -1;
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ':' && depth == 0) { colonIdx = i; break; }
        }
        if (colonIdx < 0)
            return (inner, null, end);
        return (inner[..colonIdx], inner[(colonIdx + 1)..], end);
    }

    /// <summary>
    /// Resolve all SmartFormat template patterns in a description using stat variable values.
    /// Handles: {Var}, {Var:diff()}, {Var:abs()}, {Var:plural:singular|plural},
    /// {Var:cond:condition?text|text}, {Var:energyIcons(N)}, {Var:starIcons()},
    /// {singleStarIcon}, and nested patterns like {Var:plural:word|{Var:diff()} words}.
    /// Strips BBCode and cleans up whitespace at the end.
    /// </summary>
    internal static string ResolveDescriptionWithStats(string desc, Dictionary<string, object?> vars)
    {
        // Multi-pass: resolve from innermost outward. Each pass resolves one layer
        // of templates. Continue until no more changes (handles nesting like
        // {Cards:plural:{IfUpgraded:show:X|Y}|{IfUpgraded:show:X|Y}}).
        for (int pass = 0; pass < 5; pass++)
        {
            var result = new System.Text.StringBuilder(desc.Length);
            int i = 0;
            bool changed = false;
            while (i < desc.Length)
            {
                if (desc[i] != '{') { result.Append(desc[i]); i++; continue; }

                var parsed = ParseSmartBlock(desc, i);
                if (parsed == null) { result.Append(desc[i]); i++; continue; }

                var (varName, fmt, endIdx) = parsed.Value;
                string? resolved = ResolveOneTemplate(varName, fmt, vars);
                if (resolved != null)
                {
                    // Fix "0Energy" concatenation: when a template like
                    // {energyPrefix:energyIcons(1)} resolves to "Energy" and directly
                    // follows a digit (no space), insert a space to get "0 Energy".
                    if (result.Length > 0 && char.IsDigit(result[result.Length - 1])
                        && resolved.Length > 0 && char.IsLetter(resolved[0]))
                    {
                        result.Append(' ');
                    }
                    result.Append(resolved);
                    changed = true;
                }
                else
                {
                    // Can't resolve — leave intact for TUI to handle
                    result.Append(desc, i, endIdx - i + 1);
                }
                i = endIdx + 1;
            }
            desc = result.ToString();
            if (!changed) break;
        }

        // Replace literal " X " with the first numeric var when X is a placeholder
        int? firstAmount = null;
        if (vars.TryGetValue("Amount", out var amtObj) && amtObj is int amt && amt != 0)
            firstAmount = amt;
        if (firstAmount != null)
            desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\bX\b", firstAmount.Value.ToString());

        // Strip BBCode
        desc = StripBBCode(desc);

        // Clean up whitespace (double spaces from removed templates, leading/trailing)
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"  +", " ");
        return desc.Trim();
    }

    /// <summary>
    /// Resolve a single template block {varName:formatSpec} given the available variables.
    /// Returns the resolved string, or null if the template can't be resolved.
    /// </summary>
    internal static string? ResolveOneTemplate(string varName, string? fmt, Dictionary<string, object?> vars)
    {
        // {singleStarIcon} — no format
        if (varName == "singleStarIcon" && fmt == null)
            return "Star";

        // {} — empty braces (value placeholder inside plural/cond forms)
        // This should have been handled by the plural/cond resolver, but if it appears
        // standalone, leave it as-is (will be cleaned up)
        if (varName == "" && fmt == null)
            return null;

        // No format spec — just bare {Var}
        if (fmt == null)
        {
            if (vars.TryGetValue(varName, out var val) && val != null)
                return val.ToString();
            return null; // unknown var — leave intact
        }

        // {Var:energyIcons(N)} — render N energy icons as text.
        // N=1: just the energy symbol → "Energy" (the preceding text supplies the number
        //       when relevant, e.g. "0{var:energyIcons(1)}" → "0 Energy").
        // N>1: the icon count IS the amount → "N Energy" (e.g. energyIcons(4) → "4 Energy").
        var energyMatch = System.Text.RegularExpressions.Regex.Match(fmt, @"^energyIcons\((\d+)\)$");
        if (energyMatch.Success)
        {
            int n = int.Parse(energyMatch.Groups[1].Value);
            return n <= 1 ? "Energy" : n + " Energy";
        }

        // {Var:energyIcons()} → "value Energy" (use var value if available)
        if (fmt == "energyIcons()")
        {
            if (vars.TryGetValue(varName, out var val) && val != null)
                return val + " Energy";
            return "Energy";
        }

        // {Var:starIcons()} → "value Star(s)" (use var value if available)
        if (fmt == "starIcons()")
        {
            if (vars.TryGetValue(varName, out var val) && val is int sv)
            {
                if (sv == 1) return "1 Star";
                return sv + " Stars";
            }
            return "Star";
        }

        // {Var:starIcons(N)} → "N Star(s)"
        var starMatch = System.Text.RegularExpressions.Regex.Match(fmt, @"^starIcons\((\d+)\)$");
        if (starMatch.Success)
        {
            var n = int.Parse(starMatch.Groups[1].Value);
            return n == 1 ? "1 Star" : n + " Stars";
        }

        // Get the variable's integer value for format operations
        int? intVal = null;
        if (vars.TryGetValue(varName, out var varObj) && varObj is int iv)
            intVal = iv;

        // {Var:diff()} → just the number
        if (fmt == "diff()")
        {
            if (intVal != null) return intVal.Value.ToString();
            return null; // can't resolve without value
        }

        // {Var:abs()} → absolute value
        if (fmt == "abs()")
        {
            if (intVal != null) return Math.Abs(intVal.Value).ToString();
            return null;
        }

        // {Var:percentLess()} / {Var:inverseDiff()} — display formatters, just show value
        if (fmt == "percentLess()" || fmt == "inverseDiff()")
        {
            if (intVal != null) return intVal.Value.ToString();
            return null;
        }

        // {Var:plural:singular|plural} — pick form based on value
        // The pipe inside plural content might itself contain nested braces that are already
        // resolved (from inner passes), so we split on the FIRST top-level '|' after "plural:"
        if (fmt.StartsWith("plural:"))
        {
            var content = fmt["plural:".Length..];
            // Split on first top-level '|'
            int pipeIdx = FindTopLevelPipe(content);
            if (pipeIdx < 0) return null;
            var singular = content[..pipeIdx];
            var pluralForm = content[(pipeIdx + 1)..];
            if (intVal != null)
            {
                var form = (Math.Abs(intVal.Value) == 1) ? singular : pluralForm;
                // Replace {} inside the chosen form with the value
                form = form.Replace("{}", intVal.Value.ToString());
                return form;
            }
            return null; // can't resolve plural without value
        }

        // {Var:cond:condition} — conditional text
        if (fmt.StartsWith("cond:"))
        {
            var content = fmt["cond:".Length..];
            if (intVal != null)
                return ResolveCondition(content, intVal.Value);
            return null;
        }

        // {Var.StringValue:cond:...} — string-presence conditional (used for OwnerName etc.)
        // These reference properties we can't resolve — leave intact
        if (fmt.Contains(":cond:") && varName.Contains('.'))
            return null;

        // {IfUpgraded:show:trueText|falseText} — can't resolve here (needs card context)
        if (varName == "IfUpgraded" || varName == "InCombat" || varName == "IsMultiplayer" ||
            varName == "OnPlayer")
            return null;

        // Unknown format — can't resolve, leave intact
        return null;
    }

    /// <summary>Find the index of the first top-level '|' in a string (not inside nested braces).</summary>
    internal static int FindTopLevelPipe(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == '|' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Resolve a SmartFormat cond expression like "&lt;0?negText|posText"
    /// or "==1?text1|>1?text2|".</summary>
    internal static string ResolveCondition(string condExpr, int value)
    {
        // Try simple two-branch: <0?negText|posText
        var m2 = System.Text.RegularExpressions.Regex.Match(condExpr, @"^<0\?(.+)$");
        if (m2.Success)
        {
            var rest = m2.Groups[1].Value;
            int pipe = FindTopLevelPipe(rest);
            if (pipe >= 0)
            {
                var negText = rest[..pipe];
                var posText = rest[(pipe + 1)..];
                return value < 0 ? negText : posText;
            }
        }

        // Multi-branch: ==1?text1|>1?text2|default
        // Parse branches separated by top-level '|'
        var branches = SplitTopLevel(condExpr, '|');
        foreach (var branch in branches)
        {
            var eqMatch = System.Text.RegularExpressions.Regex.Match(branch, @"^==(\d+)\?\s*(.*)$");
            if (eqMatch.Success)
            {
                if (value == int.Parse(eqMatch.Groups[1].Value))
                {
                    var form = eqMatch.Groups[2].Value;
                    return form.Replace("{}", value.ToString());
                }
                continue;
            }
            var gtMatch = System.Text.RegularExpressions.Regex.Match(branch, @"^>(\d+)\?\s*(.*)$");
            if (gtMatch.Success)
            {
                if (value > int.Parse(gtMatch.Groups[1].Value))
                {
                    var form = gtMatch.Groups[2].Value;
                    return form.Replace("{}", value.ToString());
                }
                continue;
            }
            var ltMatch = System.Text.RegularExpressions.Regex.Match(branch, @"^<(\d+)\?\s*(.*)$");
            if (ltMatch.Success)
            {
                if (value < int.Parse(ltMatch.Groups[1].Value))
                {
                    var form = ltMatch.Groups[2].Value;
                    return form.Replace("{}", value.ToString());
                }
                continue;
            }
            // Default/fallback branch — no condition prefix, just text (or empty string).
            // An empty default is valid: e.g. ">0? to ALL enemies|" means "nothing when ≤ 0".
            // String-presence conditionals (OwnerName.StringValue:cond:...) can't be evaluated
            // here, but if we've exhausted all branches, this is the fallback.
            if (!branch.Contains('?'))
            {
                return branch.Replace("{}", value.ToString());
            }
        }
        // No branch matched — return the raw value
        return value.ToString();
    }

    /// <summary>Split a string on a delimiter at the top level only (not inside nested braces).</summary>
    internal static List<string> SplitTopLevel(string s, char delimiter)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == delimiter && depth == 0)
            {
                result.Add(s[start..i]);
                start = i + 1;
            }
        }
        result.Add(s[start..]);
        return result;
    }

    /// <summary>
    /// Resolve {VarName:trueText|falseText} patterns using balanced-brace-aware parsing.
    /// Used for {InCombat:...} and {IsMultiplayer:...} context switches.
    /// </summary>
    internal static string ResolveContextBranch(string desc, string varName, bool condition)
    {
        var result = new System.Text.StringBuilder(desc.Length);
        int i = 0;
        while (i < desc.Length)
        {
            if (desc[i] != '{') { result.Append(desc[i]); i++; continue; }
            var parsed = ParseSmartBlock(desc, i);
            if (parsed == null || !parsed.Value.varName.Equals(varName, System.StringComparison.OrdinalIgnoreCase)
                || parsed.Value.formatSpec == null)
            {
                result.Append(desc[i]); i++; continue;
            }
            var (_, fmt, endIdx) = parsed.Value;
            // Split format on top-level '|'
            int pipeIdx = FindTopLevelPipe(fmt!);
            if (pipeIdx >= 0)
            {
                var trueText = fmt![..pipeIdx];
                var falseText = fmt![(pipeIdx + 1)..];
                result.Append(condition ? trueText : falseText);
            }
            else
            {
                // No pipe — show text if condition true, empty if false
                result.Append(condition ? fmt : "");
            }
            i = endIdx + 1;
        }
        return result.ToString();
    }

    /// <summary>
    /// Resolve {IfUpgraded:show:trueText|falseText} patterns using balanced-brace-aware parsing.
    /// </summary>
    internal static string ResolveIfUpgraded(string desc, bool isUpgraded)
    {
        var result = new System.Text.StringBuilder(desc.Length);
        int i = 0;
        while (i < desc.Length)
        {
            if (desc[i] != '{') { result.Append(desc[i]); i++; continue; }
            var parsed = ParseSmartBlock(desc, i);
            if (parsed == null || parsed.Value.varName != "IfUpgraded" || parsed.Value.formatSpec == null)
            {
                result.Append(desc[i]); i++; continue;
            }
            var (_, fmt, endIdx) = parsed.Value;
            // Format is "show:trueText|falseText"
            var content = fmt!;
            if (content.StartsWith("show:"))
                content = content["show:".Length..];
            int pipeIdx = FindTopLevelPipe(content);
            if (pipeIdx >= 0)
            {
                var trueText = content[..pipeIdx];
                var falseText = content[(pipeIdx + 1)..];
                result.Append(isUpgraded ? trueText : falseText);
            }
            else
            {
                result.Append(isUpgraded ? content : "");
            }
            i = endIdx + 1;
        }
        return result.ToString();
    }

    /// <summary>Resolve a full loc key like "TABLE.KEY.SUB" by searching all tables.</summary>
    public string BilingualFromKey(string locKey)
    {
        if (Lang == "zh")
        {
            foreach (var tableName in _zhs.Keys)
            {
                var zh = _zhs.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
                if (zh != null) return zh;
            }
        }
        foreach (var tableName in _eng.Keys)
        {
            var en = _eng.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
            if (en != null) return en;
        }
        return locKey;
    }

    /// <summary>
    /// Resolve literal localization keys embedded in text strings.
    /// Matches patterns like "CLUMSY.title", "SHARP.title", "BYRDONIS_EGG.title"
    /// and replaces them with the resolved localized text, falling back to
    /// HumanizeId (UPPER_SNAKE_CASE -> Title Case) if the key is not found.
    /// </summary>
    public string ResolveInlineLocKeys(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"\b([A-Z][A-Z0-9_]+)\.(title|name|description)\b",
            m =>
            {
                var rawId = m.Groups[1].Value;
                var suffix = m.Groups[2].Value;
                var locKey = rawId + "." + suffix;
                // Try to find in loc tables
                var resolved = BilingualFromKey(locKey);
                if (resolved != locKey)
                    return StripBBCode(resolved);
                // Try common table patterns: cards, relics, potions, powers, enchantments
                foreach (var table in new[] { "cards", "relics", "potions", "powers", "enchantments", "statuses" })
                {
                    var r = Bilingual(table, locKey);
                    if (r != locKey)
                        return r;
                }
                // Fallback: HumanizeId (strip _POWER etc., then Title Case)
                var clean = rawId;
                foreach (var s in new[] { "_POWER", "_RELIC", "_POTION", "_CARD" })
                    if (clean.EndsWith(s)) { clean = clean[..^s.Length]; break; }
                return HumanizeId(clean);
            });
    }

    public bool IsLoaded => _eng.Count > 0;
}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public class RunSimulator
{
    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private int _consecutiveForceStarts;
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private List<MegaCrit.Sts2.Core.Rewards.PotionReward>? _pendingPotionRewards;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;

    // Track whether a card has already been removed during this shop visit (only one removal per visit)
    private bool _shopCardRemoved;

    /// <summary>
    /// Combat turn counter — incremented every end_turn, reset when entering a new room.
    /// Used to force-end combats that exceed MAX_COMBAT_TURNS (prevents OOM in god mode).
    /// </summary>
    private int _combatTurnCount;

    /// <summary>
    /// Maximum combat turns before force-ending. God-mode combats that exceed this
    /// are force-won (player declared winner) to prevent OOM from growing action queues,
    /// power lists, and sync context callback accumulation.
    /// </summary>
    private const int MAX_COMBAT_TURNS = 50;

    /// <summary>
    /// Global action timeout: no single ExecuteAction call should ever take longer than this.
    /// If exceeded, the engine force-kills the player and returns a game_over to prevent hangs.
    /// Set to 20s to accommodate boss fights which legitimately take longer.
    /// </summary>
    private const int GLOBAL_ACTION_TIMEOUT_MS = 20_000;

    /// <summary>God mode: restore player HP to max after every action. For testing.</summary>
    public bool GodMode { get; set; }

    /// <summary>
    /// Run a Task synchronously with a wall-clock timeout. If the task does not complete
    /// within <paramref name="timeoutMs"/>, throws a TimeoutException instead of hanging.
    /// This replaces bare .GetAwaiter().GetResult() calls which can block indefinitely.
    /// </summary>
    private static void RunWithTimeout(Task task, int timeoutMs = 8000, string context = "async call")
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult(); // propagate any exception
            return;
        }
        if (task.Wait(timeoutMs))
        {
            task.GetAwaiter().GetResult(); // propagate any exception
            return;
        }
        throw new TimeoutException($"{context} did not complete within {timeoutMs}ms");
    }

    /// <summary>
    /// Run a Task&lt;T&gt; synchronously with a wall-clock timeout.
    /// </summary>
    private static T RunWithTimeout<T>(Task<T> task, int timeoutMs = 8000, string context = "async call")
    {
        if (task.IsCompleted)
            return task.GetAwaiter().GetResult();
        if (task.Wait(timeoutMs))
            return task.GetAwaiter().GetResult();
        throw new TimeoutException($"{context} did not complete within {timeoutMs}ms");
    }

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null, string lang = "en")
    {
        try
        {
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed ?? "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();

            // Finalize starting relics
            RunWithTimeout(RunManager.Instance.FinalizeStartingRelics(), 5000, "FinalizeStartingRelics");
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunWithTimeout(RunManager.Instance.EnterAct(0, doTransition: false), 5000, "EnterAct");
            Log("Entered Act 0");

            // Register card selector for cards that need player choice
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null) list.Add(model.ToMutable());
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            if (model != null) slots[idx] = model;
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");
            _shopCardRemoved = false;
            _combatTurnCount = 0;

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            // Wait for any pending relic session before entering a new room
            WaitForRelicPickingComplete();

            RunWithTimeout(RunManager.Instance.EnterRoom(room), 5000, "EnterRoom");
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───

    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            // God mode: restore HP to 9999 so the player survives any hit.
            // OOM is prevented by the MAX_COMBAT_TURNS limit (force-win after 50 turns)
            // and periodic queue cleanup, not by limiting HP.
            if (GodMode && player.Creature != null)
            {
                try
                {
                    SetField(player.Creature, "_currentHp", 9999);
                    SetField(player.Creature, "_maxHp", 9999);
                }
                catch { }

                // Force-win long combats to prevent OOM from unbounded action/callback growth
                if (_combatTurnCount > MAX_COMBAT_TURNS && CombatManager.Instance.IsInProgress)
                {
                    Log($"God mode: combat exceeded {MAX_COMBAT_TURNS} turns ({_combatTurnCount}). Force-winning to prevent OOM.");
                    // Kill all enemies to end combat as a win (rather than killing the player)
                    try
                    {
                        var combatState = CombatManager.Instance.DebugOnlyGetState();
                        if (combatState != null)
                        {
                            foreach (var enemy in combatState.Enemies.Where(e => e != null && e.IsAlive))
                            {
                                var hpField = enemy.GetType().GetField("_currentHp", BindingFlags.NonPublic | BindingFlags.Instance)
                                           ?? enemy.GetType().GetField("<CurrentHp>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                                if (hpField != null)
                                    hpField.SetValue(enemy, 0);
                                else
                                {
                                    var hpProp = enemy.GetType().GetProperty("CurrentHp", BindingFlags.Public | BindingFlags.Instance);
                                    if (hpProp != null && hpProp.CanWrite)
                                        hpProp.SetValue(enemy, 0);
                                }
                            }
                        }
                        _syncCtx.Pump();
                    }
                    catch (Exception ex) { Log($"Force-win enemy kill failed: {ex.Message}"); }

                    // Clear accumulated callbacks to free memory
                    _syncCtx.ClearQueue();
                    _combatTurnCount = 0;

                    // If enemies didn't die cleanly, fall back to force-killing player
                    if (CombatManager.Instance.IsInProgress)
                    {
                        Log("Force-win: enemies still alive, falling back to force-kill player");
                        ForceKillPlayer(player);
                    }
                    return DetectDecisionPoint();
                }

                // Periodically clear accumulated sync context callbacks during long combats
                // to prevent gradual memory growth from abandoned ThreadPool work items
                if (_combatTurnCount > 0 && _combatTurnCount % 10 == 0)
                {
                    var queueSize = _syncCtx.QueueCount;
                    if (queueSize > 500)
                    {
                        Log($"God mode: clearing {queueSize} accumulated sync context callbacks (turn {_combatTurnCount})");
                        _syncCtx.ClearQueue();
                    }
                }
            }

            // GLOBAL WATCHDOG: Run the action dispatch with a hard wall-clock timeout.
            // This is the last line of defense against hangs — if any action (including
            // all its Pump/WaitForActionExecutor/GetAwaiter calls) exceeds the timeout,
            // we force-kill the player and return an error rather than hanging forever.
            var actionTimer = Stopwatch.StartNew();
            Dictionary<string, object?>? result = null;
            Exception? actionException = null;
            var actionDone = new ManualResetEventSlim(false);

            var actionThread = new Thread(() =>
            {
                // Ensure this thread uses our sync context so async continuations
                // from game code are posted to our queue (not the default ThreadPool).
                SynchronizationContext.SetSynchronizationContext(_syncCtx);
                try
                {
                    result = ExecuteActionInner(player, action, args);
                }
                catch (Exception ex)
                {
                    actionException = ex;
                }
                finally
                {
                    actionDone.Set();
                }
            });
            actionThread.IsBackground = true;
            actionThread.Start();

            if (actionDone.Wait(GLOBAL_ACTION_TIMEOUT_MS))
            {
                // Action completed within timeout
                if (actionException != null)
                    return ErrorWithTrace($"Action '{action}' failed", actionException);
                return result ?? Error($"Action '{action}' returned null");
            }

            // GLOBAL TIMEOUT HIT — the action is hung. Force-recover.
            Log($"GLOBAL ACTION TIMEOUT: '{action}' did not complete within {GLOBAL_ACTION_TIMEOUT_MS}ms ({actionTimer.ElapsedMilliseconds}ms elapsed)");

            // Interrupt the stuck action thread. Thread.Interrupt() will cause any
            // blocking call (Thread.Sleep, WaitHandle.Wait, etc.) to throw
            // ThreadInterruptedException, which may unstick the thread.
            try { actionThread.Interrupt(); }
            catch { }

            // Clear accumulated sync context callbacks that may be contributing to the hang
            _syncCtx.ClearQueue();

            // Force-kill the player to end any stuck combat
            if (CombatManager.Instance.IsInProgress && player.Creature != null && !player.Creature.IsDead)
            {
                Log("Global timeout: force-killing player to end stuck combat");
                ForceKillPlayer(player);
            }

            // Return a proper game_over so the agent sees a clean loss instead of a
            // bridge EOF/timeout. This is critical — without it the agent retries and
            // the 15-25% timeout rate stays high.
            Log("Global timeout: returning game_over with timeout_recovery=true");
            return TimeoutGameOverState(action);
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    /// <summary>
    /// Inner action dispatch — called from ExecuteAction within a global timeout wrapper.
    /// </summary>
    private Dictionary<string, object?> ExecuteActionInner(Player player, string action, Dictionary<string, object?>? args)
    {
        switch (action)
        {
            case "select_map_node":
                return DoMapSelect(player, args);
            case "play_card":
                return DoPlayCard(player, args);
            case "end_turn":
                return DoEndTurn(player);
            case "choose_option":
                return DoChooseOption(player, args);
            case "select_card_reward":
                return DoSelectCardReward(player, args);
            case "skip_card_reward":
                return DoSkipCardReward(player);
            case "buy_card":
                return DoBuyCard(player, args);
            case "buy_relic":
                return DoBuyRelic(player, args);
            case "buy_potion":
                return DoBuyPotion(player, args);
            case "remove_card":
                return DoRemoveCard(player);
            case "select_bundle":
                return DoSelectBundle(player, args);
            case "select_cards":
                return DoSelectCards(player, args);
            case "skip_select":
                return DoSkipSelect(player);
            case "use_potion":
                return DoUsePotion(player, args);
            case "discard_potion":
                return DoDiscardPotion(player, args);
            case "collect_potion_reward":
                return DoCollectPotionReward(player, args);
            case "discard_potion_for_reward":
                return DoDiscardPotionForReward(player, args);
            case "skip_potion_reward":
                return DoSkipPotionReward(player, args);
            case "leave_room":
                return DoLeaveRoom(player);
            case "proceed":
                return DoProceed(player);
            default:
                return Error($"Unknown action: {action}");
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _pendingPotionRewards = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingRewards = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;
        _shopCardRemoved = false;
        _combatTurnCount = 0;

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForRelicPickingComplete();
        WaitForActionExecutor();
        _syncCtx.Pump();

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        // Retry once if relic picking race condition occurs; on persistent failure, return error.
        bool enterSucceeded = false;
        try
        {
            RunWithTimeout(RunManager.Instance.EnterMapCoord(coord), 5000, "EnterMapCoord");
            _syncCtx.Pump();
            WaitForActionExecutor();
            enterSucceeded = true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
        {
            Log($"EnterMapCoord relic session conflict: {ex.Message} — retrying after pump");
            // Pump aggressively then retry once
            ForceResetRelicPickingState();
            try
            {
                RunWithTimeout(RunManager.Instance.EnterMapCoord(coord), 5000, "EnterMapCoord retry");
                _syncCtx.Pump();
                WaitForActionExecutor();
                enterSucceeded = true;
            }
            catch (Exception retryEx)
            {
                Log($"EnterMapCoord retry also failed: {retryEx.GetType().Name}: {retryEx.Message}");
                // Don't crash — return error so the caller can try a different action
                return ErrorWithTrace("EnterMapCoord failed (relic session conflict)", retryEx);
            }
        }

        if (!enterSucceeded)
            return Error("EnterMapCoord failed");

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        // Determine target based on card's TargetType first
        // Self/None/All cards: target = null (game handles internally)
        // AnyEnemy cards: use target_index or auto-pick first alive enemy
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }
            // Fallback: auto-target first alive enemy
            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                target = state?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);

        // Hard timeout for play_card: WaitForActionExecutor can hang if a callback
        // in the sync context blocks. Use a reduced timeout and a Stopwatch-based
        // outer guard.
        var playCardTimer = Stopwatch.StartNew();
        WaitForActionExecutor(timeoutMs: 2000);

        // If WaitForActionExecutor timed out but combat ended or player died, that's fine
        if (playCardTimer.ElapsedMilliseconds > 2000)
        {
            Log($"DoPlayCard: WaitForActionExecutor exceeded 2s ({playCardTimer.ElapsedMilliseconds}ms). Checking state...");
            // Extra pump with timeout to try to unstick
            _syncCtx.PumpWithTimeout(perCallbackTimeoutMs: 1000);
        }

        // If combat ended (e.g., killing a boss minion ended the fight), skip
        // the hand-unchanged check and go straight to decision point detection.
        if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
        {
            Log("DoPlayCard: combat ended or player died during card play");
            return DetectDecisionPoint();
        }

        // Hard timeout: if play_card has taken more than 10s total, force-recover
        if (playCardTimer.ElapsedMilliseconds > 10_000)
        {
            Log($"DoPlayCard HARD TIMEOUT after {playCardTimer.ElapsedMilliseconds}ms");
            // If combat is somehow stuck, don't hang — just return current state
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectDecisionPoint();
            }
            // Combat still running — return what we have (the card may or may not have played)
        }

        // Check if card play had no effect (hand unchanged, same card still at same index)
        // Guard against null pcs/Hand in case combat state was torn down
        var handAfter = pcs?.Hand?.Cards;
        if (handAfter != null && handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        // HARD wall-clock timeout: the entire DoEndTurn must complete within 25s.
        // This catches cases where _syncCtx.Pump() itself blocks on a deadlocked
        // callback — the inner Stopwatch-based timeouts can't fire in that scenario
        // because they only check between pump iterations. Must be > GLOBAL_ACTION_TIMEOUT_MS
        // so the global watchdog fires first and can return a clean game_over.
        var hardTimer = Stopwatch.StartNew();
        const int HARD_TIMEOUT_MS = 25_000;

        if (!CombatManager.Instance.IsPlayPhase)
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsPlayPhase)
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(50);
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsPlayPhase)
                {
                    // The game is stuck between phases. The enemy turn may have
                    // completed but the transition back to play phase failed.
                    // Track consecutive early-path force-starts to detect unrecoverable
                    // loops where ForceStartNextTurn never recovers play phase.
                    _consecutiveForceStarts++;
                    Log($"DoEndTurn: IsPlayPhase false after 50ms pump (consecutiveForceStarts={_consecutiveForceStarts}). Trying SuppressYield recovery.");

                    // Kill-switch: if the early recovery path has been hit too many times
                    // in a row, the combat is unrecoverable. Kill the player.
                    if (_consecutiveForceStarts > 3 && CombatManager.Instance.IsInProgress)
                    {
                        Log($"DoEndTurn early path: combat unrecoverable after {_consecutiveForceStarts} consecutive force-starts. Killing player.");
                        ForceKillPlayer(player);
                        return DetectDecisionPoint();
                    }

                    YieldPatches.SuppressYield = true;
                    try
                    {
                        // Quick SuppressYield pump (up to ~200ms with Stopwatch)
                        var earlyPumpTimer = Stopwatch.StartNew();
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            if (CombatManager.Instance.IsPlayPhase) break;
                            if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                            if (earlyPumpTimer.ElapsedMilliseconds > 200) break;
                            Thread.Sleep(0); // yield timeslice without 15ms sleep penalty
                        }
                        if (!CombatManager.Instance.IsPlayPhase && CombatManager.Instance.IsInProgress
                            && (player.Creature == null || !player.Creature.IsDead))
                        {
                            // SuppressYield pump didn't help — force-start next turn
                            Log("DoEndTurn: SuppressYield pump failed. Calling ForceStartNextTurn.");
                            var earlyRound = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
                            ForceStartNextTurn(player);
                            _syncCtx.Pump();
                            // Brief pump for StartTurn to complete
                            var startPumpTimer = Stopwatch.StartNew();
                            for (int i = 0; i < 500; i++)
                            {
                                _syncCtx.Pump();
                                if (CombatManager.Instance.IsPlayPhase) break;
                                if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead)) break;
                                if (startPumpTimer.ElapsedMilliseconds > 1000) break;
                                Thread.Sleep(0);
                            }
                            // Verify round advanced
                            var earlyRoundAfter = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
                            if (earlyRoundAfter <= earlyRound && CombatManager.Instance.IsInProgress)
                                ForceIncrementRound(earlyRound);
                            if (CombatManager.Instance.IsPlayPhase)
                            {
                                Log($"DoEndTurn: ForceStartNextTurn recovered play phase (round={earlyRoundAfter})");
                                _consecutiveForceStarts = 0; // recovered — reset counter
                            }
                            else
                                Log($"DoEndTurn: ForceStartNextTurn did not recover play phase");
                        }
                        else if (CombatManager.Instance.IsPlayPhase)
                        {
                            _consecutiveForceStarts = 0; // recovered — reset counter
                        }
                    }
                    finally
                    {
                        YieldPatches.SuppressYield = false;
                    }
                    return DetectDecisionPoint();
                }
            }
        }

        // Track combat duration for OOM prevention
        _combatTurnCount++;

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor(timeoutMs: 2000);

        var roundBefore = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
        Log($"Ending turn (round={roundBefore}, combatTurn={_combatTurnCount})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks where AfterAllPlayersReadyToBeginEnemyTurn starts with
        // Task.Yield() — if SuppressYield is false, the continuation gets posted to the
        // sync context but never drained, causing the turn to never advance.
        //
        // CRITICAL: SuppressYield must stay true throughout the entire turn transition,
        // including the enemy turn and start-of-next-turn processing. It is only safe to
        // disable once IsPlayPhase is true again (or combat ended / player died).
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();

            // Pump aggressively until the turn transition completes.
            // SuppressYield stays true the entire time so async continuations
            // (Task.Yield in AfterAllPlayersReadyToBeginEnemyTurn, SwitchFromPlayerToEnemySide,
            // StartTurn, etc.) all execute inline synchronously.
            //
            // Wall-clock timeout (8s) prevents the end-turn hang variant where
            // the async chain deadlocks and the pump loop never exits.
            bool resolved = false;
            int emptyPumps = 0;
            var pumpTimer = Stopwatch.StartNew();
            for (int i = 0; i < 5000; i++)
            {
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) { resolved = true; break; }
                if (_combatEnded.IsSet) { resolved = true; break; }
                if (CombatManager.Instance.IsPlayPhase) { resolved = true; break; }

                // Wall-clock timeout: if we've been pumping for over 8 seconds,
                // the async chain is deadlocked. Break out and force-recover.
                if (pumpTimer.ElapsedMilliseconds > 8_000)
                {
                    Log($"EndTurn pump loop wall-clock timeout after {pumpTimer.ElapsedMilliseconds}ms");
                    break;
                }

                // HARD timeout check — if the entire DoEndTurn has exceeded 15s, bail immediately
                if (hardTimer.ElapsedMilliseconds > HARD_TIMEOUT_MS)
                {
                    Log($"DoEndTurn HARD TIMEOUT after {hardTimer.ElapsedMilliseconds}ms in main pump loop");
                    break;
                }

                // Early exit: if the sync context queue is drained and the executor isn't
                // running, the async chain has stalled (likely NRE). No point pumping further.
                try
                {
                    if (!RunManager.Instance.ActionExecutor.IsRunning)
                    {
                        emptyPumps++;
                        if (emptyPumps > 50) break;  // Give it 50 iterations to be safe
                    }
                    else
                    {
                        emptyPumps = 0;
                    }
                }
                catch { }

                Thread.Sleep(0); // yield timeslice without 15ms sleep penalty
            }

            if (!resolved)
            {
                Log("EndTurn did not resolve after pumping — checking executor");
                // Wait for the action executor to finish processing (with timeout)
                WaitForActionExecutor(timeoutMs: 2000);
                _syncCtx.Pump();

                // One more check
                if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    resolved = true;
            }

            if (!resolved)
            {
                _consecutiveForceStarts++;
                Log($"EndTurn not resolved after pumping (consecutiveForceStarts={_consecutiveForceStarts}). " +
                    $"IsPlayPhase={CombatManager.Instance.IsPlayPhase}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");

                // HARD timeout — if we've already burned most of our budget, skip ForceStart
                // and go straight to killing the player to end combat
                bool hardTimeoutKill = hardTimer.ElapsedMilliseconds > HARD_TIMEOUT_MS - 2000;

                // If we've needed ForceStartNextTurn too many times, the combat is
                // unrecoverably broken — end_turn never completes naturally. Kill the
                // player to end the fight rather than looping for hundreds of steps.
                if ((hardTimeoutKill || _consecutiveForceStarts > 5) && CombatManager.Instance.IsInProgress)
                {
                    Log($"Combat unrecoverable (consecutiveForceStarts={_consecutiveForceStarts}, hardTimeout={hardTimeoutKill}). Killing player.");
                    ForceKillPlayer(player);
                    resolved = true;
                }
                else
                {
                    // The turn transition failed (likely NRE in async chain).
                    // Force-start the next turn by calling StartTurn directly via reflection.
                    try
                    {
                        ForceStartNextTurn(player);
                        _syncCtx.Pump();

                        // Pump until play phase resumes (with wall-clock timeout)
                        var forceTimer = Stopwatch.StartNew();
                        for (int i = 0; i < 5000; i++)
                        {
                            _syncCtx.Pump();
                            if (_turnStarted.IsSet || _combatEnded.IsSet) { resolved = true; break; }
                            if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) { resolved = true; break; }
                            if (CombatManager.Instance.IsPlayPhase) { resolved = true; break; }
                            if (forceTimer.ElapsedMilliseconds > 3_000)
                            {
                                Log($"ForceStartNextTurn pump loop wall-clock timeout after {forceTimer.ElapsedMilliseconds}ms");
                                // Even though pumping timed out, if IsPlayPhase is true now, we're OK
                                if (CombatManager.Instance.IsPlayPhase) resolved = true;
                                break;
                            }
                            // Check hard timeout
                            if (hardTimer.ElapsedMilliseconds > HARD_TIMEOUT_MS)
                            {
                                Log($"DoEndTurn HARD TIMEOUT after {hardTimer.ElapsedMilliseconds}ms in ForceStart pump loop");
                                break;
                            }
                            Thread.Sleep(0);
                        }

                        if (!resolved && hardTimer.ElapsedMilliseconds > HARD_TIMEOUT_MS - 1000)
                        {
                            // Hard timeout imminent — kill player to unblock
                            Log($"DoEndTurn: ForceStart didn't resolve and hard timeout imminent. Killing player.");
                            ForceKillPlayer(player);
                            resolved = true;
                        }
                        else if (resolved)
                            Log("Force-StartTurn succeeded");
                        else
                            Log("Force-StartTurn did not resolve");
                    }
                    catch (Exception ex)
                    {
                        Log($"Force-StartTurn failed: {ex.Message}");
                    }
                }
            }
            else
            {
                // End turn resolved normally — reset the force-start counter
                _consecutiveForceStarts = 0;
            }

            // ROUND-STUCK GUARD: Verify the round actually advanced after end_turn.
            // If ForceStartNextTurn fired but the round didn't increment (e.g., because
            // the RoundNumber property wasn't writable via reflection), force it now.
            if (resolved && CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPlayPhase)
            {
                var roundAfter = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
                if (roundAfter <= roundBefore)
                {
                    Log($"Round did not advance (before={roundBefore}, after={roundAfter}). Force-incrementing.");
                    ForceIncrementRound(roundBefore);
                    var roundFixed = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
                    Log($"Round after force-increment: {roundFixed}");
                }
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        Log($"DoEndTurn completed in {hardTimer.ElapsedMilliseconds}ms");
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        try
        {
            RunWithTimeout(
                MegaCrit.Sts2.Core.Commands.CardPileCmd
                    .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck),
                5000, "CardPileCmd.Add");
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex) { Log($"Add card to deck: {ex.Message}"); }

        _pendingCardReward = null;
        // Check if more rewards pending
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        // If no cards left but potion rewards remain, also skip those
        // to prevent stuck state (empty cards[] card_reward that can't advance)
        if (_pendingCardReward == null && _pendingPotionRewards != null && _pendingPotionRewards.Count > 0)
        {
            Log("Skipping remaining potion rewards (no card reward pending)");
            _pendingPotionRewards = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.Inventory.CharacterCardEntries
            .Concat(merchantRoom.Inventory.ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            RunWithTimeout(entry.OnTryPurchaseWrapper(merchantRoom.Inventory), 5000, "OnTryPurchaseWrapper");
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = merchantRoom.Inventory.RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            RunWithTimeout(entry.OnTryPurchaseWrapper(merchantRoom.Inventory), 5000, "OnTryPurchaseWrapper");
            _syncCtx.Pump();
            Log($"Bought relic: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy relic failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.Inventory.PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            RunWithTimeout(entry.OnTryPurchaseWrapper(merchantRoom.Inventory), 5000, "OnTryPurchaseWrapper");
            _syncCtx.Pump();
            Log($"Bought potion: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            // Potion purchase sometimes NullRefs in headless (missing potion slot UI)
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.Inventory.CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (player.Gold < removal.Cost) return Error("Not enough gold");
        if (_shopCardRemoved) return Error("Already removed a card this shop visit");

        try
        {
            // Run on background thread so card selection can pause (same pattern as event options)
            var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.Inventory));
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (_cardSelector.HasPending)
            {
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted)
            {
                // Pump sync context while waiting to prevent deadlock
                var waitTimer = Stopwatch.StartNew();
                while (!task.IsCompleted && waitTimer.ElapsedMilliseconds < 2000)
                {
                    _syncCtx.Pump();
                    Thread.Sleep(5);
                }
            }
            _syncCtx.Pump();
            Log($"Removed card for {removal.Cost}g");
            _shopCardRemoved = true;
        }
        catch (Exception ex) { return Error($"Remove card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        tcs.TrySetResult(selected);

        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        var indices = indicesStr.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .ToArray();

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        _syncCtx.Pump();
        WaitForActionExecutor();

        // Extra wait for rest-site SMITH: the background ChooseLocalOption task
        // needs time to complete the upgrade after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            // Force to map after SMITH completes (same pattern as HEAL)
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
            _shopCardRemoved = true;
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        // Determine target based on potion's TargetType first, then fall back to target_index
        Creature? target = null;
        var potionTargetType = potion.TargetType;

        // Self-targeting potions (Flex, Fortifier, etc.) ALWAYS target the player
        // regardless of any target_index the caller provides
        if (potionTargetType == TargetType.Self || potionTargetType == TargetType.TargetedNoCreature)
        {
            target = player.Creature;
        }
        else if (potionTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided, otherwise pick first alive enemy
            if (args.TryGetValue("target_index", out var tObj) && tObj != null)
            {
                var targetIdx = Convert.ToInt32(tObj);
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState != null)
                {
                    var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIdx >= 0 && targetIdx < enemies.Count)
                        target = enemies[targetIdx];
                }
            }
            if (target == null && CombatManager.Instance.IsInProgress)
            {
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                target = combatState?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();
            // Verify potion was consumed
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                // Potion wasn't consumed — manually discard it
                Log("Potion not consumed by action, manually discarding");
                RunWithTimeout(MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion), 3000, "PotionCmd.Discard");
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            // Try manual discard as fallback
            try { RunWithTimeout(MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion), 3000, "PotionCmd.Discard fallback"); } catch { }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        RunWithTimeout(MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion), 3000, "PotionCmd.Discard");
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoCollectPotionReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingPotionRewards == null || _pendingPotionRewards.Count == 0)
            return Error("No pending potion rewards");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("collect_potion_reward requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        if (idx < 0 || idx >= _pendingPotionRewards.Count)
            return Error($"Invalid potion reward index {idx}, {_pendingPotionRewards.Count} available");

        // Check if potion belt has space — if full, auto-skip this reward
        // to advance state. Returning an error here caused agents to loop
        // infinitely: error → proceed → same card_reward → collect → error …
        var potionsList = player.Potions?.ToList() ?? new();
        var hasSpace = potionsList.Any(p => p == null);
        if (!hasSpace)
        {
            Log($"Potion belt full, auto-skipping potion reward at index {idx}");
            _pendingPotionRewards.RemoveAt(idx);
            if (_pendingPotionRewards.Count == 0) _pendingPotionRewards = null;
            return DetectDecisionPoint();
        }

        var potionReward = _pendingPotionRewards[idx];
        try
        {
            RunWithTimeout(potionReward.OnSelectWrapper(), 5000, "PotionReward.OnSelectWrapper");
            _syncCtx.Pump();
            Log($"Collected potion reward: {potionReward.Potion?.Id.Entry}");
        }
        catch (Exception ex) { Log($"Collect potion reward: {ex.Message}"); }

        _pendingPotionRewards.RemoveAt(idx);
        if (_pendingPotionRewards.Count == 0) _pendingPotionRewards = null;

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotionForReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingPotionRewards == null || _pendingPotionRewards.Count == 0)
            return Error("No pending potion rewards");
        if (args == null || !args.ContainsKey("discard_index") || !args.ContainsKey("potion_index"))
            return Error("discard_potion_for_reward requires 'discard_index' (belt slot to discard) and 'potion_index' (reward to collect)");

        var discardIdx = Convert.ToInt32(args["discard_index"]);
        var rewardIdx = Convert.ToInt32(args["potion_index"]);

        if (rewardIdx < 0 || rewardIdx >= _pendingPotionRewards.Count)
            return Error($"Invalid potion reward index {rewardIdx}, {_pendingPotionRewards.Count} available");

        // Discard the existing potion from belt
        var potionsList = player.Potions?.ToList() ?? new();
        if (discardIdx < 0 || discardIdx >= potionsList.Count)
            return Error($"Invalid belt potion index {discardIdx}");
        var existingPotion = potionsList[discardIdx];
        if (existingPotion == null)
            return Error($"No potion at belt index {discardIdx}");

        try
        {
            RunWithTimeout(MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(existingPotion), 3000, "PotionCmd.Discard belt");
            _syncCtx.Pump();
            Log($"Discarded belt potion: {existingPotion.Id.Entry} at slot {discardIdx}");
        }
        catch (Exception ex) { Log($"Discard belt potion: {ex.Message}"); }

        // Now collect the reward potion
        var potionReward = _pendingPotionRewards[rewardIdx];
        try
        {
            RunWithTimeout(potionReward.OnSelectWrapper(), 5000, "PotionReward.OnSelectWrapper");
            _syncCtx.Pump();
            Log($"Collected potion reward: {potionReward.Potion?.Id.Entry}");
        }
        catch (Exception ex) { Log($"Collect potion reward after discard: {ex.Message}"); }

        _pendingPotionRewards.RemoveAt(rewardIdx);
        if (_pendingPotionRewards.Count == 0) _pendingPotionRewards = null;

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipPotionReward(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingPotionRewards == null || _pendingPotionRewards.Count == 0)
            return Error("No pending potion rewards");

        if (args != null && args.ContainsKey("potion_index"))
        {
            // Skip a specific potion reward
            var idx = Convert.ToInt32(args["potion_index"]);
            if (idx < 0 || idx >= _pendingPotionRewards.Count)
                return Error($"Invalid potion reward index {idx}");
            Log($"Skipping potion reward: {_pendingPotionRewards[idx].Potion?.Id.Entry}");
            _pendingPotionRewards.RemoveAt(idx);
        }
        else
        {
            // Skip all remaining potion rewards
            Log("Skipping all remaining potion rewards");
            _pendingPotionRewards.Clear();
        }

        if (_pendingPotionRewards.Count == 0) _pendingPotionRewards = null;
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                // Run on background thread so Smith card selection can pause
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    Thread.Sleep(10);
                }
                if (_cardSelector.HasPending)
                {
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                if (!task.IsCompleted)
                {
                    // Pump sync context while waiting to prevent deadlock
                    var waitTimer = Stopwatch.StartNew();
                    while (!task.IsCompleted && waitTimer.ElapsedMilliseconds < 2000)
                    {
                        _syncCtx.Pump();
                        Thread.Sleep(5);
                    }
                }
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                // Give the action time to complete (heal HP, dig for relic, etc.)
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(200);
                _syncCtx.Pump();
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        // For events — use EventSynchronizer
        // Run Chosen() on a background thread so card selections can pause
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        // Run on thread pool so GetSelectedCards/GetSelectedCardReward can block
                        var task = Task.Run(() => options[optionIndex].Chosen());
                        for (int i = 0; i < 100; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null) break;
                            if (task.IsCompleted) break;
                            Thread.Sleep(10);
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                        {
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        if (!task.IsCompleted)
                        {
                            // Pump sync context while waiting to prevent deadlock
                            var waitTimer = Stopwatch.StartNew();
                            while (!task.IsCompleted && waitTimer.ElapsedMilliseconds < 2000)
                            {
                                _syncCtx.Pump();
                                Thread.Sleep(5);
                            }
                        }
                        _syncCtx.Pump();
                    }
                    catch (Exception ex) { Log($"Event choose: {ex.Message}"); }
                }

                var optCountAfter = localEvent.CurrentOptions?.Count ?? 0;
                if (!localEvent.IsFinished && optCountAfter == optCountBefore && optCountAfter > 0)
                {
                    Log($"Event {localEvent.GetType().Name} didn't advance, force-finishing");
                    ForceToMap();
                }
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        try { RunWithTimeout(RunManager.Instance.ProceedFromTerminalRewardsScreen(), 5000, "ProceedFromTerminalRewardsScreen"); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunWithTimeout(RunManager.Instance.EnterRoom(new MapRoom()), 5000, "EnterRoom(MapRoom)");
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Wait for any pending relic session before proceeding
        WaitForRelicPickingComplete();

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                var actTimer = Stopwatch.StartNew();
                try
                {
                    YieldPatches.SuppressYield = true;
                    var task = RunManager.Instance.EnterNextAct();
                    while (!task.IsCompleted && actTimer.ElapsedMilliseconds < 10_000)
                    {
                        _syncCtx.Pump();
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;
                    if (task.IsCompleted)
                    {
                        _syncCtx.Pump();
                        WaitForActionExecutor();
                    }
                    else
                    {
                        Log($"DoProceed: EnterNextAct timed out after {actTimer.ElapsedMilliseconds}ms");
                        try { _syncCtx.PumpWithTimeout(perCallbackTimeoutMs: 500); } catch { }
                    }
                }
                catch (Exception ex) { Log($"DoProceed EnterNextAct: {ex.Message}"); }
                finally { YieldPatches.SuppressYield = false; }
                return DetectDecisionPoint();
            }
        }

        RunWithTimeout(RunManager.Instance.ProceedFromTerminalRewardsScreen(), 5000, "ProceedFromTerminalRewardsScreen");
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion

    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card =>
                {
                    var stats = new Dictionary<string, object?>();
                    try { foreach (var dv in card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                    var bundleVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in stats) if (kv.Value != null) bundleVars[kv.Key] = kv.Value;
                    var bundleCardDesc = _loc.Bilingual("cards", card.Id.Entry + ".description");
                    bundleCardDesc = LocLookup.ResolveDescriptionWithStats(bundleCardDesc, bundleVars);
                    return new Dictionary<string, object?>
                    {
                        ["name"] = _loc.Card(card.Id.Entry),
                        ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
                        ["type"] = card.Type.ToString(),
                        ["description"] = bundleCardDesc,
                        ["stats"] = stats.Count > 0 ? stats : null,
                    };
                }).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) => SerializeCard(cr.Card, i)).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = PlayerSummary(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) => SerializeCard(card, i)).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward or pending potion rewards
        if (_pendingCardReward != null || (_pendingPotionRewards != null && _pendingPotionRewards.Count > 0))
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        // Check if RunManager reports game over
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            // With Task.Yield() patched, combat init should be synchronous
            _syncCtx.Pump();
            WaitForActionExecutor();

            // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
            // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;  // Jump back to card_select handling
            }

            if (CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPlayPhase)
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            // Fallback: brief wait
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (CombatManager.Instance.IsPlayPhase) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            return CombatPlayState(player);
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            try
            {
                return TreasureState(treasureRoom);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
            {
                // BUG-013: Relic session conflict escaped TreasureState retry loop
                Log($"Relic session conflict escaped to DetectDecisionPoint: {ex.Message}");
                ForceResetRelicPickingState();
                ForceToMap();
                return MapSelectState();
            }
            catch (Exception ex)
            {
                Log($"TreasureState failed, recovering to map: {ex.GetType().Name}: {ex.Message}");
                ForceToMap();
                return MapSelectState();
            }
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunWithTimeout(RunManager.Instance.GenerateMap(), 5000, "GenerateMap");
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                // Current coord is invalid (stale after forced room transition); treat as no position
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
                if (sp?.Children != null)
                {
                    foreach (var child in sp.Children)
                    {
                        choices.Add(new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        });
                    }
                }
            }
            else
            {
                choices = (currentPoint.Children ?? Enumerable.Empty<MapPoint>())
                    .Select(child => new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    })
                    .ToList();
            }
        }
        else
        {
            // Starting point — pick from starting row
            var startPoint = map.StartingMapPoint;
            choices = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["col"] = (int)startPoint.coord.col,
                    ["row"] = (int)startPoint.coord.row,
                    ["type"] = startPoint.PointType.ToString(),
                }
            };
            // Add all children of start point as well since we can travel to them
            if (startPoint.Children != null)
            {
                foreach (var child in startPoint.Children)
                {
                    choices.Add(new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    });
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            ["player"] = PlayerSummary(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        // Snapshot hand cards to avoid "Collection was modified" during enumeration
        var hand = pcs?.Hand?.Cards?.ToList().Select((c, i) =>
        {
            var cardInfo = SerializeCard(c, i, inCombat: true);
            // Combat-specific fields
            cardInfo["can_play"] = c.CanPlay(out _, out _);
            cardInfo["target_type"] = c.TargetType.ToString();
            // BUG-007: Override can_play for star-cost cards when player lacks stars
            try
            {
                var starCost = c.BaseStarCost;
                if (starCost > 0 && pcs != null && pcs.Stars < starCost)
                    cardInfo["can_play"] = false;
            }
            catch { }
            return cardInfo;
        }).ToList() ?? new();

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        // Snapshot enemies to avoid "Collection was modified" during enumeration
        var enemies = combatState?.Enemies?.ToList()
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) =>
            {
                // Extract detailed intent info
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents.ToList())
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    if (atk.Repeats > 1) intentInfo["hits"] = atk.Repeats;
                                }
                                catch { }
                            }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                // Enemy powers
                var ePowers = e.Powers?.Select(pw => new Dictionary<string, object?>
                {
                    ["name"] = _loc.Power(pw.Id.Entry),
                    ["description"] = _loc.PowerDescription(pw.Id.Entry, pw.Amount),
                    ["amount"] = pw.Amount,
                }).ToList();

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Monster(e.Monster?.Id.Entry ?? "UNKNOWN"),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };
            }).ToList() ?? new();

        // Player powers/buffs
        var playerPowers = player.Creature?.Powers?.Select(pw => new Dictionary<string, object?>
        {
            ["name"] = _loc.Power(pw.Id.Entry),
            ["description"] = _loc.PowerDescription(pw.Id.Entry, pw.Amount),
            ["amount"] = pw.Amount,
        }).ToList();

        // Compute per-card effective damage against each enemy
        try
        {
            var playerPowerList = player.Creature?.Powers?.ToList();
            int playerStrength = playerPowerList?.FirstOrDefault(p => p.Id.Entry == "STRENGTH")?.Amount ?? 0;
            bool playerWeak = playerPowerList?.Any(p => p.Id.Entry == "WEAK" && p.Amount > 0) ?? false;
            var enemyCreatures = combatState?.Enemies?.ToList()?.Where(e => e != null && e.IsAlive).ToList();

            if (enemyCreatures != null)
            {
                var handCards = pcs?.Hand?.Cards?.ToList();
                for (int hi = 0; hi < hand.Count && handCards != null && hi < handCards.Count; hi++)
                {
                    var c = handCards[hi];
                    var cardInfo = hand[hi];
                    // Only compute for attack cards with a damage-like stat
                    var dvList = c.DynamicVars.Values.ToList();
                    int baseDmg = 0;
                    bool hasDamage = false;
                    // Check for standard "Damage" stat first
                    var damageDv = dvList.FirstOrDefault(dv => dv.Name.Equals("Damage", StringComparison.OrdinalIgnoreCase));
                    if (damageDv != null) { baseDmg = (int)damageDv.BaseValue; hasDamage = true; }
                    // Also check for ostydamage and calculateddamage stat keys
                    if (!hasDamage)
                    {
                        var ostyDv = dvList.FirstOrDefault(dv => dv.Name.Equals("OstyDamage", StringComparison.OrdinalIgnoreCase));
                        if (ostyDv != null) { baseDmg = (int)ostyDv.BaseValue; hasDamage = true; }
                    }
                    if (!hasDamage)
                    {
                        var calcDv = dvList.FirstOrDefault(dv => dv.Name.Equals("CalculatedDamage", StringComparison.OrdinalIgnoreCase));
                        if (calcDv != null) { baseDmg = (int)calcDv.BaseValue; hasDamage = true; }
                    }
                    if (c.Type == CardType.Attack && hasDamage)
                    {
                        var effectiveDamages = enemyCreatures.Select(enemy =>
                        {
                            var enemyPowers = enemy.Powers?.ToList();
                            bool enemyVulnerable = enemyPowers?.Any(p => p.Id.Entry == "VULNERABLE" && p.Amount > 0) ?? false;
                            double dmg = baseDmg + playerStrength;
                            if (playerWeak) dmg *= 0.75;
                            if (enemyVulnerable) dmg *= 1.5;
                            return (int)Math.Floor(dmg);
                        }).ToList();
                        cardInfo["effective_damage"] = effectiveDamages;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Effective damage calc: {ex.Message}");
        }

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["exhaust_pile_count"] = pcs?.ExhaustPile?.Cards?.Count ?? 0,
        };

        // Pile contents (wrapped in try/catch — game state access can throw)
        try
        {
            // Draw pile: send card IDs but NOT the order (showing draw order would be cheating)
            var drawCards = pcs?.DrawPile?.Cards?.ToList();
            if (drawCards != null)
            {
                var shuffled = drawCards.OrderBy(_ => Guid.NewGuid()).ToList();
                result["draw_pile"] = shuffled.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id.Entry,
                    ["name"] = _loc.Card(c.Id.Entry),
                }).ToList();
            }

            // Discard pile contents
            var discardCards = pcs?.DiscardPile?.Cards?.ToList();
            if (discardCards != null)
            {
                result["discard_pile"] = discardCards.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id.Entry,
                    ["name"] = _loc.Card(c.Id.Entry),
                }).ToList();
            }

            // Exhaust pile contents
            var exhaustCards = pcs?.ExhaustPile?.Cards?.ToList();
            if (exhaustCards != null)
            {
                result["exhaust_pile"] = exhaustCards.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id.Entry,
                    ["name"] = _loc.Card(c.Id.Entry),
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            Log($"Pile contents data: {ex.Message}");
        }

        // Character-specific mechanics
        try
        {
            // Defect: Orbs
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Bilingual("orbs", orb.Id.Entry + ".title"),
                    ["type"] = orb.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)orb.PassiveVal,
                    ["evoke"] = (int)orb.EvokeVal,
                }).ToList();
                result["orb_slots"] = orbQueue.Capacity;
            }

            // Regent: Stars
            if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
            {
                result["stars"] = pcs.Stars;
            }

            // Necrobinder: Osty (minion)
            var osty = player.Osty;
            if (osty != null)
            {
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();

        // Generate rewards manually instead of using TestMode auto-accept
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                var rewards = RunWithTimeout(rewardsSet.GenerateWithoutOffering(), 5000, "GenerateWithoutOffering");
                _syncCtx.Pump();

                // Auto-collect gold and relics, but present card and potion choices to agent
                var cardRewards = new List<CardReward>();
                var potionRewards = new List<MegaCrit.Sts2.Core.Rewards.PotionReward>();
                foreach (var reward in rewards)
                {
                    if (reward is GoldReward || reward is MegaCrit.Sts2.Core.Rewards.RelicReward)
                    {
                        try { RunWithTimeout(reward.OnSelectWrapper(), 3000, "Reward.OnSelectWrapper"); _syncCtx.Pump(); }
                        catch (Exception ex) { Log($"Auto-collect reward: {ex.Message}"); }
                    }
                    else if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward pr)
                    {
                        potionRewards.Add(pr);
                    }
                    else if (reward is CardReward cr)
                    {
                        cardRewards.Add(cr);
                    }
                }

                _pendingPotionRewards = potionRewards.Count > 0 ? potionRewards : null;

                if (cardRewards.Count > 0 || potionRewards.Count > 0)
                {
                    _pendingCardReward = cardRewards.Count > 0 ? cardRewards[0] : null;
                    _pendingRewards = rewards;
                    return CardRewardState(player, combatRoom);
                }

                _pendingRewards = null;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        // No more pending rewards — proceed
        _pendingCardReward = null;
        _pendingPotionRewards = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        // Boss → next act
        if (combatRoom.RoomType == RoomType.Boss)
        {
            Log("Boss defeated, entering next act");
            var actTimer = Stopwatch.StartNew();
            try
            {
                // EnterNextAct can hang if async callbacks deadlock.
                // Use SuppressYield to prevent Task.Yield blocking, and
                // pump with a hard timeout.
                YieldPatches.SuppressYield = true;
                var task = RunManager.Instance.EnterNextAct();
                // Pump while waiting, with a 10s hard timeout
                while (!task.IsCompleted && actTimer.ElapsedMilliseconds < 10_000)
                {
                    _syncCtx.Pump();
                    Thread.Sleep(10);
                }
                YieldPatches.SuppressYield = false;
                if (!task.IsCompleted)
                {
                    Log($"EnterNextAct timed out after {actTimer.ElapsedMilliseconds}ms — forcing map transition");
                    // Force to map even if EnterNextAct didn't complete
                    try { _syncCtx.PumpWithTimeout(perCallbackTimeoutMs: 500); } catch { }
                }
                else
                {
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                }
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            finally { YieldPatches.SuppressYield = false; }
            return DetectDecisionPoint();
        }

        // Normal → go to map
        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        // If no card reward AND no potion rewards, proceed past combat
        if (_pendingCardReward == null && (_pendingPotionRewards == null || _pendingPotionRewards.Count == 0))
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward?.Cards.Select((c, i) => SerializeCard(c, i)).ToList()
                    ?? new List<Dictionary<string, object?>>();

        // Serialize pending potion rewards
        var potionRewardsList = new List<Dictionary<string, object?>>();
        if (_pendingPotionRewards != null)
        {
            for (int i = 0; i < _pendingPotionRewards.Count; i++)
            {
                var pr = _pendingPotionRewards[i];
                try
                {
                    var potionModel = pr.Potion;
                    var prDesc = CleanupDescriptionTemplates(
                        _loc.Bilingual("potions", (potionModel?.Id.Entry ?? "?") + ".description"));
                    potionRewardsList.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["id"] = potionModel?.Id.Entry,
                        ["name"] = _loc.Potion(potionModel?.Id.Entry ?? "?"),
                        ["description"] = prDesc,
                    });
                }
                catch (Exception ex)
                {
                    Log($"Serialize potion reward {i}: {ex.Message}");
                    potionRewardsList.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["name"] = "Unknown Potion",
                        ["description"] = "",
                    });
                }
            }
        }

        // Determine if potion belt is full
        var potionsList = player.Potions?.ToList() ?? new();
        var potionSlotsFull = potionsList.All(p => p != null);

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward?.CanSkip ?? true,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["potion_rewards"] = potionRewardsList.Count > 0 ? potionRewardsList : null,
            ["potion_slots_full"] = potionSlotsFull,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };

        return result;
    }

    private void ForceToMap()
    {
        try
        {
            RunWithTimeout(RunManager.Instance.ProceedFromTerminalRewardsScreen(), 5000, "ProceedFromTerminalRewardsScreen");
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunWithTimeout(RunManager.Instance.EnterRoom(new MapRoom()), 5000, "EnterRoom(MapRoom)"); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // If we already chose an event option and the event didn't advance, force-finish
        if (_eventOptionChosen && localEvent != null && !localEvent.IsFinished)
        {
            var currentOpts = localEvent.CurrentOptions;
            var sameOptions = currentOpts != null && currentOpts.Count > 0 &&
                _lastEventOptionCount > 0 && currentOpts.Count == _lastEventOptionCount;
            if (sameOptions)
            {
                Log($"Event {localEvent.GetType().Name}: same options after choice, force-finishing");
                _eventOptionChosen = false;
                ForceToMap();
                return MapSelectState();
            }
            // Options changed — event advanced to next page, show new options
            _eventOptionChosen = false;
        }

        // If event is finished, proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunWithTimeout(RunManager.Instance.ProceedFromTerminalRewardsScreen(), 5000, "ProceedFromTerminalRewardsScreen");
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunWithTimeout(RunManager.Instance.EnterRoom(new MapRoom()), 5000, "EnterRoom(MapRoom)"); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunWithTimeout(RunManager.Instance.EnterRoom(new MapRoom()), 5000, "EnterRoom(MapRoom)"); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        // Build resolved vars from the event's DynamicVars.
        // StringVar instances (entity references like Enchantment, Rarity, Potion, Type)
        // use their StringValue; numeric vars use IntValue.
        var resolvedEventVars = new Dictionary<string, object?>();
        try
        {
            if (localEvent.DynamicVars?.Values != null)
            {
                foreach (var dv in localEvent.DynamicVars.Values)
                    resolvedEventVars[dv.Name] = ResolveDynamicVar(dv);
            }
        }
        catch { }

        // Also collect resolved vars from LocString.Variables on option LocStrings
        // (the game populates these with fully-resolved entity names)
        void MergeLocStringVars(LocString? ls)
        {
            try
            {
                if (ls?.Variables == null) return;
                foreach (var kv in ls.Variables)
                {
                    if (kv.Value is string strVal)
                        resolvedEventVars[kv.Key] = strVal;
                    else if (kv.Value is LocString locVal)
                    {
                        try { resolvedEventVars[kv.Key] = StripBBCode(locVal.GetFormattedText()); }
                        catch { resolvedEventVars[kv.Key] = locVal.LocEntryKey; }
                    }
                    else if (kv.Value is DynamicVar dynVal)
                        resolvedEventVars[kv.Key] = ResolveDynamicVar(dynVal);
                }
            }
            catch { }
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                // Merge vars from option Title and Description LocStrings
                MergeLocStringVars(opt.Title);
                MergeLocStringVars(opt.Description);

                // Try GetFormattedText() first (game's SmartFormat with resolved vars)
                string? title = null;
                try
                {
                    if (opt.Title != null)
                    {
                        var fmt = opt.Title.GetFormattedText();
                        if (!string.IsNullOrEmpty(fmt) && fmt != opt.Title.LocEntryKey)
                            title = StripBBCode(fmt);
                    }
                }
                catch { }

                // Fallback: manual loc table lookup + variable substitution
                if (title == null && opt.Title != null)
                {
                    var t = _loc.Bilingual(opt.Title.LocTable, opt.Title.LocEntryKey);
                    if (t != opt.Title.LocEntryKey)
                        title = SubstituteVars(t, resolvedEventVars);
                }
                // Fallback: try to extract option ID from the key and look up as relic/card/potion
                if (title == null && opt.TextKey != null)
                {
                    // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" -> extract "STONE_HUMIDIFIER"
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";
                // Resolve SmartFormat patterns in title with event vars
                if (title != null && title.Contains('{') && resolvedEventVars.Count > 0)
                {
                    var ciVars = new Dictionary<string, object?>(resolvedEventVars, System.StringComparer.OrdinalIgnoreCase);
                    title = LocLookup.ResolveDescriptionWithStats(title, ciVars);
                }
                // Clean up any remaining templates/BBCode in title
                if (title != null && (title.Contains('{') || title.Contains('[')))
                    title = CleanupDescriptionTemplates(title);

                // Description: try GetFormattedText() first
                string? optDesc = null;
                try
                {
                    if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                    {
                        var fmt = opt.Description.GetFormattedText();
                        if (!string.IsNullOrEmpty(fmt) && fmt != opt.Description.LocEntryKey)
                            optDesc = StripBBCode(fmt);
                    }
                }
                catch { }

                // Fallback: manual loc table lookup + variable substitution
                if (optDesc == null && opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                {
                    var d = _loc.Bilingual(opt.Description.LocTable, opt.Description.LocEntryKey);
                    if (d != opt.Description.LocEntryKey)
                        optDesc = SubstituteVars(d, resolvedEventVars);
                }
                // Fallback: try relic/card description
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Bilingual("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                // Resolve literal localization keys embedded in description text
                // (e.g. "Add CLUMSY.title to your Deck" -> "Add Clumsy to your Deck")
                if (optDesc != null)
                    optDesc = _loc.ResolveInlineLocKeys(optDesc);
                // Resolve SmartFormat patterns (plural, cond, energyIcons, etc.)
                // with the actual event vars before cleanup strips unresolved templates
                if (optDesc != null && resolvedEventVars.Count > 0)
                {
                    var ciVars = new Dictionary<string, object?>(resolvedEventVars, System.StringComparer.OrdinalIgnoreCase);
                    optDesc = LocLookup.ResolveDescriptionWithStats(optDesc, ciVars);
                }
                // Clean up remaining templates: energy icons, BBCode, {Var:format} patterns
                if (optDesc != null)
                    optDesc = CleanupDescriptionTemplates(optDesc);

                // Build vars for JSON output (resolved names, not raw IDs)
                Dictionary<string, object?>? optVars = resolvedEventVars.Count > 0
                    ? new Dictionary<string, object?>(resolvedEventVars) : null;

                // Also try relic vars (for Neow options)
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = ResolveDynamicVar(dv);
                        }
                    }
                    catch { }
                }

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["vars"] = optVars?.Count > 0 ? optVars : null,
                };
            }).ToList();

        // Resolve event name — try ancients table first (for Neow), then events
        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventName = _loc.Bilingual("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        // Resolve event description: try GetFormattedText() first, then manual lookup
        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            try
            {
                MergeLocStringVars(localEvent.Description);
                var fmt = localEvent.Description.GetFormattedText();
                if (!string.IsNullOrEmpty(fmt) && fmt != localEvent.Description.LocEntryKey)
                    eventDesc = StripBBCode(fmt);
            }
            catch { }
            if (eventDesc == null)
            {
                var d = _loc.Bilingual(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
                if (d != localEvent.Description.LocEntryKey)
                    eventDesc = SubstituteVars(d, resolvedEventVars);
            }
        }
        // Resolve literal localization keys in event description
        if (eventDesc != null)
            eventDesc = _loc.ResolveInlineLocKeys(eventDesc);
        // Resolve SmartFormat patterns (plural, cond, energyIcons, etc.)
        // with the actual event vars before cleanup strips unresolved templates
        if (eventDesc != null && resolvedEventVars.Count > 0)
        {
            var ciVars = new Dictionary<string, object?>(resolvedEventVars, System.StringComparer.OrdinalIgnoreCase);
            eventDesc = LocLookup.ResolveDescriptionWithStats(eventDesc, ciVars);
        }
        // Clean up remaining templates: energy icons, BBCode, {Var:format} patterns
        if (eventDesc != null)
            eventDesc = CleanupDescriptionTemplates(eventDesc);

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    /// <summary>
    /// Resolve a DynamicVar to a display-friendly value.
    /// StringVar instances (entity references like Enchantment, Rarity, Potion, Type)
    /// return their StringValue. Numeric vars return IntValue.
    /// </summary>
    private static object? ResolveDynamicVar(DynamicVar dv)
    {
        if (dv is StringVar sv && sv.StringValue != null)
            return sv.StringValue;
        // Try ToString() which may return a resolved entity name
        try
        {
            var s = dv.ToString();
            if (!string.IsNullOrEmpty(s) && s != "0"
                && s != dv.BaseValue.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return s;
        }
        catch { }
        return dv.IntValue;
    }

    /// <summary>
    /// Simple template variable substitution: replaces bare {VarName} placeholders
    /// in a localization string with resolved values. Does NOT touch SmartFormat
    /// patterns like {Var:plural:...}, {Var:energyIcons(...)}, {Var:cond:...} etc.
    /// — those are left for ResolveDescriptionWithStats to handle properly.
    /// </summary>
    private static string SubstituteVars(string template, Dictionary<string, object?> vars)
    {
        if (vars.Count == 0 || !template.Contains('{'))
            return template;
        // Only match bare {VarName} — NOT {VarName:format}.
        // Patterns with format specs (plural, cond, energyIcons, etc.) must be
        // resolved by ResolveDescriptionWithStats which understands SmartFormat.
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+)\}", m =>
        {
            var key = m.Groups[1].Value;
            if (vars.TryGetValue(key, out var val) && val != null)
                return val.ToString() ?? m.Value;
            return m.Value;
        });
    }

    private static string StripBBCode(string text) => LocLookup.StripBBCode(text);

    /// <summary>Clean up remaining template patterns in any description string:
    /// energy icons, star icons, BBCode, and unresolved {Var:format} patterns.
    /// Uses balanced-brace parsing to avoid partial matches on nested templates.</summary>
    private static string CleanupDescriptionTemplates(string desc)
    {
        // Use ResolveDescriptionWithStats with empty vars — it will resolve
        // energyIcons, starIcons, singleStarIcon, and strip BBCode properly.
        // Any unresolvable templates (no var value) are left intact, then
        // we strip them with balanced-brace-aware removal below.
        desc = LocLookup.ResolveDescriptionWithStats(desc, new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase));

        // Strip remaining unresolved {Var:format} patterns using balanced-brace parsing
        var result = new System.Text.StringBuilder(desc.Length);
        int i = 0;
        while (i < desc.Length)
        {
            if (desc[i] != '{') { result.Append(desc[i]); i++; continue; }
            var parsed = LocLookup.ParseSmartBlock(desc, i);
            if (parsed == null) { result.Append(desc[i]); i++; continue; }
            var (varName, fmt, endIdx) = parsed.Value;
            if (fmt != null)
            {
                // Has a format spec — it's an unresolved template, strip it
                i = endIdx + 1;
            }
            else
            {
                // Bare {Var} — leave it (might be meaningful)
                result.Append(desc, i, endIdx - i + 1);
                i = endIdx + 1;
            }
        }
        desc = result.ToString();

        // Clean up whitespace
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"  +", " ");
        return desc.Trim();
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["option_id"] = opt.OptionId,
            ["name"] = opt.GetType().Name,
            ["is_enabled"] = opt.IsEnabled,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.Inventory;
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                if (card == null)
                {
                    var entry = "?";
                    return new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["name"] = _loc.Card(entry),
                        ["type"] = "?",
                        ["card_cost"] = 0,
                        ["description"] = _loc.Bilingual("cards", entry + ".description"),
                        ["cost"] = e.Cost,
                        ["is_stocked"] = e.IsStocked,
                        ["on_sale"] = e.IsOnSale,
                    };
                }
                var cardInfo = SerializeCard(card, i);
                // Shop-specific: rename energy "cost" to "card_cost" and add gold "cost"
                cardInfo["card_cost"] = cardInfo["cost"];
                cardInfo.Remove("cost");
                cardInfo["cost"] = e.Cost;
                cardInfo["is_stocked"] = e.IsStocked;
                cardInfo["on_sale"] = e.IsOnSale;
                return cardInfo;
            }).ToList();

        var relics = inv.RelicEntries.Select((e, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["name"] = _loc.Relic(e.Model?.Id.Entry ?? "?"),
            ["description"] = CleanupDescriptionTemplates(
                _loc.Bilingual("relics", (e.Model?.Id.Entry ?? "?") + ".description")),
            ["cost"] = e.Cost,
            ["is_stocked"] = e.IsStocked,
        }).ToList();

        var potions = inv.PotionEntries.Select((e, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["name"] = _loc.Potion(e.Model?.Id.Entry ?? "?"),
            ["description"] = CleanupDescriptionTemplates(
                _loc.Bilingual("potions", (e.Model?.Id.Entry ?? "?") + ".description")),
            ["cost"] = e.Cost,
            ["is_stocked"] = e.IsStocked,
        }).ToList();

        var removal = merchantRoom.Inventory.CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal?.Cost,
            ["card_removal_available"] = removal != null && !_shopCardRemoved,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        // Treasure rooms give relics via TreasureRoomRelicSynchronizer
        Log("Treasure room — collecting rewards");

        // BUG-013: Ensure any pending relic picking session is complete before starting new one.
        // First, use reflection to actively wait for _isInSharedRelicPicking to clear.
        // Then pump and wait for action executor. This two-phase wait is much more reliable
        // than the previous single WaitForActionExecutor call.
        WaitForRelicPickingComplete();
        WaitForActionExecutor();
        _syncCtx.Pump();

        // Retry up to 3 times with increasing delays if relic picking session conflicts
        const int maxRetries = 3;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Log($"Treasure rewards retry attempt {attempt}/{maxRetries}");
                    // Wait with increasing delay before retry
                    Thread.Sleep(attempt * 200);
                    WaitForRelicPickingComplete();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }

                RunWithTimeout(treasureRoom.DoNormalRewards(), 5000, "DoNormalRewards");
                _syncCtx.Pump();
                RunWithTimeout(treasureRoom.DoExtraRewardsIfNeeded(), 5000, "DoExtraRewardsIfNeeded");
                _syncCtx.Pump();
                break; // Success — exit retry loop
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
            {
                // BUG-013: Relic session conflict — wait for pending session then retry
                Log($"Relic session conflict (attempt {attempt}): {ex.Message}");
                if (attempt >= maxRetries)
                {
                    Log("Treasure rewards: all retries exhausted, force-clearing relic state and skipping rewards");
                    ForceResetRelicPickingState();
                }
            }
            catch (Exception ex)
            {
                Log($"Treasure rewards: {ex.Message}");
                break; // Non-relic-session errors are not retryable
            }
        }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    /// <summary>
    /// Return a game_over response when the global watchdog kills a hung action.
    /// The agent sees this as a clean loss (victory=false) with a timeout_recovery flag,
    /// rather than a bridge EOF/timeout which causes retry storms.
    /// </summary>
    private Dictionary<string, object?> TimeoutGameOverState(string timedOutAction)
    {
        try
        {
            var player = _runState!.Players[0];
            var summary = PlayerSummary(player);
            // Use last known HP (before the hang) — after ForceKillPlayer, HP is 0
            summary["hp"] = 0;
            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "game_over",
                ["context"] = RunContext(),
                ["victory"] = false,
                ["timeout_recovery"] = true,
                ["timed_out_action"] = timedOutAction,
                ["player"] = summary,
                ["act"] = _runState.CurrentActIndex + 1,
                ["floor"] = _runState.ActFloor,
            };
        }
        catch (Exception ex)
        {
            // If even building the game_over state fails, return a minimal one
            Log($"TimeoutGameOverState failed: {ex.Message}");
            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "game_over",
                ["victory"] = false,
                ["timeout_recovery"] = true,
                ["timed_out_action"] = timedOutAction,
            };
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Force-start the next turn when the normal EndTurn async chain fails (e.g., NRE in
    /// AfterAllPlayersReadyToEndTurn). Uses reflection to call CombatManager.StartTurn
    /// directly, bypassing the broken async chain.
    /// </summary>

    /// <summary>
    /// Force-kill the player to end an unrecoverable combat. Used as a last resort
    /// when all timeout and recovery mechanisms have failed.
    /// </summary>
    private void ForceKillPlayer(Player player)
    {
        try
        {
            var creature = player.Creature;
            if (creature == null || creature.IsDead)
            {
                Log("ForceKillPlayer: creature already dead or null");
                return;
            }
            // CurrentHp is read-only — set via reflection
            var hpProp = creature.GetType().GetProperty("CurrentHp", BindingFlags.Public | BindingFlags.Instance);
            if (hpProp != null && hpProp.CanWrite)
            {
                hpProp.SetValue(creature, 0);
            }
            else
            {
                // Try backing field
                var hpField = creature.GetType().GetField("_currentHp", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? creature.GetType().GetField("<CurrentHp>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                hpField?.SetValue(creature, 0);
            }
            _syncCtx.Pump();
            Log("ForceKillPlayer: player HP set to 0");
        }
        catch (Exception ex)
        {
            Log($"ForceKillPlayer failed: {ex.Message}");
        }
    }

    private void ForceStartNextTurn(Player player)
    {
        var cm = CombatManager.Instance;
        var cmType = cm.GetType();

        // Reset internal state that EndTurn partially modified
        // Clear _playersReadyToEndTurn and _playersReadyToBeginEnemyTurn
        var readyEndField = cmType.GetField("_playersReadyToEndTurn", BindingFlags.NonPublic | BindingFlags.Instance);
        if (readyEndField?.GetValue(cm) is System.Collections.ICollection readyEnd)
        {
            var clearMethod = readyEnd.GetType().GetMethod("Clear");
            clearMethod?.Invoke(readyEnd, null);
        }

        var readyBeginField = cmType.GetField("_playersReadyToBeginEnemyTurn", BindingFlags.NonPublic | BindingFlags.Instance);
        if (readyBeginField?.GetValue(cm) is System.Collections.ICollection readyBegin)
        {
            var clearMethod = readyBegin.GetType().GetMethod("Clear");
            clearMethod?.Invoke(readyBegin, null);
        }

        // Reset EndingPlayerTurnPhaseOne flag
        var phaseOneProp = cmType.GetProperty("EndingPlayerTurnPhaseOne", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        phaseOneProp?.SetValue(cm, false);

        // Set the combat side back to player and increment round
        var stateField = cmType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        var state = stateField?.GetValue(cm);
        if (state != null)
        {
            var stateType = state.GetType();
            // Increment round number — try property first, then backing field
            var roundProp = stateType.GetProperty("RoundNumber", BindingFlags.Public | BindingFlags.Instance);
            if (roundProp != null && roundProp.CanWrite)
            {
                var currentRound = (int)(roundProp.GetValue(state) ?? 0);
                roundProp.SetValue(state, currentRound + 1);
                Log($"ForceStartNextTurn: incremented RoundNumber via property {currentRound} -> {currentRound + 1}");
            }
            else
            {
                // Property not writable — try backing field directly
                var roundField = stateType.GetField("_roundNumber", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? stateType.GetField("<RoundNumber>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (roundField != null)
                {
                    var currentRound = (int)(roundField.GetValue(state) ?? 0);
                    roundField.SetValue(state, currentRound + 1);
                    Log($"ForceStartNextTurn: incremented RoundNumber via field {currentRound} -> {currentRound + 1}");
                }
                else
                {
                    Log("ForceStartNextTurn: WARNING — could not find writable RoundNumber property or backing field");
                }
            }
            // Switch side back to Player (1)
            var sideProp = stateType.GetProperty("CurrentSide", BindingFlags.Public | BindingFlags.Instance);
            sideProp?.SetValue(state, (MegaCrit.Sts2.Core.Combat.CombatSide)1);
        }

        // Call StartTurn via reflection (with wall-clock timeout to prevent hang)
        var startTurnMethod = cmType.GetMethod("StartTurn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (startTurnMethod != null)
        {
            Log("Invoking CombatManager.StartTurn via reflection...");
            var task = (Task?)startTurnMethod.Invoke(cm, new object?[] { null });
            if (task != null)
            {
                _syncCtx.Pump();
                // Pump until task completes or play phase resumes (wall-clock timeout 3s)
                var startTurnTimer = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                {
                    _syncCtx.Pump();
                    if (task.IsCompleted) break;
                    if (CombatManager.Instance.IsPlayPhase) break;
                    if (startTurnTimer.ElapsedMilliseconds > 3_000)
                    {
                        Log($"ForceStartNextTurn: StartTurn pump timed out after {startTurnTimer.ElapsedMilliseconds}ms");
                        break;
                    }
                    Thread.Sleep(0);
                }
            }
        }
        else
        {
            Log("Could not find StartTurn method");
        }
    }

    /// <summary>
    /// Defensive fallback: force-increment the round number if the normal end_turn path
    /// (or ForceStartNextTurn) left the round unchanged. This prevents the round-stuck loop
    /// where end_turn returns combat_play but the round never advances.
    /// </summary>
    private void ForceIncrementRound(int roundBefore)
    {
        try
        {
            var cm = CombatManager.Instance;

            // First try via DebugOnlyGetState() which returns the public state object
            var state = cm.DebugOnlyGetState();
            if (state != null)
            {
                var stateType = state.GetType();
                var roundProp = stateType.GetProperty("RoundNumber", BindingFlags.Public | BindingFlags.Instance);
                if (roundProp != null && roundProp.CanWrite)
                {
                    roundProp.SetValue(state, roundBefore + 1);
                    return;
                }
                // Try backing field
                var roundField = stateType.GetField("_roundNumber", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? stateType.GetField("<RoundNumber>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (roundField != null)
                {
                    roundField.SetValue(state, roundBefore + 1);
                    return;
                }
            }

            // Fallback: try via the private _state field on CombatManager
            var cmType = cm.GetType();
            var stateField = cmType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            var privateState = stateField?.GetValue(cm);
            if (privateState != null)
            {
                var pType = privateState.GetType();
                var rProp = pType.GetProperty("RoundNumber", BindingFlags.Public | BindingFlags.Instance);
                if (rProp != null && rProp.CanWrite)
                {
                    rProp.SetValue(privateState, roundBefore + 1);
                    return;
                }
                var rField = pType.GetField("_roundNumber", BindingFlags.NonPublic | BindingFlags.Instance)
                          ?? pType.GetField("<RoundNumber>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (rField != null)
                {
                    rField.SetValue(privateState, roundBefore + 1);
                    return;
                }
            }

            Log("ForceIncrementRound: could not find any writable round field");
        }
        catch (Exception ex)
        {
            Log($"ForceIncrementRound failed: {ex.Message}");
        }
    }

    private void WaitForActionExecutor(int timeoutMs = 2000)
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor (with wall-clock timeout).
                // Use Thread.Sleep(0) instead of Sleep(1) to avoid 15ms sleep penalty
                // on Windows/macOS while still yielding the timeslice.
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 5000; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    if (_cardSelector.HasPending) break;
                    if (timer.ElapsedMilliseconds > timeoutMs)
                    {
                        Log($"WaitForActionExecutor timed out after {timer.ElapsedMilliseconds}ms");
                        break;
                    }
                    Thread.Sleep(0);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    // NOTE: The _isInSharedRelicPicking flag lives on MegaCrit.Sts2.Core.Entities.TreasureRelicPicking,
    // a per-instance entity that cannot be accessed via RunManager reflection.
    // Protection against the relic picking race condition relies on:
    // 1. WaitForRelicPickingComplete() — aggressively pumps the sync context
    // 2. DoMapSelect retry loop — catches InvalidOperationException and retries
    // 3. DetectDecisionPoint wrapper — catches exceptions from TreasureState and recovers to map


    /// <summary>
    /// Wait for any pending async work to complete before entering a new room.
    /// The _isInSharedRelicPicking flag lives on a per-instance TreasureRelicPicking entity
    /// that cannot be accessed via reflection from RunManager. Instead of trying to introspect
    /// the flag, we aggressively pump the sync context and wait for the action executor to idle.
    /// The primary defense against relic picking race conditions is the catch-and-retry loop
    /// in DoMapSelect and the defensive wrapper in DetectDecisionPoint.
    /// </summary>
    private void WaitForRelicPickingComplete(int timeoutMs = 2000)
    {
        try
        {
            // Pump aggressively to drain any pending async continuations
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 200 && timer.ElapsedMilliseconds < timeoutMs; i++)
            {
                _syncCtx.Pump();
                var executor = RunManager.Instance.ActionExecutor;
                if (!executor.IsRunning) break;
                Thread.Sleep(10);
            }
            _syncCtx.Pump();
        }
        catch (Exception ex)
        {
            Log($"WaitForRelicPickingComplete exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Aggressively pump the sync context to try to drain pending relic picking async work.
    /// Since the _isInSharedRelicPicking flag is on a per-instance entity (TreasureRelicPicking)
    /// and not accessible via reflection, this is best-effort. The real protection is the
    /// catch-and-retry loop in DoMapSelect.
    /// </summary>
    private void ForceResetRelicPickingState()
    {
        try
        {
            // Pump aggressively with longer delay to let async work complete
            for (int i = 0; i < 50; i++)
            {
                _syncCtx.Pump();
                if (!RunManager.Instance.ActionExecutor.IsRunning) break;
                Thread.Sleep(20);
            }
            _syncCtx.Pump();
            Log("ForceResetRelicPickingState: pumped sync context");
        }
        catch (Exception ex)
        {
            Log($"ForceResetRelicPickingState exception: {ex.Message}");
        }
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (CombatManager.Instance.IsPlayPhase) return;
            WaitForActionExecutor();
            if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Shared card serialization: produces a consistent set of fields for any CardModel.
    /// Context-specific fields (can_play, target_type, cost/gold, is_stocked, on_sale)
    /// should be added by the caller after invoking this method.
    /// </summary>
    private Dictionary<string, object?> SerializeCard(CardModel c, int index, bool inCombat = false)
    {
        var stats = new Dictionary<string, object?>();
        try { foreach (var dv in c.DynamicVars.Values.ToList()) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }

        // Resolve card description with template substitution using balanced-brace-aware parser
        var desc = _loc.Bilingual("cards", c.Id.Entry + ".description");
        // Build vars dict from stats (case-insensitive) plus special context vars
        var descVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var kv in stats)
            if (kv.Value != null) descVars[kv.Key] = kv.Value;
        // Add context-specific pseudo-variables for InCombat and IfUpgraded
        descVars["InCombat"] = inCombat;
        descVars["IfUpgraded"] = c.IsUpgraded;
        // Pre-resolve {InCombat:trueText|falseText} using balanced braces
        desc = LocLookup.ResolveContextBranch(desc, "InCombat", inCombat);
        // Pre-resolve {IfUpgraded:show:trueText|falseText} using balanced braces
        desc = LocLookup.ResolveIfUpgraded(desc, c.IsUpgraded);
        // Now resolve all SmartFormat templates with stat values
        desc = LocLookup.ResolveDescriptionWithStats(desc, descVars);

        var cardInfo = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = c.Id.ToString(),
            ["name"] = _loc.Card(c.Id.Entry),
            ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
            ["type"] = c.Type.ToString(),
            ["rarity"] = c.Rarity.ToString(),
            ["upgraded"] = c.IsUpgraded,
            ["description"] = desc,
            ["stats"] = stats.Count > 0 ? stats : null,
            ["after_upgrade"] = GetUpgradedInfo(c),
        };

        // star_cost (conditional: only when > 0)
        try { if (c.BaseStarCost > 0) cardInfo["star_cost"] = c.BaseStarCost; } catch { }

        // keywords (conditional: only when non-empty)
        try
        {
            var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            if (kws?.Count > 0) cardInfo["keywords"] = kws;
        }
        catch { }

        // enchantment (conditional)
        try
        {
            if (c.Enchantment != null)
            {
                cardInfo["enchantment"] = _loc.Bilingual("enchantments", c.Enchantment.Id.Entry + ".title");
                if (c.Enchantment.Amount != 0) cardInfo["enchantment_amount"] = c.Enchantment.Amount;
            }
        }
        catch { }

        // affliction (conditional)
        try
        {
            if (c.Affliction != null)
            {
                cardInfo["affliction"] = _loc.Bilingual("afflictions", c.Affliction.Id.Entry + ".title");
                if (c.Affliction.Amount != 0) cardInfo["affliction_amount"] = c.Affliction.Amount;
            }
        }
        catch { }

        return cardInfo;
    }

    private Dictionary<string, object?>? GetUpgradedInfo(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var clone = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            // Apply existing upgrades first
            for (int i = 0; i < card.CurrentUpgradeLevel; i++)
            {
                clone.UpgradeInternal();
                clone.FinalizeUpgradeInternal();
            }
            // Apply one more upgrade
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = new Dictionary<string, object?>();
            try { foreach (var dv in clone.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }

            // Compare keywords before/after upgrade
            var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var addedKws = newKws.Except(oldKws).ToList();
            var removedKws = oldKws.Except(newKws).ToList();

            // Resolve upgraded description templates using balanced-brace-aware parser
            var upgVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kv in stats) if (kv.Value != null) upgVars[kv.Key] = kv.Value;
            var upgDesc = _loc.Bilingual("cards", card.Id.Entry + ".description");
            upgDesc = LocLookup.ResolveIfUpgraded(upgDesc, true); // upgraded card
            upgDesc = LocLookup.ResolveDescriptionWithStats(upgDesc, upgVars);

            return new Dictionary<string, object?>
            {
                ["cost"] = clone.EnergyCost?.GetResolved() ?? 0,
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = upgDesc,
                ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
                ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
            };
        }
        catch { return null; }
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Bilingual("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            // Snapshot collections with .ToList() before iterating to avoid
            // "Collection was modified; enumeration operation may not execute" from concurrent modification
            ["relics"] = player.Relics?.ToList().Select(r =>
            {
                var vars = new Dictionary<string, object?>();
                try { foreach (var dv in r.DynamicVars.Values.ToList()) vars[dv.Name] = (int)dv.BaseValue; } catch { }
                // Derive counter: use the first DynamicVar value if any, else -1 (no counter)
                int counter = -1;
                try { if (vars.Count > 0) counter = (int)(vars.Values.First() ?? -1); } catch { }
                // Resolve relic description templates using balanced-brace-aware parser
                var relicVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in vars) if (kv.Value != null) relicVars[kv.Key] = kv.Value;
                var relicDesc = _loc.Bilingual("relics", r.Id.Entry + ".description");
                relicDesc = LocLookup.ResolveDescriptionWithStats(relicDesc, relicVars);
                return new Dictionary<string, object?>
                {
                    ["id"] = r.Id.Entry,
                    ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = relicDesc,
                    ["counter"] = counter,
                    ["vars"] = vars.Count > 0 ? vars : null,
                };
            }).ToList(),
            ["potions"] = player.Potions?.ToList().Select((p, i) =>
            {
                if (p == null) return null;
                var pvars = new Dictionary<string, object?>();
                try { foreach (var dv in p.DynamicVars.Values.ToList()) pvars[dv.Name] = (int)dv.BaseValue; } catch { }
                // Resolve potion description templates using balanced-brace-aware parser
                var potVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in pvars) if (kv.Value != null) potVars[kv.Key] = kv.Value;
                var potDesc = _loc.Bilingual("potions", p.Id.Entry + ".description");
                potDesc = LocLookup.ResolveDescriptionWithStats(potDesc, potVars);
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Potion(p.Id.Entry),
                    ["description"] = potDesc,
                    ["vars"] = pvars.Count > 0 ? pvars : null,
                    ["target_type"] = p.TargetType.ToString(),
                };
            }).Where(x => x != null).ToList(),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.ToList().Where(c => c != null).Select(c =>
            {
                var dstats = new Dictionary<string, object?>();
                try { foreach (var dv in c.DynamicVars.Values.ToList()) dstats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                var dkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                // Resolve deck card description templates using balanced-brace-aware parser
                var deckVars = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dstats) if (kv.Value != null) deckVars[kv.Key] = kv.Value;
                var deckCardDesc = _loc.Bilingual("cards", c.Id.Entry + ".description");
                deckCardDesc = LocLookup.ResolveIfUpgraded(deckCardDesc, c.IsUpgraded);
                deckCardDesc = LocLookup.ResolveDescriptionWithStats(deckCardDesc, deckVars);
                return new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = _loc.Card(c.Id.Entry),
                    ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = c.Type.ToString(),
                    ["upgraded"] = c.IsUpgraded,
                    ["description"] = deckCardDesc,
                    ["stats"] = dstats.Count > 0 ? dstats : null,
                    ["keywords"] = dkws?.Count > 0 ? dkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(c),
                };
            }).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                // Handle special mappings
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                ctx["boss"] = new Dictionary<string, object?>
                {
                    ["id"] = bossIdEntry,
                    ["name"] = _loc.Monster(monsterKey),
                };
            }
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait").ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.Wait({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var optList = options.ToList();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // If only one option and minSelect requires it, auto-select
            if (optList.Count == 1 && minSelect >= 1)
                return Task.FromResult<IEnumerable<CardModel>>(optList);

            // Store pending selection and wait
            PendingOptions = optList;
            PendingMinSelect = minSelect;
            PendingMaxSelect = maxSelect;
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}");

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            _pendingTcs?.TrySetResult(selected);
            PendingOptions = null;
            _pendingTcs = null;
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            _pendingTcs?.TrySetResult(Array.Empty<CardModel>());
            PendingOptions = null;
            _pendingTcs = null;
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;

        public CardModel? GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return null;

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            _rewardChoice = -1;
            _rewardWait = new ManualResetEventSlim(false);

            Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            _rewardWait.Wait(TimeSpan.FromSeconds(300)); // Wait up to 5 min

            var choice = _rewardChoice;
            PendingRewardCards = null;
            _rewardWait = null;

            if (choice >= 0 && choice < options.Count)
                return options[choice].Card;
            return null;  // Skip
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardWait?.Set();
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();

            var locDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                            Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                sfField.SetValue(null, sf);
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            sfField.SetValue(null, sf);
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { Console.Error.WriteLine($"[WARN] Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }

            // Patch Neutralize.OnPlay to avoid NullRef in DamageCmd.Attack().Execute()
            try
            {
                var neutralizeType = typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize);
                var neutralizeOnPlay = neutralizeType.GetMethod("OnPlay",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (neutralizeOnPlay != null)
                {
                    var neutPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.NeutralizePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (neutPrefix != null)
                    {
                        harmony.Patch(neutralizeOnPlay, new HarmonyMethod(neutPrefix));
                        Console.Error.WriteLine("[INFO] Patched Neutralize.OnPlay");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize patch: {ex.Message}"); }

            // Patch HasEntry to always return true
            PatchMethod(harmony, typeof(LocTable), "HasEntry", nameof(LocPatches.HasEntryPrefix));

            // Patch IsLocalKey to always return true
            PatchMethod(harmony, typeof(LocTable), "IsLocalKey", nameof(LocPatches.HasEntryPrefix));

            // Patch LocString.Exists (static) to always return true
            var locStringExists = typeof(LocString).GetMethod("Exists",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (locStringExists != null)
            {
                PatchMethod(harmony, locStringExists, nameof(LocPatches.HasEntryPrefix));
            }

            // Patch LocTable.GetLocStringsWithPrefix to return empty list
            PatchMethod(harmony, typeof(LocTable), "GetLocStringsWithPrefix", nameof(LocPatches.GetLocStringsWithPrefixPrefix));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            // Return key as fallback "translation"
            __result = key;
            return false;
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        /// <summary>Harmony prefix: replace Neutralize.OnPlay with safe damage+weak.</summary>
        public static bool NeutralizePrefix(CardModel __instance, ref Task __result,
            PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            if (cardPlay.Target == null) { __result = Task.CompletedTask; return false; }
            __result = NeutralizeSafe(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
        {
            try
            {
                await CreatureCmd.Damage(ctx, play.Target!, card.DynamicVars.Damage.BaseValue,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, card);
                await PowerCmd.Apply<WeakPower>(play.Target!, card.DynamicVars["WeakPower"].BaseValue,
                    card.Owner.Creature, card);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}"); }
        }

        public static bool HasEntryPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        public static bool GetLocStringsWithPrefixPrefix(ref IReadOnlyList<LocString> __result)
        {
            __result = new List<LocString>();
            return false;
        }
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = _loc.Monster(monsterKey);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    #endregion
}
