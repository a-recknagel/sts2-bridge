# Recorded-combat parity

One real combat, replayed headless on Apple Silicon, matches the real game at
every checkpoint the game itself took.

The combat is the last fight of run `7TA07BQT5BSJ`: Defect, Ascension 10,
floor 33, the act-2 boss The Insatiable, lost on turn 6. It was replayed on the
[sts2-cli](https://github.com/wuhao21/sts2-cli) hosting substrate (Godot stubs
in place of the engine, the game's own `sts2.dll`). The driver is a thin copy
of the game's replay loop. All 49 of the game's `NetFullCombatState` checksums
match. Those checksums cover HP, block, powers, orbs, energy, the ordered hand,
draw, discard and exhaust piles, relic state, and every run and player RNG
counter. The terminal state matches too: player dead at 0 HP, boss at 90.

The same fight also runs through a steppable worker. A caller picks each
input from the legal ones, the worker executes it through the path a human
click takes, and it returns at the next decision boundary. Sending the 27
recorded player inputs this way, with nothing else from the tape, still
matches 49/49 and the terminal state. See [Step a combat](#step-a-combat).

The worker can also start any fight from a spec: a character, deck, relics,
potions and HP against any encounter in the build. A `.run` history file,
local or from the Spire Codex export, becomes a spec in one call. The
Insatiable fight rebuilt from its own run's `.run` file matches the recorded
start in everything the shuffle does not decide. See
[Start any combat](#start-any-combat).

## Reproduce

```sh
research/combat_parity/setup.sh                                  # pinned sts2-cli into .work/, then build
python3 -m unittest research.combat_parity.test_recorded_combat -v
python3 -m unittest research.combat_parity.test_step_worker -v
python3 -m unittest research.combat_parity.test_combat_spec -v
```

`setup.sh` needs an installed Slay the Spire 2 v0.111.0. On macOS it looks in
Steam's default location; pass the `data_sts2_macos_arm64` directory to
override. It also needs git, a .NET 9 runtime and network access. Only the
fixture tests run without it; the replay and worker tests skip. Each suite
takes 2–4 s. Each has a test that fails if anything in
`~/Library/Application Support/SlayTheSpire2` changed during the run.

To look at a single run by hand:

```sh
cd "$(mktemp -d)"   # GodotStubs resolve user:// against the working directory
dotnet …/research/combat_parity/ReplayCheck/bin/Debug/net9.0/ReplayCheck.dll \
  …/research/combat_parity/fixtures/7TA07BQT5BSJ-f33-the-insatiable.mcr out --strict-steps
```

The run writes these files to `out/`:

- `replay_report.json`: per-checkpoint hashes, step boundaries, the terminal
  state and timings.
- `mcr_inspect.json`: the tape read through the game's reader.
- `mcr_initial_run.json`: the captured `SerializableRun`.
- On the first mismatch, `diverge_<id>_{local,replay}.txt`: both full states,
  side by side.

Two flags turn the run into a control:

- `--drop-event N` skips one recorded input. Dropping event 1 must diverge at
  checkpoint 393.
- `--no-restore-checksum` reproduces the first failed attempt described below.

## Step a combat

[`CombatWorker/`](CombatWorker/) is a persistent process on the same
substrate. It talks JSON lines over stdin and stdout, and
[`combat_worker.py`](combat_worker.py) is its Python client:

```python
from research.combat_parity.combat_worker import CombatWorker

with CombatWorker() as w:
    s = w.load("research/combat_parity/fixtures/7TA07BQT5BSJ-f33-the-insatiable.mcr")
    while s["boundary"] != "terminal":
        s = w.step(policy(s))    # an entry of s["legal"]
```

- **Load.** `load` enters the recording's room from its initial state and
  carries over its id cursors. It then runs to the first decision. Calling it
  again in the same process resets to the same start, and the replayed fight
  reproduces every hash.
- **Inputs.** There are four:
  - `{"type": "play", "hand": i, "target": slot}`
  - `{"type": "end_turn"}`
  - `{"type": "potion", "slot": i, "target": slot}`
  - `{"type": "choose", "picks": [i, ...]}`

  Each one takes the path a human click takes. A card play is
  `CardModel.TryManualPlay`'s body. End turn is the end-turn button's
  `EndPlayerTurnAction`, through `RequestEnqueue`. A potion goes through
  `PotionModel.EnqueueManualUse`. Choices go through
  `CardSelectCmd.LocalSelector`, which the game consults only after it has
  reserved the choice id and paused the action. An input that is not legal
  is refused before anything is queued.
- **Boundaries.** A `step` returns in one of three states:
  - `awaiting_input`: every action queued since the input has resolved, the
    queue has drained, no end-turn phase is running, and it is the player's
    play phase.
  - `awaiting_choice`: a choice is pending.
  - `terminal`: the combat is over.

  A new turn number is never a boundary by itself. One `end_turn` runs the
  whole enemy turn and the next turn's start.
- **Reply.**
  - `obs`: the encounter, HP, block, energy, powers, orbs, potions, the relic
    bar (displayed counters, used up), hand with costs and playability, piles
    (draw as a multiset), and enemies with their intents.
  - `legal`: every input valid at this boundary, in the same shape the
    worker accepts.
  - `choice`: the options, when a choice is pending.
  - `state_hash`: the game's `NetFullCombatState` hash.
  - `checkpoints`: the game's own checksums taken since the previous reply.
  - `enqueued_by_game`: actions the game queued by itself.
  - `game_errors`: error-level lines the game logged since the previous
    reply (see [below](#what-the-sweeps-found-in-the-substrate)).
- **Net service.** It is singleplayer, not `SetUpReplay`. The game therefore
  queues `ReadyToBeginEnemyTurnAction` and resumes choice-paused actions on
  its own, and the worker does neither. The service carries the recorded
  player's id, because the `.mcr` writer anonymises ids and the game's
  singleplayer service hard-codes 1
  ([why](CombatWorker/SingleplayerNetService.cs)).

[`test_step_worker.py`](test_step_worker.py) is the caller. It reads the
tape, translates each recorded input into a hand index and enemy slot through
the observation, checks that the input is in `legal`, and sends it. The
result: 49/49 checkpoints, the terminal state, one game-queued
`ReadyToBeginEnemyTurnAction` per end turn (six in total, as recorded), the
Hologram choice surfaced as `awaiting_choice`, and a different first input
diverging. Measured cost on this Mac: worker boot ≈ 460 ms, combat load
≈ 100 ms, step ≈ 5 ms mean and ≈ 21 ms for an end turn.

No recording yet checks potions, multiple enemies, hook actions or
multi-card picks. All of them run in the random-policy sweeps below, but a
sweep shows that a fight plays out, not that it plays out as the game would.

## Start any combat

```python
import json
from research.combat_parity.combat_worker import CombatWorker, spec_from_run

run = json.load(open("research/combat_parity/fixtures/7TA07BQT5BSJ.run"))
with CombatWorker() as w:
    s = w.start(spec_from_run(run, "ENCOUNTER.CEREMONIAL_BEAST_BOSS", seed="ANY", current_hp=77))
    while s["boundary"] != "terminal":
        s = w.step(policy(s))
```

- **Spec.** `{"character", "ascension", "seed", "encounter", "act"?, "player"}`.
  `player` is a partial `SerializablePlayer` in the game's own save JSON.
  That is the shape a `.run` history file stores its players in, both the
  game's `saves/history` and the Spire Codex export. So deck entries carry
  upgrades, enchantments and props, relics carry their saved counters, and
  potions carry their slot. Any field it names replaces the one in a fresh
  run of that character, ascension and seed. The rest stays as the new run
  made it: player RNG, max energy, orb slots. `act` defaults to the act
  whose encounter list contains the encounter. `catalog` lists every
  character and encounter.
- **`spec_from_run(run, encounter, seed, **fields)`.** Takes the deck,
  relics and potions a run ends with, and the HP the player entered its
  last room with. Keywords override any field. A `.run` cannot give the deck
  at an earlier floor: cards record the floor they were added on, but
  removals, transforms and upgrades carry no date.
- **How it loads**
  ([`CombatSpec.cs`](CombatWorker/CombatSpec.cs)). A new run of the
  character goes through the game's own setup
  (`RunManager.SetUpNewSingleplayer`: starting inventory, ascension effects,
  room generation) and comes back out through `ToSave`. The player is then
  overlaid, and the result loads like any save, through the same path as a
  recording. Nothing is obtained, so relics' on-pickup effects do not fire
  twice. What the spec says the player has is what they have.
- **How it enters the room.** The run stands on the act's first map node of
  the encounter's type that no relic has marked. The room is entered with
  `EnterMapCoordDebug`, the game's debug entry, whose `fight` console
  command does the same minus the map node. Monsters come from the run seed,
  not the console command's clock-seeded RNG, so a spec and seed always give
  the same fight. A node is needed because game code reads the current map
  point at combat start, and Fur Coat dereferences it.
- **Refusals.** When a save names an id the build does not know, the game
  swaps in `DeprecatedCard`, `DeprecatedRelic` and so on. That is right for a
  save file and wrong for a spec, so the worker refuses unknown card,
  enchantment, relic, potion and encounter ids, and lists them.
- **Cost.** A warm start takes 6–7 ms, most of it drawing the act's map. The
  first start in a process takes about 130 ms.

[`test_combat_spec.py`](test_combat_spec.py) checks the following:

- The run's `.run` file and the `.mcr` describe the same player, down to the
  JSON.
- The Insatiable fight rebuilt from the `.run` matches the recorded first
  boundary in turn, encounter, HP, block, energy, powers, orbs, potions,
  relic bar, enemies with their HP and intents, and the deck across hand and
  draw. Only the hands and the state hash differ, as the seed dictates.
- The same spec and seed reproduce every state hash over a whole fight.
- The same deck fights an act-1 boss, an act-1 elite and a weak act-1
  fight.
- Every encounter in the build (85: 61 monster, 12 elite, 12 boss) starts
  and plays to the end under a seeded random policy, at A10, with a starter
  deck.
- A Soul Nexus can die.
- Unknown ids, a missing seed, a removed boss and a spec that sets `net_id`
  are refused, and the worker stays usable.

Beyond the tests, one sweep started 2,349 real decks with a random policy:
all 349 local `.run` files and the first 2,000 solo standard A10 runs in
the local Spire Codex sample. Every one of the 2,200 that this build can
represent played to the end. The 149 refusals were 141 decks with modded or
removed ids (`BALLS2-`, `HADESANCIENTS-`, `THEHEROEXPANSION-`,
`CARD.FOLLOW_THROUGH`, `CARD.SCARE`, …) and 8 runs whose killer was
`DOORMAKER_BOSS`, a boss older builds had. It took 106 s for 33,850 steps.

### What the sweeps found in the substrate

The Insatiable fight never touched these gaps. Any fight might.

- **Godot members the stubs lack.** sts2-cli's `GodotStubs` is missing some
  Godot members that `sts2.dll`'s combat code calls. A missing member throws
  `MissingMethodException` when the calling method is compiled, even if the
  node it would touch is null. Kaiser Crab's `SetVisible` stopped that fight
  starting. Phantasmal Gardener's `Mathf.Log` killed the turn loop. A
  static pass over `sts2.dll`'s member references, walked back to their
  callers, found the rest on gameplay paths.
  - The worst was `Callable.From(Func<T>)`, which `GameAction.Cancel` uses,
    so no action could be cancelled.
  - [`sts2-cli-stubs.patch`](sts2-cli-stubs.patch) adds them, and
    `setup.sh` applies it.
  - Deferred calls now wait for the next "frame", as they would in Godot.
    Every wait in [`Substrate`](Substrate/Substrate.cs) flushes them once
    per poll.
- **An unguarded UI dereference.** `SoulNexus.AfterDeath` calls
  `NCombatRoom.Instance.GetCreatureNode` with no null check. Headless the
  node is null, so the turn loop died with the elite.
  `Substrate.HeadlessGuards` patches that one method with Harmony. It keeps
  the unsubscription and drops the animation. Other UI dereferences in model
  code are behind TestMode or null-node checks.
- **Errors the game swallows.** `TaskHelper.RunSafely` and the turn loop log
  an exception and carry on. Every reply now carries the game's error-level
  log lines as `game_errors`. A dead turn loop or a combat that fails to
  start raises at once, with the game's message, not after a 30 s timeout.
- **One benign error.** The game logs "Tried to pop action
  EndPlayerTurnAction … didn't find it in any queue" when the last enemy
  dies inside the end-turn action itself, for instance to an end-of-turn
  Lightning passive. The combat ends first and clears the queues. The
  terminal state is right, and the line comes from unpatched game code. It
  appeared in 30 of the 2,200 sweep fights.

The 49/49 parity of both recorded-fight suites was re-run after each of
these changes.

## What is here

| Path | Role |
|---|---|
| [`fixtures/7TA07BQT5BSJ-f33-the-insatiable.mcr`](fixtures/7TA07BQT5BSJ-f33-the-insatiable.mcr) | The game's own replay of the fight, byte-for-byte |
| [`fixtures/7TA07BQT5BSJ-f33-the-insatiable.spgn-excerpt.json`](fixtures/7TA07BQT5BSJ-f33-the-insatiable.spgn-excerpt.json) | The same fight as the Spirebird recorder saw it: header, boundaries with RNG counters and state anchors, inputs, and 49 checksums. State dumps are omitted |
| [`ReplayCheck/`](ReplayCheck/) | C# harness. Reads the tape with the game's `PacketReader`, replays it as `NMultiplayerTest.RunReplay` does, and compares every checksum by id |
| [`fixtures/7TA07BQT5BSJ.run`](fixtures/7TA07BQT5BSJ.run) | The same run's history file from `saves/history`, byte-for-byte: the spec source the rebuilt-fight test reads |
| [`CombatWorker/`](CombatWorker/), [`combat_worker.py`](combat_worker.py) | The steppable worker and its Python client, described above; [`CombatSpec.cs`](CombatWorker/CombatSpec.cs) and `spec_from_run` start a fight from a spec |
| [`Substrate/`](Substrate/) | Build properties and the code both C# drivers share: boot, checkpoint restoring, hashing, the deferred-call flush and the headless guards |
| [`spgn.py`](spgn.py) | Spirebird tape reader. It regenerates the excerpt from a local tape and needs `cbor2` ([requirements](requirements-combat-parity.txt)) |
| [`setup.sh`](setup.sh) | Clones sts2-cli at `084d1aa` into the gitignored `.work/`, applies the stubs patch, and runs its setup |
| [`test_recorded_combat.py`](test_recorded_combat.py) | The replay test |
| [`test_step_worker.py`](test_step_worker.py) | The same fight driven through `step` by a caller |
| [`test_combat_spec.py`](test_combat_spec.py) | Fights started from specs: the recorded one rebuilt, every encounter, refusals |
| [`sts2-cli-stubs.patch`](sts2-cli-stubs.patch) | Godot members the stubs lack that combat code calls; applied by `setup.sh` |
| [`docs/findings-2026-09-26.md`](docs/findings-2026-09-26.md) | Tape anatomy, the sts2-cli and AutoSlay audits, candidate comparison, next patch |

## Provenance

| Item | Source | Identity |
|---|---|---|
| Game | Steam install, `SlayTheSpire2.app`, `release_info.json` | v0.111.0, commit 41cef1ea, built 2026-08-13; `sts2.dll` sha256 `9cb4f1ad…12b4`; model-ID hash 1568834832 |
| `.mcr` | `profile1/replays/latest.mcr`, written by `CombatReplayWriter` when the run ended, 2026-09-26 02:49 local | sha256 `1b58f329…1188`, 64 024 bytes. The game anonymises it on write (`CombatReplay.Anonymized`), and it contains no Steam ID or name |
| `.run` | `profile1/saves/history/1790380790.run`, the game's run history for the same run (seed `7TA07BQT5BSJ`, v0.111.0) | sha256 `ed998bfc…f9a`, 58 644 bytes. Player id 1, no Steam ID or name |
| `.spgn` | `profile1/spirebird/outbox/7TA07BQT5BSJ_20260926T004913Z_defeat.spgn`, from Spirebird Stats 1.1.0 (recorder 0.7.0, `dump_anchor_states=true`) | sha256 `e990bd68…6042`, 3 742 940 bytes; not committed |
| Recorder mod | installed `mods/spirebird_stats/spirebird_stats.dll` 1.1.0 | sha256 `a46e09b8…e9be`. This is newer than the 1.0.0 preserved in [`references/spirebird/mod/`](../../references/spirebird/mod/) |
| sts2-cli | github.com/wuhao21/sts2-cli | `084d1aa`, 2026-09-07, "update/sts2-v0.111.0". Used as the runtime; its command layer is not used |
| sts2-rl-agent | github.com/zhiyue/sts2-rl-agent | `1b7e7ce`, 2026-05-22. Audited statically, not run |
| Decompiled sources | `ilspycmd` 11.1.0 over the two DLLs above, kept outside the repo | Game type and method names cited below come from this output |
| Host | macOS 15.7.7 arm64 | .NET SDK 10.0.300, runtime 9.0.2 |

Both inputs were copied out of the save directory, and every read went
through the copies. Save-directory mtimes and sizes were snapshotted before
and after every run and compared.

## How this was established

The work on 2026-09-26 started from a paused Divine rewrite. The question was
whether an existing adapter could already replay one real combat on this Mac.

1. **Read the tapes with existing readers.** The game's own loader sits in a
   debug scene (`NMultiplayerTest.LoadReplay`). Outside it, `CombatReplay`
   still deserialises through `PacketReader` once `ModelDb`,
   `ModelIdSerializationCache` and the net-type registries are initialised.
   The model-ID hash in the tape equals the host's, which is what makes the
   bits decodable at all. The Spirebird tape follows its recorder's
   own framing code.
   - The two recorders agree on all 49 checkpoint ids, hashes and contexts.
   - They are not independent measurements: Spirebird subscribes to the same
     `ChecksumTracker.ChecksumGenerated` event.
   - The `.mcr` stores its states after anonymisation, so a replay must match
     the re-hash of the stored state, not the live hash stored beside it. The
     game's `CheckAgainstReplayChecksum` makes the same comparison.
2. **First contact with sts2-cli: its patches were not running.** sts2-cli
   builds with `RollForward=LatestMajor`, so on a machine with .NET 10 it runs
   on CoreCLR 10. There, Harmony throws "CoreCLR version 10.0.8 is not
   supported", and every patch fails with a `[WARN]` while the process carries
   on. Pinning the harness to the 9.0 runtime fixed it.
   - The same run found that sts2-cli never calls `MessageTypes.Initialize()`
     or `ActionTypes.Initialize()`, so it cannot decode a recorded net action.
3. **First replay: 1/49.** sts2-cli sets `TestMode.IsOn`, which has two
   effects:
   - It disables `ChecksumTracker`. The harness turns it back on.
   - It makes `NonInteractiveMode.IsActive` true. `ActionExecutor` then takes
     its non-interactive branch, which never subscribes
     `GameAction.JustBeforeFinished`, so no post-action checksum is taken.
     Checkpoint ids shift, and everything after the first turn-start anchor
     compares against the wrong recorded state.

   The harness re-attaches the logic of `RunManager.SendPostActionChecksum`
   to the action's own `JustBeforeFinished`. That is the same event, raised
   in the same place in `GameAction.Execute`.
4. **Second replay: 29/49, all 20 misses in context strings.** The hashes of
   the 20 post-action checkpoints matched. Their contexts did not, because
   `PlayCardAction.ToString` embeds a per-process card instance number
   (`CARD.MODDED (37885600)`). With that number normalised, the result is
   49/49.
5. **Controls.**
   - Six runs produced byte-identical hash sequences: three feeding inputs as
     the game's replay does, three waiting for a full step boundary after
     each input.
   - Dropping one input diverges at the next checkpoint.
   - The card choice shows up as its own `awaiting_choice` boundary.
   - All 49 checkpoints were taken on the main thread.
   - Repeatability is recorded as a separate result from parity.

Nothing in `sts2.dll`'s gameplay code was touched beyond what sts2-cli already
does. The three harness changes above only restore the game's own measurement
and decoding.
