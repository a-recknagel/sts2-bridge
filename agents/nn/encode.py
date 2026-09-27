"""A worker observation as a set of tokens, following research/combat_parity/docs/what-we-give-it.md.

Every token is a kind, an id, up to four small ids (zone, card type, orb position, owner…) and a few numbers. All
ids share one namespaced vocabulary and one embedding table. Piles are sets, so nothing but the orb queue carries
a position. The legal inputs become pointers into the tokens: a play names a hand card and maybe a target enemy,
end turn names the global token, a choice names an option card.

The vocabulary is collected from warm-up episodes and then frozen, so every process numbers ids the same way;
anything unseen after that is id 1, and `Vocab.unknown` counts it. `combat_card` is never read: it is an instance
index, not something on screen.
"""

import math

import torch

KINDS = ["global", "card", "orb", "enemy", "power", "relic"]
N_SUB = 4
N_NUM = 12
MAX_VARS = 6


def sl(x):
    """Symmetric log, for amounts that range from 1 to hundreds."""
    return math.copysign(math.log1p(abs(x)), x)


class Vocab:
    """Namespaced strings to indexes. 0 is none, 1 is unknown (only once frozen)."""

    def __init__(self, table=None, frozen=False):
        self.table = dict(table or {})
        self.frozen = frozen
        self.unknown = 0

    def __call__(self, s):
        if s is None:
            return 0
        i = self.table.get(s)
        if i is None:
            if self.frozen:
                self.unknown += 1
                return 1
            i = self.table[s] = len(self.table) + 2
        return i

    def __len__(self):
        return len(self.table) + 2


def encode(state, v):
    """-> (tokens, actions). tokens: dict of per-token lists. actions: (a, b) token indexes per legal input in
    order, b = -1 when the input names one token."""
    o, p = state["obs"], state["obs"]["player"]
    kind, ident, sub, nums, varn, varv = [], [], [], [], [], []

    def add(k, i, subs=(), xs=(), vars_=None):
        kind.append(KINDS.index(k)); ident.append(v(i))
        s = [v(x) for x in subs][:N_SUB]; sub.append(s + [0] * (N_SUB - len(s)))
        xs = [float(x) for x in xs][:N_NUM]; nums.append(xs + [0.0] * (N_NUM - len(xs)))
        items = list((vars_ or {}).items())[:MAX_VARS]
        varn.append([v("var:" + n) for n, _ in items] + [0] * (MAX_VARS - len(items)))
        varv.append([sl(x) for _, x in items] + [0.0] * (MAX_VARS - len(items)))
        return len(kind) - 1

    orbs = p["orbs"] or []
    cap = p.get("orb_capacity") or 0
    glob = add("global", "global", xs=[
        p["hp"] / 50, sl(p["hp"]), p["max_hp"] / 50, p["block"] / 20, sl(p["block"]), p["energy"] or 0,
        p["max_energy"] or 0, (o["turn"] or 0) / 10, cap / 5, len(orbs) / 5, len(o["draw"]) / 20, len(o["discard"]) / 20])

    def card(c, zone):
        e, cost = c.get("enchantment"), c["cost"]
        return add("card", "card:" + c["id"],
                   ["zone:" + zone, "type:" + c["type"], "target:" + c["target"], e and "ench:" + e["id"]],
                   [c["upgrades"], -1 if cost is None else cost, c["x_cost"], c["playable"], e["amount"] if e else 0],
                   c.get("vars"))

    hand = [card(c, "hand") for c in o["hand"]]
    for zone in ("draw", "discard", "exhaust"):
        for c in o[zone]:
            card(c, zone)
    for zone in ("played", "drawn"):
        for c in o["this_turn"][zone]:
            card(c, zone)
    choice = [card(c, "choice") for c in (state["choice"] or {}).get("options", [])]

    for i, orb in enumerate(orbs):
        add("orb", "orb:" + orb["id"], [f"pos:{i}"], [
            orb["passive"] / 10, sl(orb["passive"]), orb["evoke"] / 20, sl(orb["evoke"]), i == 0, len(orbs) >= cap])

    enemy_tok = {}
    for e in o["enemies"]:
        it = e["intent"] or {"move": None, "intents": []}
        dmg = sum(x.get("damage", 0) * x.get("hits", 1) for x in it["intents"])
        hits = sum(x.get("hits", 0) for x in it["intents"] if "damage" in x)
        types = ["intent:" + x["type"] for x in it["intents"]][:2]
        enemy_tok[e["slot"]] = add("enemy", "monster:" + str(e["id"]), ["move:" + str(it["move"]), *types], [
            e["hp"] / 100, sl(e["hp"]), e["max_hp"] / 100, e["block"] / 20, sl(e["block"]), dmg / 20, sl(dmg), hits,
            e["alive"]])
        for pw in e["powers"]:
            add("power", "power:" + pw["id"], ["owner:enemy"], [pw["amount"] / 10, sl(pw["amount"])])
    for pw in p["powers"]:
        add("power", "power:" + pw["id"], ["owner:player"], [pw["amount"] / 10, sl(pw["amount"])])
    for r in p["relics"]:
        add("relic", "relic:" + r["id"], (), [(r["counter"] or 0) / 10, r["counter"] is not None, r["used_up"]])

    actions = []
    for a in state["legal"]:
        if a["type"] == "play":  # a target is an index into obs.enemies, i.e. an enemy's slot
            actions.append((hand[a["hand"]], -1 if a["target"] is None else enemy_tok[a["target"]]))
        elif a["type"] == "end_turn":
            actions.append((glob, -1))
        elif a["type"] == "choose":
            actions.append((choice[a["picks"][0]], -1) if a["picks"] else (glob, glob))
        else:
            raise ValueError(f"no encoding for legal input {a}")
    return dict(kind=kind, ident=ident, sub=sub, nums=nums, varn=varn, varv=varv), actions


def collate(batch):
    """[(tokens, actions)] -> padded tensors. mask True = real token; act_mask True = legal input."""
    B = len(batch)
    T = max(len(t["kind"]) for t, _ in batch)
    A = max(len(a) for _, a in batch)
    out = dict(kind=torch.zeros(B, T, dtype=torch.long), ident=torch.zeros(B, T, dtype=torch.long),
               sub=torch.zeros(B, T, N_SUB, dtype=torch.long), nums=torch.zeros(B, T, N_NUM),
               varn=torch.zeros(B, T, MAX_VARS, dtype=torch.long), varv=torch.zeros(B, T, MAX_VARS),
               mask=torch.zeros(B, T, dtype=torch.bool), act=torch.full((B, A, 2), -1, dtype=torch.long),
               act_mask=torch.zeros(B, A, dtype=torch.bool))
    for i, (t, a) in enumerate(batch):
        n = len(t["kind"])
        for k in ("kind", "ident", "sub", "nums", "varn", "varv"):
            out[k][i, :n] = torch.tensor(t[k])
        out["mask"][i, :n] = True
        out["act"][i, :len(a)] = torch.tensor(a)
        out["act_mask"][i, :len(a)] = True
    return out
