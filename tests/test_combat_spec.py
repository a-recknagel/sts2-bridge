"""Combats started from a spec: a character, deck, relics, potions and HP against any encounter.

    python3 -m unittest research.combat_parity.test_combat_spec -v

The anchor is the recorded Insatiable fight. Its run's history file (fixtures/7TA07BQT5BSJ.run) turned into a
spec must start the same fight the recording loads, in everything a shuffle does not decide. Past that: every
encounter in the build starts and plays out, a spec and seed always give the same fight, and a spec naming
something this build does not have is refused. Needs ``research/combat_parity/setup.sh``; without it the worker
tests skip.
"""

import json
import random
import shutil
import subprocess
import unittest
from collections import Counter

from .combat_worker import PROJECT, CombatWorker, WorkerError, spec_from_run
from .test_recorded_combat import CLI_LIB, HERE, MCR, save_dir_snapshot

RUN = HERE / "fixtures" / "7TA07BQT5BSJ.run"
INSATIABLE = "ENCOUNTER.THE_INSATIABLE_BOSS"

# Logged by the game's ActionQueueSet when the last enemy dies inside the end-turn action itself (an end-of-turn
# orb passive, say): the combat ends and clears the queues before EndPlayerTurnAction finishes. The fight is over
# and its terminal state is right; the log line is the game's own.
BENIGN_GAME_ERRORS = ("Tried to pop action EndPlayerTurnAction",)


def run_fixture():
    return json.loads(RUN.read_text())


def play_randomly(worker, state, rng, max_steps=300):
    """Seeded random legal inputs until the fight ends. Returns the last state and every game-logged error."""
    errors = list(state["game_errors"])
    for _ in range(max_steps):
        if state["boundary"] == "terminal":
            break
        if state["boundary"] == "awaiting_choice" and not state["legal"]:  # multi-pick: any allowed subset
            choice = state["choice"]
            k = rng.randint(choice["min"], min(choice["max"], len(choice["options"])))
            action = {"type": "choose", "picks": rng.sample(range(len(choice["options"])), k)}
        else:
            action = rng.choice(state["legal"])
        state = worker.step(action)
        errors += state["game_errors"]
    return state, errors


def unexpected(errors):
    return [e for e in errors if not any(b in e for b in BENIGN_GAME_ERRORS)]


@unittest.skipUnless(CLI_LIB.exists() and shutil.which("dotnet"), "run research/combat_parity/setup.sh first")
class CombatSpecTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.saves_before = save_dir_snapshot()
        subprocess.run(["dotnet", "build", "-v", "q", str(PROJECT)], check=True, capture_output=True)
        cls.worker = CombatWorker()
        cls.catalog = cls.worker.catalog()

    @classmethod
    def tearDownClass(cls):
        cls.worker.close()
        shutil.rmtree(cls.worker.workdir, ignore_errors=True)

    def test_the_run_file_is_the_recording_s_player(self):
        # Two recorders of the same run, the history file and the .mcr, describe the same player in the same JSON.
        recorded = self.worker.tape(MCR)["initial"]["player"]
        spec = spec_from_run(run_fixture(), INSATIABLE, seed="ANY")["player"]
        self.assertEqual(spec["deck"], recorded["deck"])
        self.assertEqual(spec["relics"], recorded["relics"])
        self.assertEqual((spec["current_hp"], spec["max_hp"]), (recorded["current_hp"], recorded["max_hp"]))
        self.assertEqual(spec["max_potion_slot_count"], recorded["max_potion_slot_count"])
        self.assertEqual(spec["potions"], recorded.get("potions", []))

    def test_the_recorded_fight_rebuilt_from_its_run_file(self):
        recorded = self.worker.load(MCR)
        self.assertEqual(recorded["obs"]["encounter"], INSATIABLE)
        rebuilt = self.worker.start(spec_from_run(run_fixture(), INSATIABLE, seed="REBUILT"))
        a, b = recorded["obs"], rebuilt["obs"]
        # Everything turn 1 shows that the shuffle does not decide: HP, Anchor's block, Plating, Cracked Core's
        # orb, the relic bar, Festive Popper's damage on the boss, its intent, and the deck across hand and draw.
        for key in ("turn", "encounter"):
            self.assertEqual(b[key], a[key], key)
        for key in ("hp", "max_hp", "block", "energy", "max_energy", "powers", "orbs", "potions", "relics"):
            self.assertEqual(b["player"][key], a["player"][key], key)
        self.assertEqual(b["enemies"], a["enemies"])
        deck = lambda o: Counter(c["id"] for c in o["hand"] + o["draw"])
        self.assertEqual(deck(b), deck(a))
        self.assertEqual(sum(deck(b).values()), len(run_fixture()["players"][0]["deck"]))
        # The shuffle is the run's, so the hands differ, and so does the state.
        self.assertNotEqual(rebuilt["state_hash"], recorded["state_hash"])
        self.assertEqual(rebuilt["boundary"], "awaiting_input")
        self.assertEqual(rebuilt["game_errors"], [])

    def test_the_same_spec_and_seed_is_the_same_fight(self):
        spec = spec_from_run(run_fixture(), INSATIABLE, seed="SAME")

        def trace():
            state, hashes = self.worker.start(spec), []
            while state["boundary"] != "terminal" and len(hashes) < 60:
                hashes.append(state["state_hash"])
                plays = [a for a in state["legal"] if a["type"] == "play"]
                state = self.worker.step(plays[0] if plays else {"type": "end_turn"})
            return hashes + [state["state_hash"]]

        first = trace()
        self.assertGreater(len(first), 10)
        self.assertEqual(trace(), first)
        other = self.worker.start({**spec, "seed": "OTHER"})
        self.assertNotEqual(other["state_hash"], first[0])

    def test_a_deck_can_fight_any_encounter(self):
        # The loss's deck against an act-1 boss and a two-act-early elite: act and monsters follow the encounter.
        run = run_fixture()
        for encounter in ("ENCOUNTER.CEREMONIAL_BEAST_BOSS", "ENCOUNTER.BYGONE_EFFIGY_ELITE", "ENCOUNTER.SLIMES_WEAK"):
            with self.subTest(encounter=encounter):
                state = self.worker.start(spec_from_run(run, encounter, seed="ANY", current_hp=77))
                self.assertEqual(state["obs"]["encounter"], encounter)
                self.assertEqual(state["obs"]["player"]["hp"], 77)
                state, errors = play_randomly(self.worker, state, random.Random(encounter))
                self.assertEqual(state["boundary"], "terminal")
                self.assertEqual(unexpected(errors), [])

    def test_every_encounter_starts_and_plays_out(self):
        # A starter deck per character, round robin, at A10, under a seeded random policy. This is the sweep that
        # found the stub gaps (Kaiser Crab's SetVisible, the Phantasmal Gardeners' Mathf.Log).
        encounters = self.catalog["encounters"]
        characters = self.catalog["characters"]
        self.assertGreater(len(encounters), 80)
        failures, ends = [], Counter()
        for i, e in enumerate(encounters):
            spec = {"character": characters[i % len(characters)], "ascension": 10, "seed": f"SWEEP{i}", "encounter": e["id"]}
            try:
                state, errors = play_randomly(self.worker, self.worker.start(spec), random.Random(i))
                ends[state["boundary"]] += 1
                if unexpected(errors):
                    failures.append((e["id"], unexpected(errors)[:1]))
            except WorkerError as ex:
                failures.append((e["id"], str(ex)))
        self.assertEqual(failures, [])
        self.assertEqual(ends, Counter(terminal=len(encounters)))

    def test_a_soul_nexus_can_die(self):
        # SoulNexus.AfterDeath dereferences NCombatRoom.Instance without a null check; headless it is null, and before
        # Substrate's HeadlessGuards the turn loop died with the elite.
        spec = {"character": "CHARACTER.IRONCLAD", "ascension": 10, "seed": "NEXUS", "encounter": "ENCOUNTER.SOUL_NEXUS_ELITE",
                "player": {"current_hp": 999, "max_hp": 999, "deck": [{"id": "CARD.BLUDGEON", "current_upgrade_level": 1}] * 10}}
        state, errors = self.worker.start(spec), []
        while state["boundary"] != "terminal":
            plays = [a for a in state["legal"] if a["type"] == "play"]
            state = self.worker.step(plays[0] if plays else {"type": "end_turn"})
            errors += state["game_errors"]
        self.assertGreater(state["obs"]["player"]["hp"], 0)
        self.assertEqual(errors, [])

    def test_specs_the_build_cannot_honour_are_refused(self):
        base = spec_from_run(run_fixture(), INSATIABLE, seed="ANY")
        modded = json.loads(json.dumps(base))
        modded["player"]["relics"].append({"id": "RELIC.BALLS2-FOOTBALL"})
        modded["player"]["deck"][0]["enchantment"] = {"id": "ENCHANTMENT.NOT_A_THING", "amount": 1}
        cases = {
            "unknown ids": (modded, "RELIC.BALLS2-FOOTBALL"),
            "no seed": ({k: v for k, v in base.items() if k != "seed"}, "seed"),
            "unknown encounter": ({**base, "encounter": "ENCOUNTER.DOORMAKER_BOSS"}, "DOORMAKER_BOSS"),
            "net id": ({**base, "player": {**base["player"], "net_id": 7}}, "net_id"),
        }
        for name, (spec, needle) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(WorkerError, needle):
                self.worker.start(spec)
        self.assertIn("ENCHANTMENT.NOT_A_THING", self._error(modded))
        # A refused start leaves the worker usable.
        self.assertEqual(self.worker.start(base)["boundary"], "awaiting_input")

    def _error(self, spec):
        try:
            self.worker.start(spec)
        except WorkerError as ex:
            return str(ex)
        return ""

    def test_costs(self):
        spec = spec_from_run(run_fixture(), INSATIABLE, seed="COST")
        loads = [self.worker.start(spec)["ms"]["load"] for _ in range(5)]
        print(f"\nspec start {min(loads)}–{max(loads)} ms over {len(loads)} starts")
        self.assertLess(max(loads), 5_000)

    def test_real_saves_untouched(self):
        self.assertEqual(save_dir_snapshot(), self.saves_before)


if __name__ == "__main__":
    unittest.main()
