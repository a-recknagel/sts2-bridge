"""Many seeded runs in parallel processes, each with its own worker(s): the run adapters under a cheap policy.

    python3 -m research.combat_parity.sweep_runs --seeds 40 [--procs 12] [--sims 0] [--prefix SWEEP]

``--sims 0`` (the default) plays fights with the rollout policy alone, so runs end early but cover many rooms,
events and rewards quickly. Each run's result, with its trace path, goes to ``--out`` as one JSON line.
"""

import argparse
import collections
import json
from multiprocessing import Pool

from research.combat_parity.play_run import play


def one(job):
    seed, args = job
    out = f"{args.logs}/{seed}.json" if args.logs else None
    return play(seed, args.character, args.ascension, args.sims, args.codex_priors, out=out, verbose=False)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=40)
    ap.add_argument("--prefix", default="SWEEP")
    ap.add_argument("--procs", type=int, default=12)
    ap.add_argument("--sims", type=int, default=0)
    ap.add_argument("--character", default="CHARACTER.IRONCLAD")
    ap.add_argument("--ascension", type=int, default=10)
    ap.add_argument("--codex-priors")
    ap.add_argument("--out", default="sweep_runs.jsonl")
    ap.add_argument("--logs", help="directory for each run's full log as <seed>.json")
    args = ap.parse_args()
    jobs = [(f"{args.prefix}{i}", args) for i in range(args.seeds)]
    ends = collections.Counter()
    with Pool(args.procs) as pool, open(args.out, "w") as out:
        for r in pool.imap_unordered(one, jobs):
            r.pop("last", None)
            out.write(json.dumps(r) + "\n")
            out.flush()
            end = "victory" if r.get("victory") else r.get("error") or f"{r.get('boundary')} act {r.get('act', 0) + 1}"
            ends[end.split(" at ")[0][:90]] += 1
            print(f"{r['seed']:>10} {end[:140]}", flush=True)
    for end, n in ends.most_common():
        print(f"{n:4d}  {end}")
