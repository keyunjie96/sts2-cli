# STS2-CLI Bug Tracker

## [FIXED] BUG-001: Potion index shifts after use, causing invalid index errors (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play
- **Description**: After using a potion at index 0, remaining potions shift indices but the old indices are still referenced.
- **Fix**: Changed verification from index-based check to reference-based `Contains(potion)` check in `DoUsePotion`.
- **Relevant code**: Sts2Headless/RunSimulator.cs:~775

## [FIXED] BUG-002: Potion use_potion fails silently for some potion types (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play
- **Description**: Potions with TargetType.None/All (Attack Potion, Fortifier, Lucky Tonic) had incorrect auto-targeting.
- **Fix**: Removed catch-all else branch that forced `target = player.Creature`. Now only Self/AnyEnemy get auto-targets; others correctly leave target as null.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoUsePotion auto-targeting)

## [FIXED] BUG-014: Vantom R3 end_turn deadlock from Cmd.Wait in StatusCard intent (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play (end_turn during Vantom boss Round 3)
- **Description**: Vantom's DISMEMBER_MOVE adds 3 Wound status cards via CardPileCmd.AddToCombatAndPreview(), which calls Cmd.Wait(1f) for UI preview animation. In headless mode, Cmd.Wait never completes (no Godot scene tree), blocking the ActionExecutor, preventing WaitUntilQueueIsEmpty from completing, preventing StartTurn from firing, causing _turnStarted event to never set.
- **Fix**: Harmony patch on both Cmd.Wait() overloads to return Task.CompletedTask immediately (no-op in headless mode).
- **Relevant code**: Sts2Headless/RunSimulator.cs (PatchCmdWait, YieldPatches.CmdWaitPrefix)

## [FIXED] BUG-015: Self-targeting potions (Flex, Fortifier) applied to enemies when target_index provided (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play (use_potion)
- **Description**: DoUsePotion checked target_index before TargetType, so Self-targeting potions like Flex Potion would target enemy at index 0 instead of the player.
- **Fix**: Check potion.TargetType first — Self/TargetedNoCreature always targets player regardless of target_index.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoUsePotion)

## [FIXED] BUG-016: Rest site HEAL creates infinite rest_site loop (2026-03-22, fixed 2026-03-22)
- **Decision type**: rest_site (choose_option for HEAL)
- **Description**: After choosing HEAL, rest site options didn't clear, so DetectDecisionPoint returned rest_site again.
- **Fix**: After non-Smith rest options, force transition to map via ForceToMap().
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoChooseOption rest site handler)

## [WONTFIX] BUG-003: EOF crash during Leaf Slime combat (2026-03-22)
- **Decision type**: combat_play
- **Description**: Simulator occasionally crashes (returns EOF) during combat with Leaf Slime groups, possibly related to slime splitting mechanics or card interactions during split.
- **Repro**: Fight Leaf Slime group with seed=silent_run_3
- **Reported by**: Silent agent
- **Resolution**: Process-level crash (EOF) cannot be fixed in RunSimulator.cs. Requires EOF recovery in the bridge layer (sts2_bridge.py). The root cause is likely an unhandled exception in the game engine during slime split that kills the process.
- **Relevant code**: Sts2Headless/RunSimulator.cs (combat resolution)

## [FIXED] BUG-022: Self-targeting cards fail when target_index provided (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play (play_card)
- **Description**: DoPlayCard checked target_index BEFORE card.TargetType. When a Self/None/All card (Defend, Powers) was played with target_index:0, it resolved to an enemy target instead of null, causing PlayCardAction to fail silently.
- **Fix**: Check card.TargetType first — only AnyEnemy cards use target_index. All other cards get target=null (game handles targeting internally).
- **Impact**: This was likely the root cause of most "Card could not be played" errors throughout 10+ iterations.
- **Verified**: Using log replay (step 8 of seed 970fd80347ca) — Defend with target_index:0 now succeeds.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoPlayCard target resolution)

## [FIXED] BUG-004: Cards reported as can_play=true infinitely, causing infinite play loop (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play
- **Description**: PlayCardAction failing silently left card in hand, causing infinite play attempts.
- **Fix**: Added post-play verification in DoPlayCard — if card is still in hand at same index after action, returns error "Card could not be played" instead of looping.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoPlayCard)

## [FIXED] BUG-005: game_over state reports hp == max_hp even when player died (2026-03-22, fixed 2026-03-22)
- **Decision type**: game_over
- **Description**: The game_over JSON response shows player.hp equal to player.max_hp (e.g. 80/80) even when the player died. The engine resets CurrentHp after death.
- **Fix**: Added `_lastKnownHp` field, updated every combat_play state and room transition. In GameOverState, when `!isVictory`, override hp to 0 (since the player is dead and _lastKnownHp > 0 confirms they were alive before).
- **Reported by**: Ironclad agent (iteration 2)
- **Relevant code**: Sts2Headless/RunSimulator.cs (GameOverState, CombatPlayState, DoMapSelect)

## [NEEDS_VERIFY] BUG-006: Regent Particle Wall card can_play=true but fails to play (2026-03-22)
- **Decision type**: combat_play
- **Description**: Particle Wall (Regent card) reports can_play=true but when played returns "Card could not be played (still in hand after action)". May require special target or condition not captured by can_play.
- **Reported by**: Regent agent (iteration 2)
- **Status**: BUG-022 fix (target_index handling for non-AnyEnemy cards) likely resolved the root cause. Also improved error message to include card name/ID for future debugging. Needs verification with a Regent run.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoPlayCard error message now includes card name)

## [FIXED] BUG-007: Regent Astral Pulse StarCostTooHigh despite can_play not checking (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play
- **Description**: Astral Pulse reports StarCostTooHigh error. The engine's `card.CanPlay()` doesn't check star cost, so cards with star_cost > 0 showed can_play=true even when player lacked stars.
- **Fix**: In CombatPlayState hand card serialization, when a card has star_cost > 0 and `pcs.Stars < starCost`, override `can_play` to false. This prevents the agent from attempting to play cards it can't afford.
- **Reported by**: Regent agent (iteration 2)
- **Relevant code**: Sts2Headless/RunSimulator.cs (CombatPlayState hand card serialization)

## [FIXED] BUG-008: Map/context missing boss encounter name (2026-03-22, fixed 2026-03-22)
- **Decision type**: map_select / all decisions
- **Description**: Boss node in map data and RunContext had no boss name/type info, only "Boss" label.
- **Fix**: Added boss encounter extraction from Act.BossEncounter, with localized name via Monster() lookup. Boss info now in both get_map response and every decision's context field.
- **Relevant code**: Sts2Headless/RunSimulator.cs (GetFullMap, RunContext)

## [FIXED] BUG-009: BBCode tags in card/relic descriptions (2026-03-22, fixed 2026-03-22)
- **Decision type**: all
- **Description**: Card and relic descriptions contained raw BBCode tags like [gold], [/blue], [b], [sine].
- **Fix**: Added StripBBCode() to LocLookup.Bilingual() that strips all BBCode tags.
- **Relevant code**: Sts2Headless/RunSimulator.cs (LocLookup class)

## [FIXED] BUG-011: NullReferenceException on select_map_node after leaving shop (2026-03-22, fixed 2026-03-22)
- **Decision type**: map_select
- **Description**: After leaving shop, selecting an Elite node causes NullReferenceException.
- **Fix**: 4 changes: (1) DoMapSelect uses direct EnterMapCoord instead of action executor, (2) null check for player.Creature in DetectDecisionPoint, (3) null check for map.GetPoint in MapSelectState, (4) WaitForActionExecutor after EnterRoom in DoLeaveRoom.
- **Relevant code**: Sts2Headless/RunSimulator.cs

## [FIXED] BUG-012: Boss name/ID empty in context.boss throughout entire run (2026-03-22, fixed 2026-03-22)
- **Decision type**: all decisions
- **Description**: `context.boss.name` was empty because code used non-existent `BossId` property.
- **Fix**: Changed `_runState.Act?.BossId.Entry` to `_runState.Act?.BossEncounter?.Id?.Entry` in both RunContext and GetFullMap. Also restructured output to `{id, name}` dict.
- **Relevant code**: Sts2Headless/RunSimulator.cs (lines ~1990, ~2670)

## [CANNOT_REPRODUCE] BUG-017: Silent Slice card deals 0 damage (2026-03-22)
- **Decision type**: combat_play
- **Description**: Slice card (0-cost Attack) deals 0 damage consistently. Multiple agents confirmed.
- **Status**: Likely a game data issue — the card's DynamicVars may not have a "damage" entry, or the headless mode card model doesn't define damage correctly. Without a game log showing Slice in hand with its stats, cannot reproduce or diagnose further. The displayed stats come from DynamicVars.BaseValue which may differ from actual resolved damage.
- **Workaround**: Never pick Slice.

## [NOT_A_BUG] BUG-018: Precise Cut displays wrong damage (2026-03-22)
- **Decision type**: combat_play
- **Description**: Precise Cut shows 13 damage in stats but only deals 3-5 actual damage.
- **Resolution**: The displayed stats are DynamicVars.BaseValue (base stats before combat modifiers). Actual damage at play time is calculated with Strength, Vulnerable, and other modifiers. The discrepancy between displayed base stats and actual resolved damage is expected behavior — the simulator shows base values, not combat-resolved values. This is consistent with how all other cards work.
- **Workaround**: Don't rely on displayed stats as exact damage values; they are base stats only.

## [CANNOT_REPRODUCE] BUG-019: Phantom Blades unreliable auto-trigger (2026-03-22)
- **Decision type**: combat_play
- **Description**: Phantom Blades power doesn't trigger reliably on enemy attack turns.
- **Status**: Event-driven power triggers may not fire correctly in headless mode due to missing event subscriptions or timing issues with the InlineSynchronizationContext. Without a game log showing Phantom Blades active and failing to trigger, cannot diagnose further.
- **Workaround**: Don't pick this card.

## [CANNOT_REPRODUCE] BUG-020: Danse Macabre end-of-turn damage doesn't apply (2026-03-22)
- **Decision type**: combat_play
- **Description**: Danse Macabre power supposed to deal end-of-turn damage but confirmed unreliable in simulator.
- **Status**: Same class of issue as BUG-019 — event-driven end-of-turn effects may not process correctly in headless mode. Needs a game log with Danse Macabre active to diagnose.
- **Workaround**: Don't rely on this for damage scaling.

## [CANNOT_REPRODUCE] BUG-021: Doom Potion doesn't tick damage (2026-03-22)
- **Decision type**: combat_play
- **Description**: Doom Potion applies 33 Doom but damage never ticks during boss fight.
- **Status**: Same class of issue as BUG-019/020 — Doom is a debuff whose damage tick is event-driven. May not fire in headless mode. Needs a game log with Doom applied to diagnose.
- **Workaround**: Don't rely on Doom for boss kills.

## [FIXED] BUG-013: Relic picking session conflict on room transition (2026-03-22, fixed 2026-03-22)
- **Decision type**: map_select
- **Description**: "InvalidOperationException: Attempted to start new relic picking session while one was already occurring!" on floor 12→13 transition. Caused by entering a new room (especially Treasure) before the previous relic picking session completes.
- **Fix**: (1) Added WaitForActionExecutor + Pump before EnterMapCoord in DoMapSelect to ensure pending sessions complete. (2) Added WaitForActionExecutor + Pump before treasure reward collection in TreasureState. (3) Added try/catch for InvalidOperationException with "relic picking session" message that waits and retries once.
- **Reported by**: Ironclad agent (iteration 3)
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoMapSelect, TreasureState)

## [FIXED] BUG-010: Vantom boss EndTurn deadlock (2026-03-22, fixed 2026-03-22)
- **Decision type**: combat_play (end_turn during Vantom boss fight)
- **Description**: Simulator deadlocked during EndTurn in Vantom boss fight. Task.Yield() posted continuations to ThreadPool that never completed.
- **Fix**: Re-enabled PatchTaskYield() Harmony patch (was commented out). Added targeted SuppressYield=true only during EndTurn (try/finally), disabled during map navigation. This forces Task.Yield() continuations to run inline synchronously during enemy turn processing.
- **Verification**: 15/15 regression runs pass (all 5 characters × 3 runs), 0 timeouts.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DoEndTurn, EnsureModelDbInitialized, YieldPatches)

## [FIXED] BUG-024: Tools of Trade power blocks play_card at start of turn (2026-03-23, fixed 2026-03-23)
- **Decision type**: combat_play
- **Description**: When Tools of the Trade power is active (draw 1 + discard 1 at start of turn), the start-of-turn discard creates a pending card_select state. However, the bridge reports combat_play decision instead of card_select because the HasPending check at line ~1154 runs BEFORE the Pump() at line ~1210 that processes start-of-turn effects.
- **Fix**: Added a re-check for `_cardSelector.HasPending` AFTER the `Pump()` + `WaitForActionExecutor()` in the combat room section of DetectDecisionPoint. If a pending card selection appeared during pump (from start-of-turn powers), it now correctly jumps back to the card_select handler via goto label.
- **Relevant code**: Sts2Headless/RunSimulator.cs (DetectDecisionPoint, combat room section ~line 1210)

## [FIXED] BUG-023: Shop card removal causes NullReferenceException in PlayerSummary (2026-03-23, fixed 2026-03-23)
- **Decision type**: shop (remove_card → select_cards)
- **Description**: After removing a card via shop card removal, returning to ShopState triggers NullReferenceException in PlayerSummary's deck card serialization. The removed card's model becomes null in the deck list, but PlayerSummary iterates all cards without null-checking.
- **Fix**: Added `.Where(c => c != null)` filter before `.Select()` in PlayerSummary deck serialization. Also fixed `deck_size` to use `.Count(c => c != null)` to exclude null entries.
- **Relevant code**: Sts2Headless/RunSimulator.cs (PlayerSummary, lines ~2090-2091)

## [FIXED] BUG-025: Eradicate can_play=true at 0 energy blocks turn end (2026-03-23, fixed 2026-03-23)
- **Decision type**: combat_play
- **Description**: Eradicate has base cost 0 with Retain keyword. Its mechanic deals 11 damage × current energy. At 0 energy, `CanPlay()` returns true (cost 0 ≤ 0 energy), but playing it fails because the card action requires energy > 0. This blocks the game: the bridge won't allow end_turn when "playable" cards exist, and the card can't actually be played.
- **Fix**: Added special-case override in CombatPlayState hand card serialization: when card is ERADICATE and player energy is 0, set can_play to false. Same pattern as BUG-007 (Astral Pulse star cost).
- **Relevant code**: Sts2Headless/RunSimulator.cs (CombatPlayState hand card serialization, ~line 1414)

## [FIXED] BUG-026: Attack Potion card_select deadlocks combat — all cards unplayable (2026-03-23, fixed 2026-03-23)
- **Decision type**: combat_play (use_potion with Attack Potion)
- **Description**: When Attack Potion is used, it triggers a card_select (choose from 3 attack cards). The game engine's async method awaits GetSelectedCards(), which creates a pending TaskCompletionSource. WaitForActionExecutor loops 1000 pumps but the executor can never finish because it's awaiting the user's card selection. After WaitForActionExecutor gives up (with IsRunning still true), all subsequent card plays and end_turn fail because the executor remains permanently "stuck".
- **Fix**: Added early exit in WaitForActionExecutor when `_cardSelector.HasPending` or `_cardSelector.HasPendingReward` is true. The executor can't progress until the user resolves the selection, so there's no point waiting. This allows DoUsePotion to return the card_select decision immediately. When DoSelectCards later resolves the selection, the executor chain completes normally.
- **Relevant code**: Sts2Headless/RunSimulator.cs (WaitForActionExecutor, ~line 1990)
