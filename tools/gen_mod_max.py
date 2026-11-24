#!/usr/bin/env python3
"""Best roll of every item modifier per trade category, from Path of Building (PoE2) data.

Usage: gen_mod_max.py <PoB install folder> <out.json>
Output: { "armour.gloves": { "#% increased Rarity of Items found": 18, ... }, ... }
Keys are worded like poe2verlay's TradeItem.Normalize: numbers and ranges become '#'.
"""
import json
import os
import re
import sys

pob, out = sys.argv[1], sys.argv[2]
data = os.path.join(pob, "Data")

CATEGORY = {
    "Body Armour": "armour.chest", "Helmet": "armour.helmet", "Gloves": "armour.gloves", "Boots": "armour.boots",
    "Quiver": "armour.quiver", "Shield": "armour.shield", "Focus": "armour.focus", "Buckler": "armour.buckler",
    "Amulet": "accessory.amulet", "Ring": "accessory.ring", "Belt": "accessory.belt", "Claw": "weapon.claw",
    "Dagger": "weapon.dagger", "One Hand Sword": "weapon.onesword", "One Hand Axe": "weapon.oneaxe",
    "One Hand Mace": "weapon.onemace", "Spear": "weapon.spear", "Flail": "weapon.flail",
    "Two Hand Sword": "weapon.twosword", "Two Hand Axe": "weapon.twoaxe", "Two Hand Mace": "weapon.twomace",
    "Talisman": "weapon.talisman", "Crossbow": "weapon.crossbow", "Bow": "weapon.bow", "Wand": "weapon.wand",
    "Sceptre": "weapon.sceptre", "Staff": "weapon.staff", "Fishing Rod": "weapon.rod", "Jewel": "jewel",
}

# Base tags per category (union of every base of that kind).
tags = {}
for name in os.listdir(os.path.join(data, "Bases")):
    text = open(os.path.join(data, "Bases", name), encoding="utf-8").read()
    for m in re.finditer(r'itemBases\["[^"]+"\] = \{(.*?)\n\}', text, re.S):
        body = m.group(1)
        t = re.search(r'type = "([^"]+)"', body)
        tg = re.search(r'tags = \{([^}]*)\}', body)
        if not t or not tg:
            continue
        kind = t.group(1)
        sub = re.search(r'subType = "([^"]+)"', body)
        cat = "weapon.warstaff" if kind == "Staff" and sub and sub.group(1) == "Warstaff" else CATEGORY.get(kind)
        if not cat:
            continue
        base_tags = [x.split("=")[0].strip() for x in tg.group(1).split(",") if x.strip()]
        tags.setdefault(cat, []).append(set(base_tags))

NUM = re.compile(r"[+-]?\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)|[+-]?\d+(?:\.\d+)?")
MOD = re.compile(r'\["[^"]+"\] = \{ type = "(Prefix|Suffix)", affix = "[^"]*", (.*?)statOrder.*?'
                 r'weightKey = \{([^}]*)\}, weightVal = \{([^}]*)\}')


def best(line):
    """('#% increased X', top value) for one mod line."""
    top = None
    for m in NUM.finditer(line):
        v = float(m.group(2)) if m.group(2) else float(m.group(0).lstrip("+"))
        top = v if top is None else top
        break
    return NUM.sub("#", line), top


result = {}
for file in ("ModItem.lua", "ModVeiled.lua"):
    text = open(os.path.join(data, file), encoding="utf-8").read()
    for m in MOD.finditer(text):
        lines = re.findall(r'"([^"]+)"', m.group(2))
        keys = [k.strip().strip('"') for k in m.group(3).split(",") if k.strip()]
        vals = [int(v) for v in m.group(4).split(",") if v.strip()]
        for cat, bases in tags.items():
            ok = False
            for base in bases:
                for k, v in zip(keys, vals):
                    if k in base:
                        ok = ok or v > 0
                        break
            if not ok:
                continue
            for line in lines:
                if "reduced" in line or "less" in line:
                    continue
                key, top = best(line)
                if top is None or top <= 0:
                    continue
                cur = result.setdefault(cat, {}).get(key)
                if cur is None or top > cur:
                    result[cat][key] = top

json.dump(result, open(out, "w", encoding="utf-8"), separators=(",", ":"), sort_keys=True)
print({k: len(v) for k, v in sorted(result.items())})
