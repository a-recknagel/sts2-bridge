# sts2-bridge

Run Slay the Spire 2 headless and step through combats and whole runs from Python.

This started as plumbing for my own experiments with game agents. It turned out to be useful on its own.

The game's own `sts2.dll` runs against stubbed Godot, with Python supplying actions at each decision point.
The rules stay; the clicking goes.

For a reference, I used a recorded Insatiable boss fight. Replay and step-by-step play both match all 49
of the game's state checksums. The tests keep checking.

```python
from sts2bridge import CombatWorker, spec_from_run
from sts2bridge.fixtures import MCR

with CombatWorker() as w:
    s = w.load(MCR)                               # or w.start(spec_from_run(run, "ENCOUNTER.X", seed="ANY"))
    while s["boundary"] != "terminal":
        s = w.step(policy(s))                     # any entry of s["legal"]
```

Here, `policy(s)` is your action selector: it returns an entry from `s["legal"]`.

On an M-series Mac: boot ≈ 0.5 s, combat start ≈ 8 ms, step ≈ 5 ms (21 ms for an end turn).

## Setup

You'll need an installed copy of Slay the Spire 2 (the fixtures were recorded on v0.111.0), a .NET 9 runtime
plus any SDK ≥ 9, and Python ≥ 3.11. The first build also needs network access for NuGet.

```sh
./setup.sh                                        # game DLLs into lib/, IL-patch sts2.dll, build the drivers
python3 -m unittest discover -s tests -t . -v     # parity suites; they skip without setup.sh
```

If the game isn't in a default Steam location, pass its directory to `./setup.sh /path/to/game`.
The script patches a local copy of the DLL; it only reads from the install. The tests also check that
nothing in the game's save directory changes.

For the neural agent in `agents/nn`, install the optional dependencies:

```sh
uv venv .venv
uv pip install --python .venv/bin/python -e '.[nn]'
```

## Layout

```text
setup.sh             game DLLs → lib/ (gitignored), IL patch, build
dotnet/
  GodotStubs/        the engine stand-in sts2.dll links against          (vendored from sts2-cli, MIT)
  Patcher/           the two IL patches to sts2.dll                      (vendored from sts2-cli, MIT)
  Substrate/         HeadlessInit (vendored boot) + the parity fixes and headless guards
  CombatWorker/      the JSON-lines worker: load / start / step, start_run / run_step
  ReplayCheck/       replays an .mcr and diffs every checksum
  StubAudit/         which Godot members sts2.dll needs that the stubs lack; rerun per game version
sts2bridge/          Python client (worker.py), pinned fixtures, Spirebird tape reader
tests/               the contract: replay 49/49, step 49/49, hand-played win, specs, runs
fixtures/            the Insatiable fight: .mcr, .run, hand-win, Spirebird excerpt
agents/nn/           PPO on the Insatiable fight
agents/run/          whole-run player: search in combat, priors outside it
docs/                how parity was established, what the policy is given, findings
```

The parity work, reply format, and history are in [the combat parity notes](docs/combat-parity.md).
For the inputs available to an agent, see [what a combat policy sees](docs/what-we-give-it.md).

## Origin

This grew out of `research/combat_parity` and `research/combat_nn` in my `neows_ledger` project and was
extracted on 2026-09-28. The run agent's priors file is still built there (`research/run_priors.py`),
because that's where the source data lives.

## License

[MIT](LICENSE). Use it, change it, build something with it. Keep the notices.
The vendored sts2-cli code retains [Hao Wu's MIT notice](dotnet/GodotStubs/LICENSE-sts2-cli).
The game and its assets belong to Mega Crit and aren't covered by this license; bring your own installation.

## Thanks

Thanks to [Mega Crit](https://www.megacrit.com/) for the game,
[Hao Wu's sts2-cli](https://github.com/wuhao21/sts2-cli) for the headless foundation,
[jorbs' Spirebird](https://spirebird.com/) for the recorder and community stats,
and [ptrlrd's Spire Codex](https://github.com/ptrlrd/spire-codex) for the game data.
