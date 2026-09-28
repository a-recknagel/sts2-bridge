# Training on the Insatiable

A small PPO experiment: give a network the deck from one Defect A10 run and let it learn the
Insatiable fight. It reached 9% wins on held-out seeds in the first two-hour run. The recorded
fight still kills it on turn 5.

## Run it

From the repository root, after `./setup.sh`:

```sh
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python -e '.[nn]'
.venv/bin/python -m agents.nn.train --name seeds --hours 2
```

The learner defaults to Apple's GPU (`--device mps`). Use `--device cpu` or `--device cuda`
elsewhere. `--actors` controls the number of game workers; the default is 10.

Each run writes to `agents/nn/runs/<name>/`: `log.jsonl`, the latest `ckpt.pt`,
`best.pt` by held-out win rate, and `snapshots/it*.pt` at each evaluation.

To watch a checkpoint play the recorded fight:

```sh
.venv/bin/python -m agents.nn.show agents/nn/runs/seeds/best.pt
```

Add `--sample` to sample actions or `--seed MYSEED` to try a different shuffle.
`python -m agents.nn.shift <checkpoint> <checkpoint> ...` compares their play and scores
the choices along the hand-played winning line.

## How it works

Training starts the same deck, relics, and HP against the boss with a fresh seed each episode.
Evaluation uses the recorded fight and a fixed set of `EVAL-*` seeds. For a fixed-fight
pipeline check, use `--train-on recorded`; that lets the policy memorise the shuffle.

[`encode.py`](encode.py) turns the [observation](../../docs/what-we-give-it.md) into tokens
for cards, orbs, enemies, powers, relics, and global state. Cards carry IDs, instance state,
and displayed numbers. The vocabulary is collected during warm-up and shared across actors.

[`model.py`](model.py) is a set transformer. It scores a legal action using the tokens for
its card and target; a value head estimates the outcome. The default reward is 1 for a win,
otherwise half the fraction of the boss's HP removed.

## First runs — 2026-09-27

| Run | Training | Fights | Held-out seeds won | Recorded fight |
|---|---|---|---|---|
| `smoke` | Fixed recording | 14k | 0% | Turn-5 loss, boss at 60 HP |
| `smoke-survival` | Fixed recording, survival reward | 9k | 0% | Turn-5 loss, boss at 60 HP |
| `seeds` | Fresh seeds, 2 hours | 123k | 0% → 9% | Turn-5 loss, boss at 52–96 HP late in training |

The fresh-seed policy reproduced turns 2–4 of the hand-played win, then used Hologram+ to fetch
Turbo+ instead of Boot Sequence+. At 5 HP, that is an expensive preference for more energy.
The fixed-recording runs made the same choice.

The damage reward looks like a bottleneck: blocking sacrifices damage now, while the payoff
requires surviving long enough to win on turn 7. The survival-reward run didn't fix it.
Possible next experiments are more samples from mid-fight states and a win-only reward with
more exploration. Neither has been tried here yet.
