"""How a run's play changed from one checkpoint to the next, on the recorded fight.

    research/combat_nn/.venv/bin/python -m research.combat_nn.shift runs/seeds/snapshots/*.pt
    research/combat_nn/.venv/bin/python -m research.combat_nn.shift runs/seeds/best.pt runs/seeds/ckpt.pt

Two views per checkpoint, in the order given:

- Its own greedy line on the recorded fight, one row per turn, with a * on turns that differ from the previous
  checkpoint's. Lines diverge as soon as one card differs, so later turns are different fights.
- A probe that doesn't diverge: the states along the pinned hand-played win, the same for every checkpoint. At
  each of its decisions, the probability the policy gives the move that won, and its own top pick where that
  differs. The per-turn number is the chance the policy plays that whole turn as the win did.

--json writes both views for a figure.
"""

import argparse
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from research.combat_nn.show import label, line, load, policy, short  # noqa: E402
from research.combat_parity.combat_worker import CombatWorker  # noqa: E402
from research.combat_parity.test_hand_win import WIN  # noqa: E402
from research.combat_parity.test_recorded_combat import MCR  # noqa: E402


def probe(worker, net, vocab, inputs):
    """[(turn, winning move, p(winning move), top pick, p(top pick))] along the hand-win."""
    state, out = worker.load(MCR), []
    for action in inputs:
        p, _ = policy(net, vocab, state)
        i = state["legal"].index(action)
        top = int(p.argmax())
        out.append((state["obs"]["turn"], label(state, action), float(p[i]), label(state, state["legal"][top]),
                    float(p[top])))
        state = worker.step(action)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ckpts", nargs="+")
    ap.add_argument("--json")
    args = ap.parse_args()
    inputs = json.loads(WIN.read_text())["inputs"]
    rows, prev = [], None
    with CombatWorker() as w:
        for path in args.ckpts:
            net, vocab, ck = load(path)
            turns, end = line(w, net, vocab)
            pr = probe(w, net, vocab, inputs)
            rows.append(dict(ckpt=path, it=ck.get("it"), episodes=ck.get("episodes"), turns=turns, end=end, probe=pr))

            name = Path(path).stem + (f" (it {ck['it']})" if ck.get("it") else "")
            print(f"\n=== {name}: {'WIN' if end['won'] else 'loss'}, hp {end['hp']}, boss {end['boss'] or 'dead'}")
            for k, t in enumerate(turns):
                cards = " · ".join(short(m) for m, _ in t["moves"] if m != "end")
                changed = prev is None or k >= len(prev) or [m for m, _ in prev[k]["moves"]] != [m for m, _ in t["moves"]]
                print(f"  {'*' if changed and prev is not None else ' '} t{t['turn']} hp {t['hp']:2d} boss {t['boss']:3d} | {cards}")
            prev = turns

            print("  hand-win probe, p(the whole turn as the win played it):")
            for turn in sorted({x[0] for x in pr}):
                moves = [x for x in pr if x[0] == turn]
                joint = math.prod(x[2] for x in moves)
                worst = min(moves, key=lambda x: x[2])
                note = f"  weakest: {short(worst[1])} {worst[2]:.2f}" + (
                    f", prefers {short(worst[3])} {worst[4]:.2f}" if worst[3] != worst[1] else "")
                print(f"    t{turn} {joint:6.3f}{note}")
    if args.json:
        Path(args.json).write_text(json.dumps(rows, indent=1))


if __name__ == "__main__":
    main()
