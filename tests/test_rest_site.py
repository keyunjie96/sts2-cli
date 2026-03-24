"""Tests for rest site / campfire."""
import pytest


class TestRestSiteStructure:
    def test_rest_site_fields(self, game):
        state = game.start(seed="rs1")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        assert state["decision"] == "rest_site"
        for opt in state["options"]:
            assert "index" in opt
            assert "option_id" in opt
            assert "is_enabled" in opt

    def test_has_heal_and_smith(self, game):
        state = game.start(seed="rs2")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        ids = {o["option_id"] for o in state["options"]}
        assert "HEAL" in ids
        assert "SMITH" in ids


class TestRestSiteActions:
    def test_heal_restores_hp(self, game):
        state = game.start(seed="rsa1")
        game.skip_neow(state)
        game.set_player(hp=30, max_hp=80)
        state = game.enter_room("rest_site")
        heal = next((o for o in state["options"] if o["option_id"] == "HEAL" and o["is_enabled"]), None)
        assert heal, "HEAL not available"
        hp_before = state["player"]["hp"]
        state = game.act("choose_option", option_index=heal["index"])
        new_hp = state.get("player", {}).get("hp", hp_before)
        assert new_hp > hp_before

    def test_heal_caps_at_max(self, game):
        state = game.start(seed="rsa2")
        game.skip_neow(state)
        game.set_player(hp=79, max_hp=80)
        state = game.enter_room("rest_site")
        heal = next((o for o in state["options"] if o["option_id"] == "HEAL" and o["is_enabled"]), None)
        if not heal:
            pytest.skip("HEAL not available at near-full HP")
        state = game.act("choose_option", option_index=heal["index"])
        assert state.get("player", {}).get("hp", 0) <= 80

    def test_smith_triggers_card_select(self, game):
        state = game.start(seed="rsa3")
        game.skip_neow(state)
        state = game.enter_room("rest_site")
        smith = next((o for o in state["options"] if o["option_id"] == "SMITH" and o["is_enabled"]), None)
        assert smith, "SMITH not available"
        state = game.act("choose_option", option_index=smith["index"])
        assert state["decision"] == "card_select"
        assert len(state.get("cards", [])) > 0
