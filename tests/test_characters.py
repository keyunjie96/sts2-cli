"""Tests for all 5 characters."""
import pytest

CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


class TestCharacterMechanics:
    def test_defect_has_orbs(self, game):
        state = game.start(character="Defect", seed="dm1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "orbs" in state or "orb_slots" in state

    def test_regent_has_stars(self, game):
        state = game.start(character="Regent", seed="dm2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "stars" in state

    def test_necrobinder_has_osty(self, game):
        state = game.start(character="Necrobinder", seed="dm3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "osty" in state
        assert "hp" in state["osty"]
        assert "alive" in state["osty"]


class TestFullRun:
    @pytest.mark.parametrize("character", CHARACTERS)
    @pytest.mark.slow
    def test_full_run(self, game, character):
        state = game.start(character=character, seed=f"full_{character.lower()}")
        steps = 0
        while steps < 2000:
            dec = state.get("decision", "")
            if dec == "game_over":
                assert "victory" in state
                return
            if state.get("type") == "error":
                state = game.act("proceed")
            elif dec == "combat_play":
                state = game.auto_combat(state)
            elif dec == "map_select":
                state = game.act("select_map_node",
                                 col=state["choices"][0]["col"],
                                 row=state["choices"][0]["row"])
            elif dec == "event_choice":
                opts = [o for o in state["options"] if not o.get("is_locked")]
                state = game.act("choose_option", option_index=opts[0]["index"]) if opts else game.act("leave_room")
            elif dec == "card_reward":
                state = game.act("skip_card_reward")
            elif dec == "bundle_select":
                state = game.act("select_bundle", bundle_index=0)
            elif dec == "card_select":
                if state.get("min_select", 0) == 0:
                    state = game.act("skip_select")
                else:
                    state = game.act("select_cards", indices="0")
            elif dec == "rest_site":
                opts = [o for o in state["options"] if o.get("is_enabled")]
                state = game.act("choose_option", option_index=opts[0]["index"])
            elif dec == "shop":
                state = game.act("leave_room")
            else:
                state = game.act("proceed")
            steps += 1
        pytest.fail(f"{character} did not finish in {steps} steps")
