#!/usr/bin/env python3
"""
Validation script for the stuck-state fix.
Plays 10 seeds with Ironclad + 1 game per character to verify no regressions.
Each game runs with a per-game timeout to prevent indefinite hangs.
"""

import json
import subprocess
import sys
import os
import time
import signal

# Add python/ to path so we can import play_full_run
PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(PROJ_ROOT, "python"))
from play_full_run import play_run

CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]
PER_GAME_TIMEOUT = 120  # seconds


class TimeoutError(Exception):
    pass


def _timeout_handler(signum, frame):
    raise TimeoutError("Game timed out")


def play_with_timeout(seed, character, timeout=PER_GAME_TIMEOUT):
    """Play a game with a timeout. Returns result dict."""
    old_handler = signal.signal(signal.SIGALRM, _timeout_handler)
    signal.alarm(timeout)
    try:
        result = play_run(seed, character, verbose=False, log=True)
        result["character"] = character
        return result
    except TimeoutError:
        return {
            "seed": seed, "character": character,
            "victory": False, "timeout": True,
            "error": f"hard_timeout_{timeout}s"
        }
    except Exception as e:
        return {
            "seed": seed, "character": character,
            "victory": False, "error": str(e)
        }
    finally:
        signal.alarm(0)
        signal.signal(signal.SIGALRM, old_handler)


def classify(result):
    if not result:
        return "ERROR"
    if result.get("victory"):
        return "VICTORY"
    if result.get("timeout"):
        return "STUCK/TIMEOUT"
    if result.get("error"):
        return f"ERROR"
    steps = result.get("steps", 0)
    if steps and steps >= 500:
        return "STUCK/TIMEOUT"
    return "DEFEATED"


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
        t0 = time.time()
        result = play_with_timeout(seed, "Ironclad")
        result["phase"] = "multi-seed"
        elapsed = time.time() - t0
        all_results.append(result)
        status = classify(result)
        floor = result.get("floor", "?")
        hp = result.get("hp", "?")
        max_hp = result.get("max_hp", "?")
        print(f"  => {status} | floor={floor} hp={hp}/{max_hp} steps={result.get('steps', '?')} ({elapsed:.1f}s)")
        if result.get("error"):
            errors.append(f"Seed {seed}: {result['error']}")

    # --- Phase 2: 1 game per character ---
    print("\n" + "=" * 70)
    print("PHASE 2: 1 game per character (all 5 characters)")
    print("=" * 70)
    for char in CHARACTERS:
        seed = f"char_test_{char.lower()}"
        print(f"\n--- {char} (seed: {seed}) ---")
        t0 = time.time()
        result = play_with_timeout(seed, char)
        result["phase"] = "character-test"
        elapsed = time.time() - t0
        all_results.append(result)
        status = classify(result)
        floor = result.get("floor", "?")
        hp = result.get("hp", "?")
        max_hp = result.get("max_hp", "?")
        print(f"  => {status} | floor={floor} hp={hp}/{max_hp} steps={result.get('steps', '?')} ({elapsed:.1f}s)")
        if result.get("error"):
            errors.append(f"{char} ({seed}): {result['error']}")

    # --- Summary ---
    print_summary(all_results, errors)
    return all_results, errors


def print_summary(results, errors):
    print("\n" + "=" * 70)
    print("VALIDATION SUMMARY")
    print("=" * 70)

    # Detailed table
    hdr = f"{'#':<3} {'Phase':<14} {'Character':<12} {'Seed':<25} {'Status':<14} {'Floor':<6} {'HP':<10} {'Steps':<6}"
    print(f"\n{hdr}")
    print("-" * len(hdr))
    for i, r in enumerate(results, 1):
        status = classify(r)
        floor = str(r.get("floor", "?"))
        hp_str = f"{r.get('hp', '?')}/{r.get('max_hp', '?')}"
        steps = str(r.get("steps", "?"))
        char = r.get("character", "?")
        phase = r.get("phase", "?")
        seed = r.get("seed", "?")
        print(f"{i:<3} {phase:<14} {char:<12} {seed:<25} {status:<14} {floor:<6} {hp_str:<10} {steps:<6}")

    # Aggregate stats
    total = len(results)
    victories = sum(1 for r in results if r.get("victory"))
    defeated = sum(1 for r in results if classify(r) == "DEFEATED")
    stuck = sum(1 for r in results if "STUCK" in classify(r) or "TIMEOUT" in classify(r))
    errored = sum(1 for r in results if classify(r) == "ERROR")

    floors = [r.get("floor", 0) for r in results if r.get("floor")]
    avg_floor = sum(floors) / len(floors) if floors else 0

    print(f"\n{'Metric':<30} {'Value':<10}")
    print("-" * 40)
    print(f"{'Total games':<30} {total}")
    print(f"{'Victories':<30} {victories}")
    print(f"{'Defeated (normal)':<30} {defeated}")
    print(f"{'Stuck/Timeout':<30} {stuck}")
    print(f"{'Errors':<30} {errored}")
    print(f"{'Avg floor reached':<30} {avg_floor:.1f}")
    print(f"{'Completion rate':<30} {(victories + defeated) / total * 100:.0f}% ({victories + defeated}/{total})")

    # Character breakdown
    print(f"\nPer-character results (Phase 2):")
    print(f"{'Character':<12} {'Status':<14} {'Floor':<6} {'HP':<10}")
    print("-" * 45)
    for r in results:
        if r.get("phase") == "character-test":
            hp_str = f"{r.get('hp', '?')}/{r.get('max_hp', '?')}"
            print(f"{r.get('character','?'):<12} {classify(r):<14} {str(r.get('floor','?')):<6} {hp_str:<10}")

    if errors:
        print(f"\nERRORS/ISSUES DETECTED ({len(errors)}):")
        for e in errors:
            print(f"  - {e}")

    # Final verdict
    print("\n" + "=" * 70)
    if stuck == 0 and errored == 0:
        print("VERDICT: PASS -- No stuck states or errors detected")
    elif stuck > 0 and errored == 0:
        print(f"VERDICT: FAIL -- {stuck} game(s) got stuck/timed out")
    elif stuck == 0 and errored > 0:
        print(f"VERDICT: FAIL -- {errored} game(s) had errors")
    else:
        print(f"VERDICT: FAIL -- {stuck} stuck/timeout + {errored} errors")
    print("=" * 70)


if __name__ == "__main__":
    run_test_suite()
