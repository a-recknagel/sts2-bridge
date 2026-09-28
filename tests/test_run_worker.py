"""One seed crosses two rooms in one native RunState, including the first room's rewards."""

import json
import unittest

from sts2bridge.worker import BINARY, CombatWorker, WorkerError


@unittest.skipUnless(BINARY.is_file(), "build the pinned combat substrate first")
class RunWorkerTests(unittest.TestCase):
    def play_first_room(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "RUNMILESTONE", unlocks="none")
            identity = state["run_state_identity"]
            self.assertEqual(state["boundary"], "awaiting_map")
            self.assertEqual(state["obs"]["room"], "MapRoom")
            first_map_action = state["legal"][0]
            with self.assertRaises(WorkerError):
                worker.run_step({"type": "map", "col": 255, "row": 255})
            self.assertEqual(worker.run_observe()["obs"]["floor"], 0)

            state = worker.run_step(first_map_action)
            self.assertEqual(state["boundary"], "combat")
            self.assertEqual(state["run_state_identity"], identity)
            self.assertEqual(state["combat"]["boundary"], "awaiting_input")
            combat_hashes = [state["combat"]["state_hash"]]
            for _ in range(100):
                if state["boundary"] != "combat":
                    break
                combat = state["combat"] or worker.run_observe()["combat"]
                if combat["boundary"] == "awaiting_choice":
                    action = combat["legal"][0]
                else:
                    action = next((a for a in combat["legal"] if a["type"] == "play"), {"type": "end_turn"})
                state = worker.run_step(action)
                self.assertEqual(state["run_state_identity"], identity)
                if state["combat"]:
                    combat_hashes.append(state["combat"]["state_hash"])
            self.assertEqual(state["boundary"], "awaiting_rewards")
            self.assertEqual(state["obs"]["room"], "CombatRoom")
            self.assertEqual(state["obs"]["floor"], 1)  # the act's starting point, a fight on a fresh profile

            gold_before = state["obs"]["gold"]
            deck_before = len(state["obs"]["deck"])
            rewards = state["rewards"]
            gold = next(r for r in rewards if r["type"] == "GoldReward")
            card = next(r for r in rewards if r["type"] == "CardReward")
            chosen_card = card["cards"][0]
            state = worker.run_step({"type": "take_reward", "index": gold["index"]})
            self.assertGreater(state["obs"]["gold"], gold_before)
            state = worker.run_step({"type": "take_reward", "index": card["index"], "card": 0})
            self.assertEqual(state["boundary"], "awaiting_proceed")
            self.assertEqual(len(state["obs"]["deck"]), deck_before + 1)
            self.assertIn(chosen_card, state["obs"]["deck"])
            state = worker.run_step({"type": "proceed"})
            self.assertEqual(state["boundary"], "awaiting_map")
            self.assertTrue(state["legal"])
            self.assertEqual(state["run_state_identity"], identity)
            next_map = state["legal"][0]
            state = worker.run_step(next_map)
            self.assertEqual(state["boundary"], "combat")
            self.assertEqual(state["run_state_identity"], identity)
            self.assertEqual(state["obs"]["floor"], 2)
            self.assertIn(chosen_card, state["obs"]["deck"])
            trace = [json.loads(line) for line in worker.run_trace_path.read_text().splitlines()]
            self.assertEqual(len(trace), state["decision"] + 2)  # start plus the refused map input
            self.assertEqual(trace[0]["action"]["seed"], "RUNMILESTONE")
            self.assertEqual(trace[0]["action"]["type"], "start_run")
            self.assertEqual(trace[-1]["state"]["obs"], state["obs"])
            return combat_hashes, state["obs"]

    def test_two_rooms_with_native_rewards_and_repeatability(self):
        first_hashes, first_state = self.play_first_room()
        second_hashes, second_state = self.play_first_room()
        self.assertEqual(first_hashes, second_hashes)
        self.assertEqual(first_state, second_state)

    def test_unlocked_start_exposes_neow_and_its_rewards(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "RUNMILESTONE")
            identity = state["run_state_identity"]
            self.assertEqual(state["boundary"], "event")
            self.assertTrue(any("NEOW" in choice["key"] for choice in state["legal"]))
            self.assertEqual(state["model_id_hash"], 1568834832)
            state = worker.run_step(state["legal"][0])
            self.assertEqual(state["boundary"], "awaiting_rewards")
            card = next(r for r in state["rewards"] if r["type"] == "CardReward")
            potion = next(r for r in state["rewards"] if r["type"] == "PotionReward")
            state = worker.run_step({"type": "take_reward", "index": card["index"], "card": 0})
            state = worker.run_step({"type": "take_reward", "index": potion["index"]})
            self.assertEqual(state["boundary"], "awaiting_map")
            self.assertTrue(state["obs"]["potions"])
            state = worker.run_step(state["legal"][0])
            self.assertEqual(state["boundary"], "combat")
            self.assertEqual(state["run_state_identity"], identity)

    def test_neow_bundle_choice_preserves_chosen_pack(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "BUNDLE9")
            choice = next(a for a in state["legal"] if "SCROLL_BOXES" in a["key"])
            state = worker.run_step(choice)
            self.assertEqual(state["boundary"], "awaiting_bundle")
            self.assertEqual(len(state["bundle_choice"]["bundles"]), 2)
            deck_before = len(state["obs"]["deck"])
            chosen = state["bundle_choice"]["bundles"][1]["cards"]
            state = worker.run_step({"type": "bundle", "index": 1})
            self.assertEqual(state["boundary"], "awaiting_map")
            self.assertEqual(len(state["obs"]["deck"]), deck_before + len(chosen))
            for card in chosen:
                self.assertIn(card, state["obs"]["deck"])

    def test_shop_purchase_uses_same_run(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "SHOPSEED2", unlocks="none")
            identity = state["run_state_identity"]
            # Walk the map towards the first shop it offers, attacking through fights and skipping rewards.
            for _ in range(400):
                boundary = state["boundary"]
                if boundary in ("shop", "terminal"):
                    break
                if boundary == "awaiting_map":
                    action = next((a for a in state["legal"] if a["point_type"] == "Shop"), state["legal"][0])
                elif boundary == "combat":
                    combat = state["combat"] or worker.run_observe()["combat"]
                    if combat["boundary"] == "awaiting_choice":
                        action = combat["legal"][0] if combat["legal"] else {
                            "type": "choose", "picks": list(range(combat["choice"]["min"]))}
                    else:
                        plays = [a for a in combat["legal"] if a["type"] == "play"]
                        attacks = [a for a in plays if combat["obs"]["hand"][a["hand"]]["type"] == "Attack"]
                        action = (attacks or plays or [{"type": "end_turn"}])[0]
                elif boundary == "awaiting_rewards":
                    action = next((a for a in state["legal"] if a["type"] == "skip_rewards"), state["legal"][0])
                elif boundary == "awaiting_proceed":
                    action = {"type": "proceed"}
                elif boundary == "treasure":
                    action = {"type": "open_chest"}
                elif boundary == "awaiting_room_choice" and not state["legal"]:
                    action = {"type": "choose", "picks": list(range(state["choice"]["min"]))}
                else:
                    action = state["legal"][0]
                state = worker.run_step(action)
            self.assertEqual(state["boundary"], "shop")
            purchase = next(a for a in state["legal"] if a["type"] == "buy" and a["item_type"] == "MerchantCardEntry")
            card_id = state["shop"][purchase["index"]]["id"]
            gold_before = state["obs"]["gold"]
            state = worker.run_step(purchase)
            self.assertLess(state["obs"]["gold"], gold_before)
            self.assertIn(card_id, state["obs"]["deck"])
            state = worker.run_step({"type": "leave_shop"})
            self.assertEqual(state["boundary"], "awaiting_map")
            self.assertEqual(state["run_state_identity"], identity)

    def test_run_reaches_death_across_event_treasure_and_rest(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "RUNMILESTONE13", unlocks="none")
            identity = state["run_state_identity"]
            seen = set()
            for _ in range(400):
                boundary = state["boundary"]
                seen.add(boundary)
                self.assertEqual(state["run_state_identity"], identity)
                if boundary == "terminal":
                    break
                if boundary == "awaiting_map":
                    action = state["legal"][0]
                elif boundary == "combat":
                    combat = state["combat"] or worker.run_observe()["combat"]
                    if combat["boundary"] == "awaiting_choice":
                        action = combat["legal"][0]
                    else:
                        plays = [a for a in combat["legal"] if a["type"] == "play"]
                        attacks = [a for a in plays if combat["obs"]["hand"][a["hand"]]["type"] == "Attack"]
                        action = (attacks or plays or [{"type": "end_turn"}])[0]
                elif boundary == "awaiting_rewards":
                    action = next((a for a in state["legal"] if a["type"] == "take_reward" and
                                   (state["rewards"][a["index"]]["type"] == "GoldReward" or
                                    (state["rewards"][a["index"]]["type"] == "CardReward" and a.get("card") == 0))),
                                  {"type": "skip_rewards"})
                elif boundary == "awaiting_proceed":
                    action = {"type": "proceed"}
                elif boundary in ("event", "rest", "treasure_relic"):
                    action = state["legal"][0]
                elif boundary == "treasure":
                    action = {"type": "open_chest"}
                elif boundary == "awaiting_room_choice":
                    action = state["legal"][0]
                else:
                    self.fail(f"unexpected boundary {boundary}")
                state = worker.run_step(action)
            self.assertEqual(state["boundary"], "terminal")
            self.assertEqual(state["obs"]["hp"], 0)
            self.assertTrue({"event", "treasure", "treasure_relic", "rest", "awaiting_room_choice"} <= seen)

    @unittest.skip("ACTSEARCH28 beat the act 1 boss only on the old map, which skipped the act's starting point; "
                   "no seed in 400 wins it with this naive policy now. Needs a new seed or a stronger policy.")
    def test_boss_win_enters_next_act_without_rebuilding_state(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "ACTSEARCH28", unlocks="none")
            identity = state["run_state_identity"]
            entered_boss = False
            treasure_relic_granted = False
            for _ in range(400):
                if state["obs"]["act"] == 1 or state["boundary"] == "terminal":
                    break
                boundary = state["boundary"]
                if boundary == "awaiting_map":
                    priority = {"Treasure": 0, "Shop": 1, "Unknown": 2,
                                "RestSite": 3, "Monster": 4, "Elite": 9, "Boss": 10}
                    if state["obs"]["hp"] < state["obs"]["max_hp"] * 0.6:
                        priority["RestSite"] = -1
                    action = min(state["legal"], key=lambda a: priority.get(a["point_type"], 5))
                    entered_boss |= action["point_type"] == "Boss"
                elif boundary == "combat":
                    combat = state["combat"] or worker.run_observe()["combat"]
                    if combat["boundary"] == "awaiting_choice":
                        action = combat["legal"][0] if combat["legal"] else {
                            "type": "choose", "picks": list(range(combat["choice"]["min"]))}
                    else:
                        plays = [a for a in combat["legal"] if a["type"] == "play"]
                        attacks = [a for a in plays if combat["obs"]["hand"][a["hand"]]["type"] == "Attack"]
                        potions = [a for a in combat["legal"] if a["type"] == "potion"] if state["obs"]["hp"] < 25 else []
                        action = (potions or attacks or plays or [{"type": "end_turn"}])[0]
                elif boundary == "awaiting_rewards":
                    priority = {"RelicReward": 0, "GoldReward": 1, "PotionReward": 2, "CardReward": 3}
                    available = [a for a in state["legal"] if a["type"] == "take_reward" and a.get("card") in (None, 0)]
                    action = min(available, key=lambda a: priority.get(state["rewards"][a["index"]]["type"], 9)) \
                        if available else {"type": "skip_rewards"}
                elif boundary == "awaiting_proceed":
                    action = {"type": "proceed"}
                elif boundary in ("event", "treasure_relic", "awaiting_bundle"):
                    action = state["legal"][0]
                elif boundary == "rest":
                    action = next((a for a in state["legal"] if a["id"] == "HEAL"), state["legal"][0])
                elif boundary == "shop":
                    action = {"type": "leave_shop"}
                elif boundary == "treasure":
                    action = {"type": "open_chest"}
                elif boundary == "awaiting_room_choice":
                    action = state["legal"][0] if state["legal"] else {
                        "type": "choose", "picks": list(range(state["choice"]["min"]))}
                else:
                    self.fail(f"unexpected boundary {boundary}")
                state = worker.run_step(action)
                self.assertEqual(state["run_state_identity"], identity)
                if action["type"] == "pick_relic" and action["index"] is not None:
                    relic_ids = {f"{r['id']['Category']}.{r['id']['Entry']}" for r in state["obs"]["relics"]}
                    self.assertIn(action["id"], relic_ids)
                    treasure_relic_granted = True
            self.assertTrue(entered_boss)
            self.assertTrue(treasure_relic_granted)
            self.assertEqual(state["obs"]["act"], 1)
            self.assertEqual(state["boundary"], "awaiting_map")
            state = worker.run_step(state["legal"][0])
            self.assertEqual(state["boundary"], "combat")
            self.assertEqual(state["run_state_identity"], identity)

    def test_combat_snapshot_reenters_the_same_fight_in_another_worker(self):
        with CombatWorker() as run, CombatWorker() as copy:
            state = run.start_run("CHARACTER.IRONCLAD", "SNAP1", ascension=10)
            while state["boundary"] != "combat":
                skip = [a for a in state["legal"] if a["type"] == "skip_rewards"]
                state = run.run_step((skip or state["legal"])[0])
            snapshot = run.run_combat_snapshot(run.workdir / "fight.mcr")
            fight = copy.load(snapshot["path"])
            self.assertEqual(fight["state_hash"], state["combat"]["state_hash"])
            steps = 0
            while state["boundary"] == "combat":
                c = state["combat"]
                if c["boundary"] == "awaiting_choice":
                    action = c["legal"][0] if c["legal"] else {"type": "choose", "picks": list(range(c["choice"]["min"]))}
                else:
                    action = next((a for a in c["legal"] if a["type"] == "play"), {"type": "end_turn"})
                state = run.run_step(action)
                fight = copy.step(action)
                self.assertEqual(fight["state_hash"], state["combat"]["state_hash"], f"step {steps}")
                steps += 1
            self.assertEqual(fight["boundary"], "terminal")
            self.assertEqual(fight["obs"]["player"]["hp"], state["combat"]["obs"]["player"]["hp"])
            self.assertGreater(steps, 5)
            with self.assertRaises(WorkerError):
                run.run_combat_snapshot(run.workdir / "no-fight.mcr")

    def test_a_reward_can_offer_a_nested_rewards_set(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "SWEEP40", ascension=10)
            state = worker.run_step(next(a for a in state["legal"] if "NEOWS_BONES" in a["key"]))
            kaleidoscope = next(r for r in state["rewards"] if r["id"] == "RELIC.KALEIDOSCOPE")
            state = worker.run_step({"type": "take_reward", "index": kaleidoscope["index"]})
            self.assertEqual(state["boundary"], "awaiting_rewards")
            self.assertEqual([r["type"] for r in state["rewards"]], ["CardReward", "CardReward"])
            chosen = state["rewards"][0]["cards"][0]
            state = worker.run_step({"type": "take_reward", "index": 0, "card": 0})
            state = worker.run_step({"type": "skip_rewards"})
            # Back on Neow's set: the relic that offered the inner set is taken, the other one is still there.
            self.assertEqual(state["boundary"], "awaiting_rewards")
            self.assertEqual([r["id"] for r in state["rewards"] if not r["selected"]], ["RELIC.NEW_LEAF"])
            self.assertIn(chosen, state["obs"]["deck"])
            relics = {f"{r['id']['Category']}.{r['id']['Entry']}" for r in state["obs"]["relics"]}
            self.assertIn("RELIC.KALEIDOSCOPE", relics)
            state = worker.run_step({"type": "take_reward", "index": 1})
            self.assertEqual(state["boundary"], "awaiting_room_choice")  # New Leaf transforms a card
            state = worker.run_step({"type": "choose", "picks": [0]})
            self.assertEqual(state["boundary"], "awaiting_map")

    def test_a_relic_reward_can_open_a_deck_choice(self):
        with CombatWorker() as worker:
            state = worker.start_run("CHARACTER.IRONCLAD", "SWEEP16", ascension=10)
            state = worker.run_step(next(a for a in state["legal"] if "NEOWS_BONES" in a["key"]))
            scissors = next(r for r in state["rewards"] if r["id"] == "RELIC.PRECISE_SCISSORS")
            deck = len(state["obs"]["deck"])
            state = worker.run_step({"type": "take_reward", "index": scissors["index"]})
            self.assertEqual(state["boundary"], "awaiting_room_choice")
            self.assertEqual((state["choice"]["min"], state["choice"]["max"]), (1, 1))
            removed = state["choice"]["options"][0]["id"]
            state = worker.run_step({"type": "choose", "picks": [0]})
            self.assertEqual(state["boundary"], "awaiting_rewards")
            self.assertEqual(len(state["obs"]["deck"]), deck - 1, removed)


if __name__ == "__main__":
    unittest.main()
