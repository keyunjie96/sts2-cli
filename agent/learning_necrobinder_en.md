# Necrobinder Strategy

> Updated 2026-03-22.

## Starter Deck
- Strike/Defend + Osty companion via Bound Phylactery relic

## Key Mechanics
- **Osty** = bone companion with HP/MaxHP/Block, fights alongside you
- Osty grows each turn (MaxHP increases); resets to 1 HP each new combat
- `osty` field: `{"hp": X, "max_hp": Y, "block": Z, "alive": bool}`
- **Osty dies turn 1-2 in multi-enemy fights** — MUST protect immediately
- Turn 1: ALWAYS play Bodyguard to protect Osty

## Card Pick Priorities
**Osty cards (high priority)**: Bodyguard, Unleash, Flatten, Sic 'Em, Fetch, Calcify

**General good cards**: Reave, Grave Warden, Defy, Wisp, Melancholy, Borrowed Time, Capture Spirit, Danse Macabre, Enfeebling Touch, Haunt, Deathbringer

## Combat Play Order

### 0-cost ALWAYS first
Powers, Wisp/Soul, Borrowed Time, Flatten, Block, Damage

### LETHAL MODE
Block only (p=10), Enfeebling (p=12), powers deprioritized (p=200)

### Normal Priority
1. Powers (p=15+cost)
2. Bodyguard turn 1-2 (p=16-18, CRITICAL)
3. Enfeebling Touch when incoming > 10 (p=19)
4. Block when incoming > 15 or HP < 50% (p=22)
5. Bodyguard later turns (p=28)
6. Unleash when Osty alive (p=32)
7. Attacks by damage (p=40-dmg)

## Targeting
- Fogmog: target boss, ignore Eye With Teeth (respawns)
- Kill shot: target if HP <= card_dmg + 3
- Otherwise: highest threat first

## Calcify (Upgraded)
- +6 damage to ALL Osty attacks (Unleash, Flatten, Fetch, Sic 'Em)
- Bodyguard (+5 HP) then Unleash = 6+6(HP)+6(Calcify) = 18+ dmg
- Flatten after Unleash = FREE 12+6 = 18 additional
- **Prioritize getting Calcify early, upgrade first**

## Dangerous Enemies
- **Byrdonis**: Str 17→36→54→90, kill in 4-5 turns or die
- **Bygone Effigy** (127hp): 23 dmg/turn after sleep, NEVER fight < 60 HP
- **Phrog Parasite**: spawns 4 Wrigglers + Infection cards on death, AoE critical
- **Mawler** (72hp): 6+ turn sustained fight, save Osty for big Unleash
- **Shrinker Beetle** (+Fuzzy Wurm): kill Beetle first; Shrink reduces all attacks
- **Fogmog** (74hp): Eye respawns w/ Illusion; 10+ round fight drains 30+ HP

## Multi-Enemy Crisis (#1 cause of death)
- Slime+Flyconid: 40-60 HP loss. 4-slime: 50+ HP. Raiders: 30-60 HP.

## Kin Priest Boss (190hp + 2x58-59hp followers)
- Followers buff Priest Str each turn — killing them is critical but they have 58+ HP
- Necrobinder lacks AoE — can only single-target, making this boss very hard
- Need Calcify or other scaling to kill followers in 3-4 turns
- **Enter with 66 HP minimum** — expect 30+ damage before followers die
- Defy + Weak essential to reduce multi-hit attacks
- Need Calcify/Flatten by mid-Act 1, or Kin Priest is near-unwinnable

## Ceremonial Beast Boss (252hp)
- Plow damages Osty each turn — Osty dies quickly even with Bodyguard
- Ringing: can only play 1 card next turn — play highest-value card
- Str scales +2/turn — damage increases rapidly (18-20-22...)
- Need 60+ HP entry + sustained block + damage powers (Calcify, Haunt)
- 23 HP is certain death — confirmed
- Friendship relic's -2 Str severely cripples damage output
- **252hp requires sustained DPS** — starter deck Strikes/Unleash only do ~15 dmg/turn, need 17+ rounds
- **Flatten (Pressure) is ESSENTIAL** — 2-cost Osty 12dmg is massive recurring damage with BT/Wisp energy
- Turn 1 usually Buff (no attack) — immediately play Powers (Neurosurge, Calcify), go all-in
- **Power Potion triggers card_select** — use BEFORE combat starts or on non-attack turns only

## Neow Choice
- Best: Precarious Shears (remove 2) > Stone Humidifier (+5 maxHP/rest) > Nutritious Oyster (+maxHP) > Golden Pearl (150g)
- NEVER take Cursed Pearl — Greed curse dilutes deck

## Shop Strategy
- Remove Strike ALWAYS (even over buying cards)
- Buy healing potions at ANY price when HP < 50%. Priority: Bodyguard > Unleash > Reave > Grave Warden
- 300+ gold no good cards: buy ALL potions + relic

## Key Insights
- Defy is Ethereal — must play the turn drawn or it Exhausts
- Reave (9dmg + Soul) excellent early — damage + draw engine
- Avoid forced elites floors 4-6 without scaling or HP > 55
- 4 consecutive monster floors with no rest = death trap
- Poke scales with Osty HP — free 6+ dmg when Osty healthy
- Drain Power = strongest single-target (10 dmg + draw 2)
- Devour Life: Power Potion can offer this — heals on attack, excellent for boss
- **Eradicate is MULTI-HIT** — "Deal 11 dmg X times" where X = current energy. 3e = 33 dmg (3 hits). AMAZING vs Slippery bosses (strips 1 layer per hit)
- **Eradicate scales with energy** — Wisp (+1e), Tea Set relic (+2e turn 1) are Eradicate amplifiers. 5e = 55 dmg
- **Melancholy cost reduction** — Reduces by 1 each time ANYTHING dies (enemies, Osty, minions). Often free in multi-enemy fights
- **Haunt triggers on Soul play** — NOT end of turn. Need frequent Soul generation (Grave Warden, Reave produce Souls) for Haunt to deal damage
- **Vantom Boss (173hp, Slippery 9)** — Enter with 55+ HP. Eradicate is best damage card (multi-hit strips Slippery). Play Calcify + Haunt turn 1, then Eradicate nuke turns
