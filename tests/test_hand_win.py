"""The recorded fight won, one pass, from what a player can see: the line pinned as inputs and as cards.

    python3 -m unittest tests.test_hand_win -v
    python3 -m tests.test_hand_win --write   # regenerate the fixture's turns from its inputs

The fixture keeps the 34 worker inputs the win was played with, and beside them each turn as the cards played,
the HP both sides started it on and the boss's intent. Inputs are hand positions, which mean nothing without the
recorded seed; the turns are the line a person can read, and the test replays the inputs to check they still say
the same thing and still win. Needs ``./setup.sh``; without it the tests skip.
"""

import json
import shutil
import subprocess
import sys
import unittest

from sts2bridge.fixtures import MCR, WIN
from sts2bridge.worker import LIB, PROJECT, CombatWorker, card_name

from .test_recorded_combat import save_dir_snapshot


def play_line(worker, inputs):
    """Load the recorded fight, step every input, and return (turns, end, trace).

    turns is [{turn, hp, boss_hp, intent, played}], one per player turn; a choice is written after the card that
    opened it as "CARD -> PICK". trace is [(state before, input)]."""
    state = worker.load(MCR)
    turns, trace = [], []

    def open_turn(obs):
        boss = obs["enemies"][0]
        turns.append({"turn": obs["turn"], "hp": obs["player"]["hp"], "boss_hp": boss["hp"],
                      "intent": boss["intent"] and boss["intent"]["move"], "played": []})

    open_turn(state["obs"])
    for action in inputs:
        played = turns[-1]["played"]
        if action["type"] == "play":
            played.append(card_name(state["obs"]["hand"][action["hand"]]))
        elif action["type"] == "choose":
            picks = [card_name(state["choice"]["options"][i]) for i in action["picks"]]
            played[-1] += " -> " + ", ".join(picks)
        trace.append((state, action))
        state = worker.step(action)
        if action["type"] == "end_turn" and state["boundary"] != "terminal":
            open_turn(state["obs"])
    obs = state["obs"]
    end = {"boundary": state["boundary"], "hp": obs["player"]["hp"],
           "enemies_alive": sum(e["alive"] for e in obs["enemies"])}
    return turns, end, trace


@unittest.skipUnless(LIB.exists() and shutil.which("dotnet"), "run ./setup.sh first")
class HandWinTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.saves_before = save_dir_snapshot()
        subprocess.run(["dotnet", "build", "-v", "q", "-c", "Release", str(PROJECT)], check=True, capture_output=True)
        cls.fixture = json.loads(WIN.read_text())
        cls.worker = CombatWorker()
        cls.turns, cls.end, cls.trace = play_line(cls.worker, cls.fixture["inputs"])

    @classmethod
    def tearDownClass(cls):
        cls.worker.close()
        shutil.rmtree(cls.worker.workdir, ignore_errors=True)

    def test_the_inputs_still_win(self):
        self.assertEqual(self.end, self.fixture["end"])
        self.assertEqual(self.end, {"boundary": "terminal", "hp": 3, "enemies_alive": 0})

    def test_the_inputs_still_play_the_pinned_line(self):
        for replayed, pinned in zip(self.turns, self.fixture["turns"], strict=True):
            with self.subTest(turn=pinned["turn"]):
                self.assertEqual(replayed, pinned)

    def test_every_input_was_legal_where_it_was_played(self):
        for i, (before, action) in enumerate(self.trace):
            with self.subTest(input=i):
                self.assertIn(action, before["legal"])

    def test_the_observation_carries_what_we_give_it(self):
        # docs/what-we-give-it.md items 3, 4 and 9, checked where the line makes them matter.
        start, after_five = self.trace[0][0]["obs"], self.trace[5][0]["obs"]
        self.assertEqual(start["player"]["orb_capacity"], 3)
        self.assertEqual(after_five["player"]["orb_capacity"], 6)  # Modded +1, Capacitor +2
        self.assertEqual(start["this_turn"]["played"], [])
        self.assertEqual(sorted(c["id"] for c in start["this_turn"]["drawn"]), sorted(c["id"] for c in start["hand"]))
        self.assertEqual([c["id"] for c in after_five["this_turn"]["played"]],
                         ["CARD.BEAM_CELL", "CARD.BLOODLETTING", "CARD.CAPACITOR", "CARD.COOLHEADED", "CARD.MODDED"])
        piles = start["hand"] + start["draw"] + start["discard"] + start["exhaust"]
        self.assertEqual([(c["id"], c["enchantment"]) for c in piles if c["enchantment"]],
                         [("CARD.REFRACT", {"id": "ENCHANTMENT.SHARP", "amount": 2})])
        self.assertEqual(sorted(c["upgrades"] for c in piles if c["id"] == "CARD.STRIKE_DEFECT"), [0, 0, 1])

    def test_real_saves_untouched(self):
        self.assertEqual(save_dir_snapshot(), self.saves_before)


if __name__ == "__main__" and "--write" in sys.argv:
    fixture = json.loads(WIN.read_text())
    with CombatWorker() as w:
        fixture["turns"], fixture["end"], _ = play_line(w, fixture["inputs"])
    rows = lambda xs: "[\n  " + ",\n  ".join(json.dumps(x) for x in xs) + "\n ]"
    WIN.write_text("{\n" + ",\n".join(f" {json.dumps(k)}: {rows(v) if isinstance(v, list) else json.dumps(v)}"
                                       for k, v in fixture.items()) + "\n}\n")
