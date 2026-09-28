"""Read a Spirebird recorder tape (.spgn) and excerpt its last combat.

The container framing follows ``SpgnFinalizer.Build``/``SelfCheck`` in the
recorder (decompiled from spirebird_stats 1.1.0, recorder 0.7.0): magic
``SPGN``, format major/minor, u32 header length, CBOR header, body tag ``V``,
u32 body length, CBOR array of records, CRC-32C. Field numbers are the
constants in ``Spgn.cs``; only the ones this study reads are named here.

    python3 -m sts2bridge.spgn TAPE.spgn            # summary
    python3 -m sts2bridge.spgn TAPE.spgn --excerpt OUT.json

Needs ``cbor2`` (see requirements-combat-parity.txt); the committed excerpt
does not.
"""

import argparse
import collections
import hashlib
import json
import struct
from pathlib import Path

import cbor2

KIND = {0: "preamble", 1: "action", 2: "choice", 3: "boundary", 4: "session", 5: "aux",
        6: "fault", 7: "checksum", 8: "reward_claim", 9: "shop_buy", 10: "rest_option",
        11: "netid_table", 12: "play_resolved"}
BOUNDARY = {0: "run_start", 1: "room_enter", 2: "room_exit", 3: "act_enter", 4: "combat_start",
            5: "combat_end", 6: "turn_start", 7: "run_end"}
HEADER = {1: "seed", 2: "game_version", 3: "mods", 4: "enum_version", 5: "run_params", 6: "recorder",
          7: "claims", 9: "integrity", 100: "model_id_hash", 101: "git_commit", 102: "anchor_model", 103: "vocab"}
CLAIMS = {1: "result", 2: "floor", 3: "score", 4: "act"}
INTEGRITY = {1: "sessions", 2: "rollbacks", 3: "discarded_decisions", 4: "multiplayer", 5: "faults",
             6: "incomplete", 7: "unaudited_version"}
STATE = {1: "hp", 2: "max_hp", 3: "gold", 4: "deck_crc", 5: "potions", 6: "relic_count", 7: "relics"}
RUN_RNG = ["up_front", "shuffle", "unknown_map_point", "combat_card_generation", "combat_potion_generation",
           "combat_card_selection", "combat_energy_costs", "combat_targets", "monster_ai", "niche",
           "combat_orbs", "treasure_room_relics"]  # RunRngType enum values 0..11; the recorder writes (int)key


def load(path):
    data = Path(path).read_bytes()
    if data[:4] != b"SPGN":
        raise ValueError("bad magic")
    header_len = struct.unpack_from("<I", data, 6)[0]
    pos = 10
    header = cbor2.loads(data[pos:pos + header_len])
    pos += header_len + 1  # body profile tag
    body_len = struct.unpack_from("<I", data, pos)[0]
    pos += 4
    body = cbor2.loads(data[pos:pos + body_len])
    return (data[4], data[5]), header, body, data


def last_combat(body):
    """Records from the last combat_start boundary through combat_end or the end of the tape."""
    starts = [i for i, r in enumerate(body) if r.get(0) == 3 and r.get(2) == 4]
    start = starts[-1]
    end = next((i for i in range(start + 1, len(body)) if body[i].get(0) == 3 and body[i].get(2) in (5, 7)),
               len(body) - 1)
    return body[start:end + 1]


def _named(mapping, names):
    return {names.get(k, str(k)): v for k, v in mapping.items()}


def excerpt(path):
    fmt, header, body, raw = load(path)
    records = last_combat(body)
    boundaries = []
    for r in records:
        if r[0] != 3:
            continue
        b = {"anchor": r[1], "type": BOUNDARY[r[2]], "floor": r.get(5), "act_index": r.get(6), "room": r.get(7)}
        if 8 in r:
            b["data"] = r[8]
        if 3 in r:
            b["run_rng_counters"] = {RUN_RNG[k]: v for k, v in sorted(r[3].items())}
        if 9 in r:
            b["state"] = _named(r[9], STATE)
        boundaries.append(b)
    head = _named(header, HEADER)
    head["claims"] = _named(head["claims"], CLAIMS)
    head["integrity"] = _named(head["integrity"], INTEGRITY)
    return {
        "source": {"file": Path(path).name, "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw),
                   "format": list(fmt)},
        "header": {k: v for k, v in head.items() if k not in ("104", "105")},
        "combat": {
            "record_kinds": dict(collections.Counter(KIND[r[0]] for r in records)),
            "boundaries": [b for b in boundaries if b["type"] != "turn_start"],
            "turn_starts": sum(1 for b in boundaries if b["type"] == "turn_start"),
            "inputs": [{"anchor": r[1], "kind": KIND[r[0]], "net_type": r.get(5) if r[0] == 1 else None,
                        "choice_id": r.get(2) if r[0] == 2 else None}
                       for r in records if r[0] in (1, 2)],
            "checksums": [{"id": r[2], "value": r[3], "context": r[4]} for r in records if r[0] == 7],
            "state_dumps": sum(1 for r in records if r[0] == 7 and 5 in r),
        },
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("tape")
    ap.add_argument("--excerpt", help="write the last-combat excerpt JSON here")
    args = ap.parse_args()
    ex = excerpt(args.tape)
    if args.excerpt:
        Path(args.excerpt).write_text(json.dumps(ex, indent=1) + "\n")
    print(json.dumps({"source": ex["source"], "header": ex["header"],
                      "record_kinds": ex["combat"]["record_kinds"]}, indent=1))


if __name__ == "__main__":
    main()
