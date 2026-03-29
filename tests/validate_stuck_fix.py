#!/usr/bin/env python3
"""
Validation script for the stuck-state fix.
Plays 10 seeds with default character + 1 game per character to verify no regressions.
"""

import json
import subprocess
import sys
import os
import time
import random

# Add python/ to path so we can import play_full_run
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "python"))
from play_full_run import play_run

CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]

def run_test_suite():
    all_results = []
    errors = []

    # --- Phase 1: 10 seeds with Ironclad ---
    print("=" * 70)
    print("PHASE 1: 10 seeds with Ironclad")
    print("=" * 70)
    for i in range(1, 11):
        seed = f"validate_{i}"
        print(f"\n--- Seed {i}/10: {seed} ---")
        try:
            result = play_run(seed, "Ironclad", verbose=False, log=True)
            result["character"] = "Ironclad"
            result["phase"] = "multi-seed"
            all_results.append(result)
            status = classify(result)
            floor = result.get("floor", "?")
            hp = result.get("hp", "?")
            max_hp = result.get("max_hp", "?")
            print(f"  => {status} | floor={floor} hp={hp}/{max_hp} steps={result.get('steps', '?')}")
            if result.get("error"):
                errors.append(f"Seed {seed}: {result['error']}")
        except Exception as e:
            print(f"  => EXCEPTION: {e}")
            errors.append(f"Seed {seed}: EXCEPTION {e}")
            all_results.append({
                "seed": seed, "character": "Ironclad", "phase": "multi-seed",
                "victory": False, "error": str(e)
            })

    # --- Phase 2: 1 game per character ---
    print("\n" + "=" * 70)
    print("PHASE 2: 1 game per character (all 5 characters)")
    print("=" * 70)
    for char in CHARACTERS:
        seed = f"char_test_{char.lower()}"
        print(f"\n--- {char} (seed: {seed}) ---")
        try:
            result = play_run(seed, char, verbose=False, log=True)
            result["character"] = char
            result["phase"] = "character-test"
            all_results.append(result)
            status = classify(result)
            floor = result.get("floor", "?")
            hp = result.get("hp", "?")
            max_hp = result.get("max_hp", "?")
            print(f"  => {status} | floor={floor} hp={hp}/{max_hp} steps={result.get('steps', '?')}")
            if result.get("error"):
                errors.append(f"{char} ({seed}): {result['error']}")
        except Exception as e:
            print(f"  => EXCEPTION: {e}")
            errors.append(f"{char} ({seed}): EXCEPTION {e}")
            all_results.append({
                "seed": seed, "character": char, "phase": "character-test",
                "victory": False, "error": str(e)
            })

    # --- Summary ---
    print_summary(all_results, errors)
    return all_results, errors


def classify(result):
    if not result:
        return "ERROR"
    if result.get("victory"):
        return "VICTORY"
    if result.get("timeout"):
        return "STUCK/TIMEOUT"
    if result.get("error"):
        return f"ERROR({result['error'][:30]})"
    # Check if it got stuck (detected by stuck_count > 20 in play_full_run)
    # Those return without timeout flag but also without victory
    steps = result.get("steps", 0)
    if steps and steps < 500:
        return "DEFEATED"
    return "STUCK/TIMEOUT"


def print_summary(results, errors):
    print("\n" + "=" * 70)
    print("VALIDATION SUMMARY")
    print("=" * 70)

    # Detailed table
    print(f"\n{'#':<3} {'Phase':<14} {'Character':<12} {'Seed':<22} {'Status':<16} {'Floor':<6} {'HP':<10} {'Steps':<6}")
    print("-" * 95)
    for i, r in enumerate(results, 1):
        status = classify(r)
        floor = str(r.get("floor", "?"))
        hp_str = f"{r.get('hp', '?')}/{r.get('max_hp', '?')}"
        steps = str(r.get("steps", "?"))
        char = r.get("character", "?")
        phase = r.get("phase", "?")
        seed = r.get("seed", "?")
        print(f"{i:<3} {phase:<14} {char:<12} {seed:<22} {status:<16} {floor:<6} {hp_str:<10} {steps:<6}")

    # Aggregate stats
    total = len(results)
    victories = sum(1 for r in results if r.get("victory"))
    defeated = sum(1 for r in results if classify(r) == "DEFEATED")
    stuck = sum(1 for r in results if "STUCK" in classify(r) or "TIMEOUT" in classify(r))
    errored = sum(1 for r in results if "ERROR" in classify(r))

    floors = [r.get("floor", 0) for r in results if r.get("floor")]
    avg_floor = sum(floors) / len(floors) if floors else 0

    print(f"\n{'Metric':<25} {'Value':<10}")
    print("-" * 35)
    print(f"{'Total games':<25} {total}")
    print(f"{'Victories':<25} {victories}")
    print(f"{'Defeated (normal)':<25} {defeated}")
    print(f"{'Stuck/Timeout':<25} {stuck}")
    print(f"{'Errors':<25} {errored}")
    print(f"{'Avg floor reached':<25} {avg_floor:.1f}")
    print(f"{'Completion rate':<25} {(victories + defeated) / total * 100:.0f}% ({victories + defeated}/{total})")

    # Character breakdown
    print(f"\nPer-character results (Phase 2):")
    print(f"{'Character':<12} {'Status':<16} {'Floor':<6}")
    print("-" * 35)
    for r in results:
        if r.get("phase") == "character-test":
            print(f"{r.get('character','?'):<12} {classify(r):<16} {r.get('floor','?'):<6}")

    if errors:
        print(f"\nERRORS DETECTED ({len(errors)}):")
        for e in errors:
            print(f"  - {e}")

    # Final verdict
    print("\n" + "=" * 70)
    if stuck == 0 and errored == 0:
        print("VERDICT: PASS -- No stuck states or errors detected")
    elif stuck > 0:
        print(f"VERDICT: FAIL -- {stuck} game(s) got stuck/timed out")
    else:
        print(f"VERDICT: FAIL -- {errored} game(s) had errors")
    print("=" * 70)


if __name__ == "__main__":
    run_test_suite()
