"""Out-of-combat priors for ``play_run``: what A10 players pick, as far as the local data says.

Cards come from the pinned Spirebird capture (``references/spirebird/captures/latest``), cohort ``a10``: each row is
a card, upgrade level and played character with a pairwise pick Elo, pick rate and win rate. The capture's SKIP rows
(``SKIP_ACT<n>_<CHARACTER>``) carry an Elo on the same scale, so "take the best card or skip" is one comparison.

The capture has no relic, ancient or event tabs. Those, and upgrade and removal counts, come from a local Spire
Codex run export (solo standard A10 runs), built once into a JSON file:

    python3 -m research.combat_parity.run_priors <export dir with chunks/*.jsonl.gz> <out.json>

A Codex rate is shrunk towards the kind's overall pick rate with a weight of 5 offers. Event options are counted by
the key the game shows (``EVENT.pages.PAGE.options.OPTION``), which is the ``key`` the run worker reports.
"""

import collections
import glob
import gzip
import json
import sys
from pathlib import Path

from statsdb.sources.spirebird import load_capture

DEFAULT_ELO = 1500.0  # an unrated card sits at the Elo scale's starting point


class Priors:
    def __init__(self, character, codex_json=None):
        self.character = character.removeprefix("CHARACTER.")
        capture = load_capture(cohorts=("a10",))
        self.build = capture.current_version
        self._elo = {}
        self._skip = {}
        for row in capture.cards["a10"]:
            if row.get("character") != self.character:
                continue
            if row.get("isSkip") and row.get("elo") is not None:
                # SKIP_ACT<n>_<CHARACTER>: the act is the only thing a skip row varies by.
                act = next((int(p[3:]) for p in row["key"].split("_") if p.startswith("ACT") and p[3:].isdigit()), None)
                self._skip[act] = row["elo"]
                continue
            if row.get("isToken") or row.get("enchanted") or row.get("elo") is None:
                continue
            key = ("CARD." + row["cardId"], row["upg"])
            # Prefer the capture's current build; older builds only fill gaps.
            if key not in self._elo or row.get("version") == self.build:
                self._elo[key] = row["elo"]
        codex = json.loads(Path(codex_json).read_text()).get(character, {}) if codex_json else {}
        self.relic = codex.get("relic", {})
        self.ancient = codex.get("ancient", {})
        self.event = codex.get("event", {})
        self.upgrade = codex.get("upgrade", {})
        self.remove = codex.get("remove", {})

    def card(self, card_id, upgrades=0):
        return self._elo.get((card_id, upgrades), self._elo.get((card_id, 0), DEFAULT_ELO))

    def skip(self, act):
        """``act`` is the game's 0-based act index; Spirebird numbers acts from 1."""
        return self._skip.get(act + 1, self._skip.get(None, DEFAULT_ELO))


def build_codex(export_dir):
    offered = collections.defaultdict(collections.Counter)
    picked = collections.defaultdict(collections.Counter)
    chosen = collections.defaultdict(collections.Counter)
    runs = collections.Counter()
    for chunk in sorted(glob.glob(str(Path(export_dir) / "chunks" / "*.jsonl.gz"))):
        with gzip.open(chunk, "rt") as fh:
            for line in fh:
                run = json.loads(line)
                if run["ascension"] != 10 or run["game_mode"] != "standard" or len(run["players"]) != 1:
                    continue
                ch = run["players"][0]["character"]
                runs[ch] += 1
                for act in run["map_point_history"]:
                    for point in act:
                        for s in point["player_stats"]:
                            for r in s.get("relic_choices", []):
                                offered[ch, "relic"][r["choice"]] += 1
                                picked[ch, "relic"][r["choice"]] += r["was_picked"]
                            for a in s.get("ancient_choice", []):
                                offered[ch, "ancient"][a["TextKey"]] += 1
                                picked[ch, "ancient"][a["TextKey"]] += a["was_chosen"]
                            for e in s.get("event_choices", []):
                                chosen[ch, "event"][e["title"]["key"].removesuffix(".title")] += 1
                            for key in s.get("upgraded_cards", []):
                                chosen[ch, "upgrade"][key] += 1
                            for c in s.get("cards_removed", []):
                                chosen[ch, "remove"][c["id"]] += 1
    out = {}
    for ch, n in runs.items():
        rates = {}
        for kind in ("relic", "ancient"):
            base = sum(picked[ch, kind].values()) / max(1, sum(offered[ch, kind].values()))
            rates[kind] = {k: (picked[ch, kind][k] + 5 * base) / (offered[ch, kind][k] + 5) for k in offered[ch, kind]}
        counts = {kind: dict(chosen[ch, kind]) for kind in ("event", "upgrade", "remove")}
        out[ch] = {"runs": n, **rates, **counts}
    return out


if __name__ == "__main__":
    codex = build_codex(sys.argv[1])
    Path(sys.argv[2]).write_text(json.dumps(codex, indent=1, sort_keys=True))
    print({ch: p["runs"] for ch, p in codex.items()})
