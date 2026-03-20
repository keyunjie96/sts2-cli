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
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
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
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }
        // removed debug log

        // Execute inline immediately, then drain any nested posts
        _executing = true;
        try
        {
            d(state);
            // Drain any callbacks that were queued during execution
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        // Drain any remaining queued callbacks
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            _executing = true;
            try { cb(st); }
            finally { _executing = false; }
        }
    }
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

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null)
    {
        try
        {
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

            // Skip Neow event — it requires complex multi-page interaction.
            // Non-Neow events (which have simpler option structures) work with our loc patches.
            _runState.ExtraFields.StartedWithNeow = false;

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
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "pick_card":
                    return DoPickCard(player, args);
                case "skip_card":
                    return DoSkipCard(player);
                case "pick_reward":
                    return DoPickReward(player, args);
                case "skip_reward":
                    return DoSkipReward(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        // Enqueue and execute the move action
        var moveAction = new MoveToMapCoordAction(player, coord);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(moveAction);

        // Wait for the action executor to finish
        WaitForActionExecutor();

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

        // Determine target
        Creature? target = null;
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
        else
        {
            // Auto-target: if card targets single enemy, pick first alive
            var targetType = card.TargetType;
            if (targetType == TargetType.AnyEnemy)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                target = state?.Enemies.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsPlayPhase)
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsPlayPhase)
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsPlayPhase)
                    return DetectDecisionPoint();
            }
        }

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // EndTurn triggers async chain with Task.Yield() on ThreadPool.
        // We poll for state change to detect completion.
        var prevHp = player.Creature.CurrentHp;
        var prevEnemyHp = CombatManager.Instance.DebugOnlyGetState()?.Enemies?
            .Where(e => e != null && e.IsAlive).Sum(e => e.CurrentHp) ?? 0;
        var prevRound = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;

        PlayerCmd.EndTurn(player, canBackOut: false);
        _syncCtx.Pump();

        // Wait for turn transition — detect by state changes
        for (int i = 0; i < 200; i++)
        {
            _syncCtx.Pump();
            if (_turnStarted.IsSet || _combatEnded.IsSet) break;
            if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;

            var curRound = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
            if (curRound > prevRound) break;

            // Also detect state change by HP change (enemy turn happened)
            var curHp = player.Creature.CurrentHp;
            if (curHp != prevHp) { Thread.Sleep(50); _syncCtx.Pump(); break; }

            Thread.Sleep(5);
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPickCard(Player player, Dictionary<string, object?>? args)
    {
        // For now, auto-accept all rewards (TestMode handles this in RewardsSet.Offer)
        // This is used when we need to pick a specific card from card reward
        Log("Pick card - proceeding");
        return DoProceed(player);
    }

    private Dictionary<string, object?> DoSkipCard(Player player)
    {
        Log("Skip card - proceeding");
        return DoProceed(player);
    }

    private Dictionary<string, object?> DoPickReward(Player player, Dictionary<string, object?>? args)
    {
        Log("Pick reward - proceeding");
        return DoProceed(player);
    }

    private Dictionary<string, object?> DoSkipReward(Player player)
    {
        Log("Skip reward - proceeding");
        return DoProceed(player);
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        // For events, use EventSynchronizer
        var eventSync = RunManager.Instance.EventSynchronizer;
        var localEvent = eventSync?.GetLocalEvent();
        if (localEvent != null && !localEvent.IsFinished)
        {
            var options = localEvent.CurrentOptions;
            if (optionIndex >= 0 && optionIndex < options.Count)
            {
                var option = options[optionIndex];
                option.Chosen().GetAwaiter().GetResult();
            }
        }

        // For rest sites, use RestSiteSynchronizer
        var restSync = RunManager.Instance.RestSiteSynchronizer;
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            restSync.ChooseLocalOption(optionIndex).GetAwaiter().GetResult();
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
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
            // Wait for combat to initialize (StartCombatInternal runs via RunSafely)
            // In TestMode async resolves synchronously, but we need to pump the task system
            for (int i = 0; i < 50 && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead; i++)
            {
                WaitForActionExecutor();
                Thread.Sleep(10);
            }

            if (CombatManager.Instance.IsInProgress)
            {
                if (CombatManager.Instance.IsPlayPhase)
                {
                    return CombatPlayState(player);
                }
                // Enemy turn or between phases — wait more
                SpinWaitForCombatStable();
                if (CombatManager.Instance.IsPlayPhase)
                    return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress)
                {
                    return DetectPostCombatState(player, combatRoom);
                }
                // Still not in play phase — return combat state anyway
                return CombatPlayState(player);
            }
            else if (combatRoom.IsPreFinished)
            {
                // Pre-finished combat (e.g., loaded from save) — proceed
                return DetectPostCombatState(player, combatRoom);
            }
            else
            {
                // Combat not started yet — this shouldn't happen in TestMode
                // but try proceeding anyway
                Log($"Combat not in progress for {combatRoom.RoomType}, checking enemies");
                return DetectPostCombatState(player, combatRoom);
            }
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
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
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
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
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
            choices = currentPoint.Children
                .Select(child => new Dictionary<string, object?>
                {
                    ["col"] = (int)child.coord.col,
                    ["row"] = (int)child.coord.row,
                    ["type"] = child.PointType.ToString(),
                })
                .ToList();
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
            ["choices"] = choices,
            ["player"] = PlayerSummary(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        var hand = pcs?.Hand?.Cards?.Select((c, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["id"] = c.Id.ToString(),
            ["name"] = c.GetType().Name,
            ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
            ["type"] = c.Type.ToString(),
            ["can_play"] = c.CanPlay(out _, out _),
            ["target_type"] = c.TargetType.ToString(),
        }).ToList() ?? new();

        var enemies = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = e.Monster?.GetType().Name ?? "Unknown",
                ["hp"] = e.CurrentHp,
                ["max_hp"] = e.MaxHp,
                ["block"] = e.Block,
                ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
            }).ToList() ?? new();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
        };
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        // Combat has ended. In TestMode, RewardsSet.Offer auto-selects all rewards.
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");

        // Wait for async reward processing to complete
        _syncCtx.Pump();
        Thread.Sleep(100);
        _syncCtx.Pump();

        // Check if boss fight → act transition
        if (combatRoom.RoomType == RoomType.Boss)
        {
            Log("Boss defeated, entering next act");
            RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        // Proceed from rewards to map
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"ProceedFromTerminalRewardsScreen: {ex.Message}");
        }

        _syncCtx.Pump();
        WaitForActionExecutor();

        // Check if we're now at the map
        var room = _runState?.CurrentRoom;
        if (room is MapRoom)
        {
            return MapSelectState();
        }

        // If still in combat room, force transition to map
        Log("Force entering map room after combat");
        try
        {
            RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            for (int i = 0; i < 20; i++) { _syncCtx.Pump(); Thread.Sleep(20); }
        }
        catch (Exception ex)
        {
            Log($"EnterRoom(MapRoom) failed: {ex.Message}");
        }

        return MapSelectState();
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // If event is finished, proceed to map
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        var options = currentOptions
            .Select((opt, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["title"] = opt.Title?.LocEntryKey ?? opt.TextKey ?? $"option_{i}",
                ["description"] = opt.Description?.LocEntryKey ?? "",
                ["text_key"] = opt.TextKey,
                ["is_locked"] = opt.IsLocked,
            }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["event_name"] = localEvent.GetType().Name,
            ["description"] = localEvent.Description?.LocEntryKey ?? "",
            ["options"] = options,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        if (options == null || options.Count == 0)
        {
            // Rest site needs localization for options. Auto-heal and proceed.
            Log("Rest site has no options, auto-skipping to map");
            // Heal the player manually (rest site default heal is 30% max HP)
            var player = _runState!.Players[0];
            var healAmount = (int)(player.Creature.MaxHp * 0.3m);
            player.Creature.HealInternal(healAmount);
            Log($"Auto-healed for {healAmount} HP");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                for (int i = 0; i < 20; i++) { _syncCtx.Pump(); Thread.Sleep(20); }
            }
            catch (Exception ex) { Log($"Skip rest site failed: {ex.Message}"); }
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["name"] = opt.GetType().Name,
            ["description"] = opt.Description?.ToString() ?? opt.GetType().Name,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["options"] = optionList,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        // Auto-skip shop — force enter map room
        Log("Shop room — auto-leaving in headless mode");
        try
        {
            RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            for (int i = 0; i < 20; i++) { _syncCtx.Pump(); Thread.Sleep(20); }
        }
        catch (Exception ex)
        {
            Log($"Skip shop failed: {ex.Message}");
        }
        return MapSelectState();
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        // Auto-handle treasure room and proceed to map
        Log("Treasure room — auto-proceeding");
        try
        {
            treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
            _syncCtx.Pump();
            treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }

        try
        {
            RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
            for (int i = 0; i < 20; i++) { _syncCtx.Pump(); Thread.Sleep(20); }
        }
        catch (Exception ex) { Log($"Skip treasure failed: {ex.Message}"); }
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["victory"] = isVictory,
            ["player"] = PlayerSummary(player),
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion

    #region Helpers

    private void WaitForActionExecutor()
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
                // Pump while waiting for executor
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
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

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = player.Character?.GetType().Name ?? "Unknown",
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(r => r.GetType().Name).ToList() ?? new List<string>(),
            ["potions"] = player.Potions?.Select(p => p?.GetType().Name).Where(n => n != null).ToList() ?? new List<string?>(),
            ["deck_size"] = player.Deck?.Cards?.Count ?? 0,
        };
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

        // Task.Yield patch disabled — causes re-entrancy issues.
        // PatchTaskYield();

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
            _ => null
        };
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

            // Set _tables with all known table names
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();
            var tableNames = new[] {
                "achievements", "acts", "afflictions", "ancients", "ascension",
                "bestiary", "card_keywords", "card_library", "card_reward_ui",
                "card_selection", "cards", "characters", "combat_messages",
                "credits", "enchantments", "encounters", "epochs", "eras",
                "events", "ftues", "game_over_screen", "gameplay_ui",
                "inspect_relic_screen", "intents", "main_menu_ui", "map",
                "merchant_room", "modifiers", "monsters", "orbs", "potion_lab",
                "potions", "powers", "relic_collection", "relics", "rest_site_ui",
                "run_history", "settings_ui", "static_hover_tips", "stats_screen",
                "timeline", "vfx"
            };
            foreach (var name in tableNames)
                tables[name] = new LocTable(name, new Dictionary<string, string>());
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter so SmartFormat() doesn't crash
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (sfField != null)
                {
                    // Create a SmartFormatter using the SmartFormat library
                    var smartFormatType = sfField.FieldType; // SmartFormat.SmartFormatter
                    var sfInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(smartFormatType);
                    sfField.SetValue(instance, sfInstance);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.Message}"); }

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

            // Patch LocString.GetFormattedText to return LocEntryKey directly
            var getFormattedText = typeof(LocString).GetMethod("GetFormattedText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, Type.EmptyTypes, null);
            var gftPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetFormattedTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getFormattedText != null && gftPrefix != null)
            {
                try
                {
                    harmony.Patch(getFormattedText, new HarmonyMethod(gftPrefix));
                    Console.Error.WriteLine("[INFO] Patched LocString.GetFormattedText");
                }
                catch (Exception ex2)
                {
                    Console.Error.WriteLine($"[WARN] Failed to patch GetFormattedText: {ex2.Message}");
                }
            }

            // Patch LocString.GetRawText (instance, no params) to return LocEntryKey
            var getRawTextInst = typeof(LocString).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, Type.EmptyTypes, null);
            var grtInstPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextInstancePrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawTextInst != null && grtInstPrefix != null)
            {
                try
                {
                    harmony.Patch(getRawTextInst, new HarmonyMethod(grtInstPrefix));
                    Console.Error.WriteLine("[INFO] Patched LocString.GetRawText");
                }
                catch (Exception ex3)
                {
                    Console.Error.WriteLine($"[WARN] Failed to patch LocString.GetRawText: {ex3.Message}");
                }
            }
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
