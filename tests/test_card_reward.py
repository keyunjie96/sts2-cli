"""Tests for card rewards."""
import pytest


class TestCardReward:
    def test_card_reward_after_combat(self, game):
        state = game.start(seed="cr1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        # After combat we expect card_reward (or bundle_select, card_select)
        assert state["decision"] in ("card_reward", "bundle_select", "card_select", "map_select")

    def test_card_reward_structure(self, game):
        state = game.start(seed="cr2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        if state["decision"] != "card_reward":
            pytest.skip("No card_reward after this fight")
        assert len(state["cards"]) > 0
        for card in state["cards"]:
            assert isinstance(card["name"], str)
            assert "cost" in card
            assert "type" in card

    def test_select_card_adds_to_deck(self, game):
        state = game.start(seed="cr3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        if state["decision"] != "card_reward":
            pytest.skip("No card_reward")
        deck_before = state["player"]["deck_size"]
        state = game.act("select_card_reward", card_index=state["cards"][0]["index"])
        deck_after = state.get("player", {}).get("deck_size", deck_before)
        assert deck_after == deck_before + 1

    def test_skip_card(self, game):
        state = game.start(seed="cr4")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        if state["decision"] != "card_reward":
            pytest.skip("No card_reward")
        deck_before = state["player"]["deck_size"]
        state = game.act("skip_card_reward")
        deck_after = state.get("player", {}).get("deck_size", deck_before)
        assert deck_after == deck_before
