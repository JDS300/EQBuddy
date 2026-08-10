#!/usr/bin/env python3
"""Promote the quest harvest into the embedded catalog (QuestCatalog.json).

This scripts the converter that previously lived only in a working session —
verified byte-identical against the shipped catalog on 2026-08-09:

  - zones: zone categories + Related Zones + Start Zone, sorted. A category is
    a zone unless it ends in "Quests" — the wiki files quests under both zone
    categories ("South Qeynos") and quest-list categories ("Paladin Quests",
    "All Classes Quests"), and the suffix is the reliable separator.
  - questItems: Category:Quest Items passthrough (the wiki's own "part of a
    quest" marker — broader than turn-ins on purpose, it feeds the Loot views'
    "don't vendor this" signal).
  - every other field passes through; categories/relatedZones fold away.

Serialization is single-line compact JSON with a fixed entry key order: the
catalog is reviewed as a diff in knowledge-refresh PRs, and a stable shape is
what keeps those diffs about DATA, not formatting.
"""

import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE.parents[2] / "src" / "EQBuddy.Core" / "Data" / "QuestCatalog.json"


def promote(q):
    zones = {c for c in q["categories"] if not c.endswith("Quests")}
    zones |= set(q["relatedZones"])
    if q["startZone"]:
        zones.add(q["startZone"])
    return {
        "name": q["name"],
        "url": q["url"],
        "startZone": q["startZone"],
        "questGiver": q["questGiver"],
        "minLevel": q["minLevel"],
        "classes": q["classes"],
        "items": q["items"],
        "rewards": q["rewards"],
        "repeatable": q["repeatable"],
        "era": q["era"],
        "zones": sorted(zones),
    }


def main():
    quests = json.loads((HERE / "quests.json").read_text(encoding="utf-8"))
    items = json.loads((HERE / "quest-items.json").read_text(encoding="utf-8"))
    catalog = {"quests": [promote(q) for q in quests], "questItems": items}
    OUT.write_text(json.dumps(catalog, separators=(",", ":"), ensure_ascii=False),
                   encoding="utf-8")
    print(f"wrote {OUT}: {len(catalog['quests'])} quests, {len(items)} quest items")


if __name__ == "__main__":
    main()
