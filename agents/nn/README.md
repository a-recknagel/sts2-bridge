# A network for one fight

A policy and value network trained with PPO on a single encounter: the Insatiable, with the deck, relics and HP
that run 7TA07BQT5BSJ (Defect A10) brought to it on floor 33. The question is whether a network learns the
fight's rules from play, not whether it plays the whole game. There is no search.

## Held out

The fight that was recorded and lost (`research/combat_parity/fixtures/7TA07BQT5BSJ-f33-the-insatiable.mcr`) is
never trained on. Training fights are started from the run's history file with a fresh random seed each, so the
shuffle and every roll change; the evaluator plays the recording, greedily and sampled, plus the `EVAL-*` seeds
greedily. Some shuffles can't be won and some are easy, which only matters for reading win rates: the recording
is known to be winnable, by the one-pass line in `fixtures/…hand-win.json`.

Training on one seed would teach the draw order through the policy itself, which is peeking by training.
`--train-on recorded` does exactly that, as a check that the pipeline can learn a win at all.

## What it is handed

The observation follows [`what-we-give-it.md`](../combat_parity/docs/what-we-give-it.md). `encode.py` turns it
into tokens: one global token (HP, block, energy, turn, orb capacity, pile sizes), one per card in every pile and
in this turn's played and drawn sets, one per orb with its queue position, one per enemy with its move and intent
damage, and one per power and relic. Cards are ids plus instance state and displayed numbers; the model learns
what each does. The vocabulary is collected from warm-up episodes and frozen, and the run log counts ids that
turned up later (`unknown_ids`).

`model.py` is a set transformer with no positional encoding. The policy scores each legal input from the tokens
it points at, so a play of card i at enemy j reads card i's and enemy j's tokens, and end turn reads the global
token. The value head reads the global token and the mean token.

The reward is terminal: 1 for a win, otherwise half the fraction of the boss's HP taken.

## Running it

```sh
cd research/combat_nn && uv venv --python 3.13 .venv && uv pip install --python .venv/bin/python torch numpy
cd ../..
research/combat_nn/.venv/bin/python -m research.combat_nn.train --name seeds --hours 2
research/combat_nn/.venv/bin/python -m research.combat_nn.train --name smoke --train-on recorded --hours 0.3
```

It needs `research/combat_parity/setup.sh` first. Each run writes `runs/<name>/log.jsonl`, `ckpt.pt` (latest)
and `best.pt` (best held-out win rate), plus `snapshots/it*.pt`, one per eval. The learner defaults to the Apple GPU (`--device mps`); actors are one
CPU thread and one worker each. A worker plays about 10 episodes a second, and an update over 240 episodes
(about 6,000 decisions) takes about 7 s at width 128 and 3 layers, so the learner is the bottleneck.

`show.py` prints a checkpoint's line on the recorded fight; `shift.py` lines several checkpoints up against each
other, by their own greedy lines and by a probe along the hand-played win (below).

## What the first runs showed (2026-09-27)

Three runs, all on the defaults above except where named. Random play-first wins none of 102 fights and takes
the boss to about 290.

| Run | Trained on | Fights | Held-out seeds won | Recorded fight, greedy |
|---|---|---|---|---|
| `smoke` | the recording | 14k | 0% | loss on turn 5, boss 60 |
| `smoke-survival` (`--reward survival --ent 0.02`) | the recording | 9k | 0% | loss on turn 5, boss 60 |
| `seeds` | random seeds, 2 h | 123k | 0% → 9% | loss on turn 5, boss 52–96 in the second half |

In the seeds run, wins on training fights went from 0 to 5–10% and the boss's mean HP left on the held-out seeds from
164 to 114. The network is handed ids, numbers and no card text, so that much of the fight was learned from play.

It had not seen the recorded fight, and played turns 2 to 4 of it card for card as the hand-played win did, at the
same HP (34, 27, 5). It lost on turn 5. There it plays Hologram+ and fetches Turbo+ instead of Boot Sequence+,
has no block against the Thrash at 5 HP, and dies with the boss at 60 or so. The agent that found the win by hand
had named that pick as the fight's crux before any training. Both smoke runs, which trained on the recording
itself, stop at the same place, so memorising the draw order didn't get past it either.

The cause is the reward, not the observation. Every fact the pick needs is on the tokens: 5 HP, the intent's
damage and Boot Sequence+'s displayed 13 block. What the network can't see is that surviving pays. Blocking on
turn 5 costs damage now and is only worth anything if the fight is then won on turn 7, which random exploration
almost never reaches. Under the damage reward, blocking, surviving and dying on turn 6 is worth about the same as
dealing damage and dying on turn 5. Paying 0.04 a turn for survival didn't change that, because the damage given
up to block cancels it. The policy loses entropy and settles on all-in play: a local optimum.

`shift.py` on the seeds run's two kept checkpoints (update 370 and 513) shows the wrong pick hardening: Turbo+
goes from 0.86 to 0.99, Boot Sequence+ from 0.01 to 0.00. On turn 7 at 3 HP both give Bloodletting 1.00 where the
win ended the turn, so the habit of spending every energy has become suicide there. The probe's per-turn number
counts order as well as choice: the policy playing Uproar before Strike on turn 4 scores near 0 although it plays
the same cards.

Other things learned along the way:

- A worker plays about 10 fights a second, so the learner is the bottleneck. Padding each minibatch separately
  ran at 250 decisions a second; padding once per update and running on the Apple GPU is about 7 s per 6,000.
- The vocabulary must be the same in every process. Interning ids in each actor would number them differently, so
  the vocabulary is collected from warm-up fights and frozen, and no run has seen an unknown id since.
- A spec start with a new seed matches the recording in everything but the shuffle: HP, Plating, the Lightning
  orb, orb capacity 3, the boss at 332/341 with Liquify first, and 29 cards.
- Earlier runs kept only `ckpt.pt` and `best.pt`, so how play changed between them can't be recovered and a
  rerun wouldn't retrace it (the actors are parallel). Runs now keep a snapshot per eval.

Not yet tried: restarting training fights from mid-fight states of the policy's own episodes (replaying a seed
and input prefix costs about 5 ms a step), so that turns 4 to 6 get the samples turns 1 to 3 now take; and
a win-only reward with more exploration.
