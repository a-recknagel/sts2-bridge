"""PPO on one fight: the Insatiable, with the deck, relics and HP of run 7TA07BQT5BSJ.

    .venv/bin/python -m agents.nn.train --name first
    .venv/bin/python -m agents.nn.train --name smoke --train-on recorded   # memorise it

Training fights are started from the run's history file with a fresh random seed each, so the shuffle and every
roll change and nothing can be learned about one recording. The recorded fight itself (the .mcr the user lost)
is held out, as are the EVAL-* seeds. An evaluator process plays the recorded fight greedily and sampled, and the
held-out seeds greedily, and reports win rates next to the training ones. `--train-on recorded` trains on the
recording instead, as a check that the pipeline can learn anything at all; a win there proves nothing else.

The reward is terminal: 1 for a win, and a loss is worth less by one of two measures (see `outcome`). Runs write log.jsonl, ckpt.pt, best.pt
and a snapshot per eval under agents/nn/runs/<name>/.
"""

import argparse
import json
import queue
import random
import time
from pathlib import Path

import torch
import torch.multiprocessing as mp

from sts2bridge import CombatWorker, spec_from_run
from sts2bridge.fixtures import MCR
from sts2bridge.fixtures import RUN as RUN_FILE

from .encode import Vocab, collate, encode
from .model import Net

HERE = Path(__file__).resolve().parent
RUN = json.loads(RUN_FILE.read_text())
ENCOUNTER = "ENCOUNTER.THE_INSATIABLE_BOSS"
MAX_TURNS = 30


# ---- the fight ----

def start(worker, seed):
    """seed None = the recorded fight, else a fresh fight from the run's history file."""
    return worker.load(MCR) if seed is None else worker.start(spec_from_run(RUN, ENCOUNTER, seed=seed))


REWARD = "damage"


def outcome(state, boss0):
    """A win is 1. A loss is worth half the fraction of the boss's HP taken ("damage"), or, with "survival", a
    quarter of that plus 0.04 for each turn lived. Damage alone settles on all-in play that dies on turn 5; the
    Sandpit clock stops survival from paying for stalling."""
    o = state["obs"]
    won = state["boundary"] == "terminal" and o["player"]["hp"] > 0 and not any(e["alive"] for e in o["enemies"])
    boss = sum(e["hp"] for e in o["enemies"] if e["alive"])
    taken = (boss0 - boss) / boss0
    if won:
        reward = 1.0
    elif REWARD == "survival":
        reward = 0.25 * taken + 0.04 * o["turn"]
    else:
        reward = 0.5 * taken
    return dict(won=won, reward=reward, hp=o["player"]["hp"], boss=boss, turn=o["turn"])


def play(worker, net, vocab, seed, greedy=False, record=True):
    """One episode. Returns (steps, result); steps are (tokens, actions, chosen index, log prob) per decision."""
    state = start(worker, seed)
    boss0 = sum(e["hp"] for e in state["obs"]["enemies"])
    steps = []
    while state["boundary"] != "terminal" and state["obs"]["turn"] <= MAX_TURNS:
        if not state["legal"]:  # a multi-pick choice: not enumerated by the worker, not learned here
            state = worker.step({"type": "choose", "picks": list(range(state["choice"]["min"]))})
            continue
        toks, acts = encode(state, vocab)
        with torch.no_grad():
            logits, _ = net(collate([(toks, acts)]))
        dist = torch.distributions.Categorical(logits=logits[0])
        i = int(logits[0].argmax()) if greedy else int(dist.sample())
        if record:
            steps.append((toks, acts, i, float(dist.log_prob(torch.tensor(i)))))
        state = worker.step(state["legal"][i])
    return steps, outcome(state, boss0)


def warm_vocab(n=150, seed=0):
    """Collect ids from random play-first episodes, the recorded fight included, then freeze."""
    vocab, rng = Vocab(), random.Random(seed)
    with CombatWorker() as w:
        for k in range(n):
            state = start(w, None if k % 10 == 0 else f"VOCAB-{k}")
            while True:
                encode(state, vocab)
                if state["boundary"] == "terminal" or state["obs"]["turn"] > MAX_TURNS:
                    break
                legal = state["legal"] or [{"type": "choose", "picks": list(range(state["choice"]["min"]))}]
                plays = [a for a in legal if a["type"] != "end_turn"]
                state = w.step(rng.choice(plays if plays and rng.random() < 0.85 else legal))
    vocab.frozen = True
    return vocab


# ---- processes ----

def actor(rank, cfg, vocab_table, weights_q, out_q, train_on, reward):
    global REWARD
    REWARD = reward
    torch.set_num_threads(1)
    vocab = Vocab(vocab_table, frozen=True)
    net = Net(**cfg).eval()
    rng = random.Random(1000 + rank)
    version = -1
    with CombatWorker() as w:
        while True:
            try:
                while True:
                    version, sd = weights_q.get_nowait()
                    if version == "stop":
                        return
                    net.load_state_dict(sd)
            except queue.Empty:
                pass
            if version < 0:
                time.sleep(0.05)
                continue
            seed = None if train_on == "recorded" else f"TRAIN-{rank}-{rng.getrandbits(40):x}"
            steps, res = play(w, net, vocab, seed)
            out_q.put((version, steps, res, vocab.unknown))


def evaluator(cfg, vocab_table, weights_q, out_q, n_seeds, n_sampled, reward):
    global REWARD
    REWARD = reward
    torch.set_num_threads(1)
    vocab = Vocab(vocab_table, frozen=True)
    net = Net(**cfg).eval()
    with CombatWorker() as w:
        while True:
            item = weights_q.get()
            while not weights_q.empty():
                item = weights_q.get()
            version, sd = item
            if version == "stop":
                return
            net.load_state_dict(sd)
            t0 = time.time()
            _, rec = play(w, net, vocab, None, greedy=True, record=False)
            sampled = [play(w, net, vocab, None, record=False)[1] for _ in range(n_sampled)]
            held = [play(w, net, vocab, f"EVAL-{i}", greedy=True, record=False)[1] for i in range(n_seeds)]
            out_q.put(dict(version=version, eval_s=round(time.time() - t0, 1),
                           recorded_greedy=rec, recorded_sampled_win=mean(r["won"] for r in sampled),
                           heldout_win=mean(r["won"] for r in held), heldout_boss=mean(r["boss"] for r in held),
                           heldout_turn=mean(r["turn"] for r in held)))


def mean(xs):
    xs = list(xs)
    return sum(xs) / len(xs) if xs else float("nan")


# ---- learner ----

def ppo_update(net, opt, episodes, args, device):
    """GAE over each episode's terminal reward, then clipped PPO epochs. Everything is padded once per update."""
    episodes = [(steps, res) for steps, res in episodes if steps]
    samples = [s for steps, _ in episodes for s in steps]
    big = {k: v.to(device) for k, v in collate([(t, a) for t, a, _, _ in samples]).items()}
    chosen = torch.tensor([s[2] for s in samples], device=device)
    old = torch.tensor([s[3] for s in samples], device=device)
    n = len(samples)
    take = lambda idx: {k: v[idx] for k, v in big.items()}
    with torch.no_grad():
        values = torch.cat([net(take(torch.arange(k, min(k + 2048, n), device=device)))[1]
                            for k in range(0, n, 2048)]).cpu().tolist()
    advs, returns, k = [], [], 0
    for steps, res in episodes:
        v = values[k:k + len(steps)] + [0.0]
        gae, adv = 0.0, [0.0] * len(steps)
        for t in reversed(range(len(steps))):
            r = res["reward"] if t == len(steps) - 1 else 0.0
            gae = r + v[t + 1] - v[t] + args.lam * gae
            adv[t] = gae
        advs += adv
        returns += [a + b for a, b in zip(adv, v)]
        k += len(steps)
    advs_t, returns_t = torch.tensor(advs, device=device), torch.tensor(returns, device=device)
    advs_t = (advs_t - advs_t.mean()) / (advs_t.std() + 1e-8)
    stats = []
    for _ in range(args.epochs):
        perm = torch.randperm(n, device=device)
        for j in range(0, n, args.minibatch):
            idx = perm[j:j + args.minibatch]
            logits, value = net(take(idx))
            dist = torch.distributions.Categorical(logits=logits)
            ratio = torch.exp(dist.log_prob(chosen[idx]) - old[idx])
            a = advs_t[idx]
            pg = -torch.min(ratio * a, ratio.clamp(1 - args.clip, 1 + args.clip) * a).mean()
            vl = (value - returns_t[idx]).pow(2).mean()
            ent = dist.entropy().mean()
            loss = pg + args.vf * vl - args.ent * ent
            opt.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(net.parameters(), 1.0)
            opt.step()
            stats.append((pg.item(), vl.item(), ent.item(), ((ratio - 1).abs() > args.clip).float().mean().item()))
    s = [mean(x) for x in zip(*stats)]
    return dict(samples=n, pg=s[0], vloss=s[1], entropy=s[2], clipfrac=s[3])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--train-on", choices=["seeds", "recorded"], default="seeds")
    ap.add_argument("--actors", type=int, default=10)
    ap.add_argument("--episodes", type=int, default=240, help="episodes per update")
    ap.add_argument("--hours", type=float, default=1.0)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--epochs", type=int, default=3)
    ap.add_argument("--minibatch", type=int, default=512)
    ap.add_argument("--clip", type=float, default=0.2)
    ap.add_argument("--lam", type=float, default=0.95)
    ap.add_argument("--vf", type=float, default=0.5)
    ap.add_argument("--ent", type=float, default=0.01)
    ap.add_argument("--d", type=int, default=128)
    ap.add_argument("--layers", type=int, default=3)
    ap.add_argument("--eval-every", type=int, default=10)
    ap.add_argument("--eval-seeds", type=int, default=100)
    ap.add_argument("--eval-sampled", type=int, default=20)
    ap.add_argument("--threads", type=int, default=3)
    ap.add_argument("--device", default="mps")
    ap.add_argument("--reward", choices=["damage", "survival"], default="damage")
    args = ap.parse_args()

    out = HERE / "runs" / args.name
    out.mkdir(parents=True, exist_ok=True)
    log = open(out / "log.jsonl", "a")
    log.write(json.dumps({"args": vars(args)}) + "\n")
    torch.manual_seed(0)
    torch.set_num_threads(args.threads)

    t0 = time.time()
    vocab = warm_vocab()
    print(f"vocab {len(vocab)} ids in {time.time() - t0:.0f}s", flush=True)
    cfg = dict(vocab_size=len(vocab) + 8, d=args.d, layers=args.layers)
    net = Net(**cfg).to(args.device)
    opt = torch.optim.Adam(net.parameters(), lr=args.lr)
    print(f"{sum(p.numel() for p in net.parameters()):,} parameters", flush=True)

    ctx = mp.get_context("spawn")
    out_q, eval_out = ctx.Queue(), ctx.Queue()
    weight_qs = [ctx.Queue() for _ in range(args.actors)]
    procs = [ctx.Process(target=actor, args=(r, cfg, vocab.table, weight_qs[r], out_q, args.train_on, args.reward), daemon=True)
             for r in range(args.actors)]
    eval_q = ctx.Queue()
    procs.append(ctx.Process(target=evaluator, args=(cfg, vocab.table, eval_q, eval_out, args.eval_seeds,
                                                     args.eval_sampled, args.reward), daemon=True))
    for p in procs:
        p.start()

    def publish(version):
        sd = {k: v.detach().cpu().clone() for k, v in net.state_dict().items()}
        for q in weight_qs:
            q.put((version, sd))
        return sd

    it, total_eps, deadline = 0, 0, time.time() + args.hours * 3600
    publish(0)
    best_heldout = -1.0
    while time.time() < deadline:
        t_it = time.time()
        episodes, lags, unknown = [], [], 0
        while len(episodes) < args.episodes:
            version, steps, res, unk = out_q.get()
            lags.append(it - version); unknown = max(unknown, unk)
            if it - version <= 2:
                episodes.append((steps, res))
        t_collect = time.time() - t_it
        stats = ppo_update(net, opt, episodes, args, args.device)
        it += 1
        total_eps += len(episodes)
        sd = publish(it)
        results = [r for _, r in episodes]
        row = dict(it=it, episodes=total_eps, min=round((time.time() - t0) / 60, 1),
                   train_win=mean(r["won"] for r in results), train_reward=mean(r["reward"] for r in results),
                   train_boss=mean(r["boss"] for r in results), train_turn=mean(r["turn"] for r in results),
                   collect_s=round(t_collect, 1), update_s=round(time.time() - t_it - t_collect, 1),
                   lag=mean(lags), unknown_ids=unknown, **stats)
        if it % args.eval_every == 0:
            eval_q.put((it, sd))
            # Every evaluated policy is kept, so shift.py can show how play changed across training.
            (out / "snapshots").mkdir(exist_ok=True)
            torch.save(dict(cfg=cfg, vocab=vocab.table, state=sd, it=it, episodes=total_eps),
                       out / "snapshots" / f"it{it:05d}.pt")
        try:
            while True:
                ev = eval_out.get_nowait()
                row["eval"] = ev
                if ev["heldout_win"] >= best_heldout:
                    best_heldout = ev["heldout_win"]
                    torch.save(dict(cfg=cfg, vocab=vocab.table, state={k: v.cpu() for k, v in net.state_dict().items()}, eval=ev), out / "best.pt")
        except queue.Empty:
            pass
        log.write(json.dumps(row) + "\n"); log.flush()
        ev = row.get("eval")
        print(f"it {it:4d} eps {total_eps:7d} {row['min']:6.1f}m | train win {row['train_win']:.3f} "
              f"boss {row['train_boss']:5.1f} turn {row['train_turn']:4.1f} | ent {stats['entropy']:.2f} "
              f"v {stats['vloss']:.3f} | {row['collect_s']}s+{row['update_s']}s"
              + (f" || EVAL@{ev['version']} recorded {'WIN' if ev['recorded_greedy']['won'] else 'loss'} "
                 f"(hp {ev['recorded_greedy']['hp']}, boss {ev['recorded_greedy']['boss']}) "
                 f"sampled {ev['recorded_sampled_win']:.2f} heldout {ev['heldout_win']:.2f}" if ev else ""),
              flush=True)
        torch.save(dict(cfg=cfg, vocab=vocab.table, state={k: v.cpu() for k, v in net.state_dict().items()}, it=it), out / "ckpt.pt")

    for q in weight_qs:
        q.put(("stop", None))
    eval_q.put(("stop", None))
    for p in procs:
        p.join(timeout=10)


if __name__ == "__main__":
    main()
