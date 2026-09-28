"""One recorded combat, replayed headless, checked against the real game's own checkpoints.

    python3 -m unittest tests.test_recorded_combat -v

The fixture tests always run. The replay tests need ``./setup.sh`` to have copied and
patched the game's DLLs and built the drivers; without it they skip.
"""

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from sts2bridge.fixtures import EXCERPT, MCR
from sts2bridge.worker import LIB, ROOT

MCR_SHA256 = "1b58f3299a7424382193bea2063bbb57020b28f10f81c06a29125044dd1c1188"
SPGN_SHA256 = "e990bd687fb0ce4aa803f4c3bc29e3a832df016bef03a0f46654b603f8d26042"
PROJECT = ROOT / "dotnet" / "ReplayCheck" / "ReplayCheck.csproj"
BINARY = ROOT / "dotnet" / "ReplayCheck" / "bin" / "Debug" / "net9.0" / "ReplayCheck.dll"
REAL_SAVES = Path.home() / "Library" / "Application Support" / "SlayTheSpire2"
CHECKPOINT_IDS = list(range(391, 440))


def excerpt():
    return json.loads(EXCERPT.read_text())


def save_dir_snapshot():
    """(path, mtime_ns, size) for every file the game persists; caches excluded."""
    if not REAL_SAVES.exists():
        return []
    return sorted(
        (str(p), p.stat().st_mtime_ns, p.stat().st_size)
        for p in REAL_SAVES.rglob("*")
        if p.is_file() and "shader_cache" not in p.parts and "vulkan" not in p.parts
    )


class FixtureTests(unittest.TestCase):
    def test_tape_is_the_pinned_recording(self):
        self.assertEqual(hashlib.sha256(MCR.read_bytes()).hexdigest(), MCR_SHA256)
        self.assertEqual(excerpt()["source"]["sha256"], SPGN_SHA256)

    def test_spirebird_excerpt_covers_the_same_combat(self):
        ex = excerpt()
        self.assertEqual((ex["header"]["game_version"], ex["header"]["git_commit"]), ("v0.111.0", "41cef1ea"))
        start, *_, end = ex["combat"]["boundaries"]
        self.assertEqual(start["data"], "ENCOUNTER.THE_INSATIABLE_BOSS")
        self.assertEqual((start["state"]["hp"], end["type"], end["state"]["hp"]), (37, "run_end", 0))
        self.assertEqual([c["id"] for c in ex["combat"]["checksums"]], CHECKPOINT_IDS)
        self.assertEqual(ex["combat"]["state_dumps"], 49)


@unittest.skipUnless(LIB.exists() and shutil.which("dotnet"), "run ./setup.sh first")
class ReplayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.saves_before = save_dir_snapshot()
        subprocess.run(["dotnet", "build", "-v", "q", str(PROJECT)], check=True, capture_output=True)
        cls.work = Path(tempfile.mkdtemp(prefix="combat-parity-"))
        cls.inspect = cls.run_harness("inspect", "--inspect-only")[1]
        cls.pass_rc, cls.report = cls.run_harness("replay", "--strict-steps")
        cls.neg_rc, cls.negative = cls.run_harness("drop-event-1", "--strict-steps", "--drop-event", "1")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.work, ignore_errors=True)

    @classmethod
    def run_harness(cls, name, *flags):
        out = cls.work / name
        cwd = cls.work / f"{name}-cwd"  # GodotStubs resolve user:// relative to cwd
        cwd.mkdir(parents=True)
        proc = subprocess.run(["dotnet", str(BINARY), str(MCR), str(out), *flags],
                              cwd=cwd, capture_output=True, text=True, timeout=300)
        (cls.work / f"{name}.stderr").write_text(proc.stderr)
        report = out / ("mcr_inspect.json" if "--inspect-only" in flags else "replay_report.json")
        return proc.returncode, json.loads(report.read_text())

    def test_tape_reads_with_the_game_reader_on_this_build(self):
        i = self.inspect
        self.assertEqual((i["version"], i["git_commit"]), ("v0.111.0", "41cef1ea"))
        self.assertEqual(i["model_id_hash_recorded"], i["model_id_hash_host"])
        self.assertEqual(i["event_counts"], {"GameAction": 32, "PlayerChoice": 1, "ResumeAction": 1})
        self.assertEqual([c["id"] for c in i["checksums"]], CHECKPOINT_IDS)

    def test_both_recorders_captured_identical_checkpoints(self):
        spirebird = [(c["id"], c["value"], c["context"]) for c in excerpt()["combat"]["checksums"]]
        game = [(c["id"], c["recorded"], c["context"]) for c in self.inspect["checksums"]]
        self.assertEqual(game, spirebird)

    def test_every_checkpoint_matches(self):
        r = self.report
        self.assertEqual(self.pass_rc, 0, r["first_divergence"])
        self.assertEqual((r["matched"], r["mismatched"], r["recorded_checksums"]), (49, 0, 49))
        self.assertEqual((r["unreached_recorded"], r["stall"]), ([], None))
        self.assertEqual(r["checkpoint_threads"], {str(r["main_thread"]): 49})

    def test_terminal_state_matches_the_recordings(self):
        t = self.report["terminal"]
        run_end = excerpt()["combat"]["boundaries"][-1]["state"]
        self.assertEqual((t["player_hp"], t["player_dead"], t["combat_in_progress"]), (run_end["hp"], True, False))
        self.assertEqual([(e["id"], e["hp"]) for e in t["enemies"]], [("MONSTER.THE_INSATIABLE", 90)])

    def test_step_boundaries(self):
        boundaries = {s["i"]: s["boundary"] for s in self.report["steps"]}
        self.assertEqual(len(boundaries), 34)
        self.assertEqual((boundaries[24], boundaries[25], boundaries[33]), ("awaiting_choice", "awaiting_choice", "terminal"))
        self.assertEqual({b for i, b in boundaries.items() if i not in (24, 25, 33)}, {"awaiting_input"})

    def test_dropping_one_input_is_detected_at_the_next_checkpoint(self):
        self.assertEqual(self.neg_rc, 2)
        first = self.negative["first_divergence"]
        self.assertEqual(first["checksum_id"], 393)
        self.assertIn("CARD.BEAM_CELL", first["replay_context"])

    def test_real_saves_untouched(self):
        self.assertEqual(save_dir_snapshot(), self.saves_before)


if __name__ == "__main__":
    unittest.main()
