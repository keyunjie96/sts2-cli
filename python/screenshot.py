#!/usr/bin/env python3
"""Capture screenshots of key game states for social media / README."""

import subprocess
import json
import sys
import os
import time

DOTNET = os.path.expanduser("~/.dotnet-arm64/dotnet")
PROJECT = os.path.join(os.path.dirname(__file__), "..", "Sts2Headless", "Sts2Headless.csproj")

def start_sim():
    return subprocess.Popen(
        [DOTNET, "run", "--project", PROJECT, "--no-build"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )

def send(proc, cmd):
    proc.stdin.write(json.dumps(cmd) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline().strip()
        if not line:
            continue
        try:
            return json.loads(line)
        except json.JSONDecodeError:
            continue

def format_combat(state):
    """Format combat state for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    lines.append("  ⚔️  COMBAT  ⚔️")
    lines.append("=" * 60)

    # Enemies
    for e in d.get("enemies", []):
        intent = e.get("intent_text", "?")
        powers = ", ".join(f"{p['name']}({p['amount']})" for p in e.get("powers", []))
        hp_bar = f"[{'█' * (e['hp'] * 20 // e['max_hp'])}{'░' * (20 - e['hp'] * 20 // e['max_hp'])}]"
        lines.append(f"  👹 {e.get('name_en', e.get('name', '?')):<20} HP {e['hp']:>3}/{e['max_hp']:<3} {hp_bar}")
        if powers:
            lines.append(f"     Powers: {powers}")
        lines.append(f"     Intent: {intent}")

    lines.append("-" * 60)

    # Player
    p = d.get("player_creature", d.get("allies", [{}])[0] if d.get("allies") else {})
    hp = p.get("hp", 0)
    max_hp = p.get("max_hp", 1)
    block = p.get("block", 0)
    hp_bar = f"[{'█' * (hp * 20 // max_hp)}{'░' * (20 - hp * 20 // max_hp)}]"
    lines.append(f"  🛡️  You: HP {hp}/{max_hp} {hp_bar}  Block: {block}")
    lines.append(f"  ⚡ Energy: {d.get('energy', 0)}/{d.get('max_energy', 0)}")

    # Hand
    lines.append("")
    lines.append("  🃏 Hand:")
    for c in d.get("hand", []):
        name = c.get("name_en", c.get("name", "?"))
        cost = c.get("cost", 0)
        ctype = c.get("type", "?")
        lines.append(f"    [{c['index']}] {name:<25} Cost:{cost}  ({ctype})")

    lines.append(f"\n  📚 Draw: {d.get('draw_pile_count', 0)}  🗑️ Discard: {d.get('discard_pile_count', 0)}")
    lines.append("=" * 60)
    return "\n".join(lines)

def format_map(state):
    """Format map for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    lines.append("  🗺️  MAP  🗺️")
    lines.append("=" * 60)

    if "map" in d:
        m = d["map"]
        lines.append(f"  Act {m.get('act', '?')} — Floor {m.get('floor', '?')}")
        lines.append("")
        for node in m.get("available_nodes", []):
            room_icon = {"Monster": "👹", "Elite": "💀", "Event": "❓", "RestSite": "🏕️", "Merchant": "🛒", "Boss": "👑"}.get(node.get("room_type", ""), "❔")
            lines.append(f"  [{node.get('index', '?')}] {room_icon} {node.get('room_type_en', node.get('room_type', '?'))}")

    lines.append("=" * 60)
    return "\n".join(lines)

def format_reward(state):
    """Format card reward for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    lines.append("  🎁  CARD REWARD  🎁")
    lines.append("=" * 60)

    for c in d.get("cards", []):
        name = c.get("name_en", c.get("name", "?"))
        rarity = c.get("rarity", "?")
        ctype = c.get("type", "?")
        desc = c.get("description_en", c.get("description", ""))
        icon = {"Common": "⚪", "Uncommon": "🔵", "Rare": "🟡"}.get(rarity, "⚪")
        lines.append(f"  [{c.get('index', '?')}] {icon} {name:<25} ({ctype}, {rarity})")
        if desc:
            lines.append(f"      {desc}")

    lines.append(f"\n  [s] Skip")
    lines.append("=" * 60)
    return "\n".join(lines)

def format_event(state):
    """Format event for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    event_name = d.get("event_name_en", d.get("event_name", "Unknown Event"))
    lines.append(f"  ❓  EVENT: {event_name}  ❓")
    lines.append("=" * 60)

    for o in d.get("options", []):
        lines.append(f"  [{o.get('index', '?')}] {o.get('text_en', o.get('text', '?'))}")

    lines.append("=" * 60)
    return "\n".join(lines)

def format_rest(state):
    """Format rest site for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    lines.append("  🏕️  REST SITE  🏕️")
    lines.append("=" * 60)

    for o in d.get("options", []):
        lines.append(f"  [{o.get('index', '?')}] {o.get('description_en', o.get('description', '?'))}")

    lines.append("=" * 60)
    return "\n".join(lines)

def format_shop(state):
    """Format shop for screenshot."""
    d = state
    lines = []
    lines.append("=" * 60)
    lines.append("  🛒  SHOP  🛒")
    lines.append("=" * 60)
    lines.append(f"  💰 Gold: {d.get('gold', 0)}")
    lines.append("")

    for section in ["cards", "relics", "potions"]:
        items = d.get(section, [])
        if items:
            lines.append(f"  {section.upper()}:")
            for item in items:
                name = item.get("name_en", item.get("name", "?"))
                price = item.get("price", "?")
                lines.append(f"    {name:<30} 💰{price}")

    if d.get("card_removal_price"):
        lines.append(f"\n  Remove a card: 💰{d['card_removal_price']}")

    lines.append("=" * 60)
    return "\n".join(lines)

def main():
    """Run a game and capture screenshots at each decision point."""
    print("Building...")
    subprocess.run([DOTNET, "build", PROJECT, "-q"], check=True, capture_output=True)

    print("Starting simulator...")
    proc = start_sim()
    ready = proc.stdout.readline()
    print(f"Ready: {ready.strip()}")

    # Start a run with a known seed that gives variety
    state = send(proc, {"cmd": "start_run", "character": "Ironclad", "seed": "screenshot_seed_42", "ascension": 0})

    screenshots = {}
    actions_taken = 0
    max_actions = 200

    while actions_taken < max_actions:
        decision = state.get("decision", state.get("type", ""))

        if decision == "game_over":
            print(f"\n🏁 Game Over! Victory: {state.get('victory', False)}, Floor: {state.get('floor', '?')}")
            break

        # Capture screenshot if we haven't seen this type yet
        if decision == "combat_play" and "combat" not in screenshots:
            screenshots["combat"] = format_combat(state)
            print("\n📸 Captured: COMBAT")
            print(screenshots["combat"])

        elif decision == "map_select" and "map" not in screenshots:
            screenshots["map"] = format_map(state)
            print("\n📸 Captured: MAP")
            print(screenshots["map"])

        elif decision == "card_reward" and "reward" not in screenshots:
            screenshots["reward"] = format_reward(state)
            print("\n📸 Captured: CARD REWARD")
            print(screenshots["reward"])

        elif decision == "event_choice" and "event" not in screenshots:
            screenshots["event"] = format_event(state)
            print("\n📸 Captured: EVENT")
            print(screenshots["event"])

        elif decision == "rest_site" and "rest" not in screenshots:
            screenshots["rest"] = format_rest(state)
            print("\n📸 Captured: REST SITE")
            print(screenshots["rest"])

        elif decision == "shop" and "shop" not in screenshots:
            screenshots["shop"] = format_shop(state)
            print("\n📸 Captured: SHOP")
            print(screenshots["shop"])

        # Take simple auto-play action
        action_cmd = auto_action(state)
        if action_cmd is None:
            break

        state = send(proc, action_cmd)
        actions_taken += 1

        if actions_taken % 20 == 0:
            print(f"  ... {actions_taken} actions, captured: {list(screenshots.keys())}")

    proc.stdin.close()
    proc.wait(timeout=5)

    print(f"\n\n{'='*60}")
    print(f"  CAPTURED {len(screenshots)} SCREENSHOTS")
    print(f"  Types: {', '.join(screenshots.keys())}")
    print(f"{'='*60}")

    # Save all screenshots
    os.makedirs("docs/screenshots", exist_ok=True)
    for name, text in screenshots.items():
        path = f"docs/screenshots/{name}.txt"
        with open(path, "w") as f:
            f.write(text)
        print(f"  Saved: {path}")

def auto_action(state):
    """Simple auto-play: pick first available action."""
    decision = state.get("decision", state.get("type", ""))

    if decision == "map_select":
        nodes = state.get("map", {}).get("available_nodes", [])
        if nodes:
            n = nodes[0]
            return {"cmd": "action", "action": "select_map_node", "args": {"col": n.get("col", 0), "row": n.get("row", 0)}}

    elif decision == "combat_play":
        hand = state.get("hand", [])
        energy = state.get("energy", 0)
        enemies = state.get("enemies", [])

        # Play first affordable card
        for card in hand:
            if card.get("cost", 99) <= energy and card.get("playable", True):
                target = enemies[0].get("index", 0) if enemies else 0
                return {"cmd": "action", "action": "play_card", "args": {"card_index": card["index"], "target_index": target}}

        # End turn
        return {"cmd": "action", "action": "end_turn"}

    elif decision == "card_reward":
        return {"cmd": "action", "action": "skip_card_reward"}

    elif decision == "event_choice":
        options = state.get("options", [])
        if options:
            return {"cmd": "action", "action": "choose_event_option", "args": {"option_index": 0}}
        return {"cmd": "action", "action": "leave_room"}

    elif decision == "rest_site":
        return {"cmd": "action", "action": "choose_rest_option", "args": {"option_index": 0}}

    elif decision == "shop":
        return {"cmd": "action", "action": "leave_shop"}

    elif decision == "neow_blessing":
        options = state.get("options", [])
        if options:
            return {"cmd": "action", "action": "choose_neow_blessing", "args": {"option_index": 0}}

    elif decision == "boss_relic":
        return {"cmd": "action", "action": "choose_boss_relic", "args": {"relic_index": 0}}

    return None

if __name__ == "__main__":
    main()
