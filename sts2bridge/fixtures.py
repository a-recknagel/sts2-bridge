"""The pinned fight every parity suite anchors on: run 7TA07BQT5BSJ, Defect A10, floor 33, The Insatiable."""

from pathlib import Path

FIXTURES = Path(__file__).resolve().parent.parent / "fixtures"
MCR = FIXTURES / "7TA07BQT5BSJ-f33-the-insatiable.mcr"  # the game's own replay of the fight, byte for byte
EXCERPT = FIXTURES / "7TA07BQT5BSJ-f33-the-insatiable.spgn-excerpt.json"  # the Spirebird recorder's view of it
WIN = FIXTURES / "7TA07BQT5BSJ-f33-the-insatiable.hand-win.json"  # the same fight won by hand
RUN = FIXTURES / "7TA07BQT5BSJ.run"  # the run's history file: the spec source
