"""Out-of-combat priors for ``play_run``: what A10 players pick, read from one JSON file per export.

The file is built outside this repo, from data this repo does not carry (neows_ledger's
``research/run_priors.py``). Per character (``CHARACTER.IRONCLAD``, ...):

    card     {"CARD.X": {"0": elo, "1": elo}}    Spirebird A10 pairwise pick Elo by upgrade level
    skip     {"1": elo, ..., "any": elo}          the SKIP_ACT<n> rows, on the same scale: "best card or skip" is
                                                  one comparison
    relic, ancient                                Codex A10 pick rates, shrunk towards the kind's overall rate
    event, upgrade, remove                        Codex A10 counts; event keys are the game's
                                                  EVENT.pages.PAGE.options.OPTION, the ``key`` the run worker reports

With no file every card sits at the Elo scale's starting point, so card choices tie, and the counts are empty.
"""

import json
from pathlib import Path

DEFAULT_ELO = 1500.0  # an unrated card sits at the Elo scale's starting point


class Priors:
    def __init__(self, character, path=None):
        p = json.loads(Path(path).read_text()).get(character, {}) if path else {}
        self.build = p.get("build")
        self._card = p.get("card", {})
        self._skip = p.get("skip", {})
        self.relic = p.get("relic", {})
        self.ancient = p.get("ancient", {})
        self.event = p.get("event", {})
        self.upgrade = p.get("upgrade", {})
        self.remove = p.get("remove", {})

    def card(self, card_id, upgrades=0):
        by_upg = self._card.get(card_id, {})
        return by_upg.get(str(upgrades), by_upg.get("0", DEFAULT_ELO))

    def skip(self, act):
        """``act`` is the game's 0-based act index; Spirebird numbers acts from 1."""
        return self._skip.get(str(act + 1), self._skip.get("any", DEFAULT_ELO))
