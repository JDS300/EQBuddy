#!/usr/bin/env python3
"""Merge zone-connection sources into src/EQBuddy.Core/Data/ZoneGraph.json.

Primary: eqltools.com atlas zonelines (layout-extract.json here) — client-mined
from EverQuest Legends game files, so it IS the walking topology. But it has no
boat routes: the ocean zones (Ocean of Tears, Erud's Crossing, Timorous Deep)
carry no zone lines, which would strand each continent.

Supplement: the eqlwiki "Adjacent Zones" harvest (../eqlwiki/zones.json), which
lists boat and port connections. Wiki names are canonicalized onto the atlas's
display names (leading "The " stripped for matching) so one zone never becomes
two nodes; only edges the atlas doesn't already have are added.

Report lists every wiki-supplied edge so drift is auditable.
"""

import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
DEST = ROOT / "src" / "EQBuddy.Core" / "Data" / "ZoneGraph.json"


import re

# Wiki page names that don't reduce to an atlas name by the generic rules —
# hand-curated so drift stays auditable. City halves map to the half holding
# the named connector (docks, tunnel); being one intra-city hop off is cheaper
# than an ambiguity heuristic being confidently wrong.
ALIASES = {
    "burning wood": "The Burning Woods",
    "kaladim": "South Kaladim",
    "cabilis": "Cabilis West",
    "cazic thule": "Temple of Cazic-Thule",
    "cazic thule (zone)": "Temple of Cazic-Thule",
    "freeport": "East Freeport",
    "kael drakkal": "Kael Drakkel",
    "upper guk": "The City of Guk",
    "lower guk": "The Ruins of Old Guk",
    "runnyeye": "The Liberated Citadel of Runnyeye",
    "runnyeye citadel": "The Liberated Citadel of Runnyeye",
    "neriak": "Neriak - Foreign Quarter",
    "neriak third gate": "Neriak - Third Gate",
    "hole": "The Ruins of Old Paineel",
    "old sebilis": "The Ruins of Sebilis",
    "sebilis": "The Ruins of Sebilis",
    "kerra island": "Kerra Isle",
    "southern karana": "The Southern Plains of Karana",
    "western karana": "The Western Plains of Karana",
    "splitpaw lair": "The Lair of the Splitpaw",
    "mistmoore castle": "The Castle of Mistmoore",
    "thurgadin": "The City of Thurgadin",
    "permafrost": "Permafrost Keep",
    "qeynos": "South Qeynos",
    "qeynos aqueducts": "The Qeynos Aqueduct System",
    "north ro": "The Northern Desert of Ro",
    "warsliks wood": "The Warsliks Woods",
    "kelethin": "The Greater Faydark",     # platform city inside the zone
    "felwithe": "Northern Felwithe",
}

# Not zones at all (wiki link noise on Adjacent Zones lines).
BLOCKLIST = {"wizard", "plane of hate cleanupproject"}


def norm(name):
    n = re.sub(r"\s*\([^)]*\)$", "", name.strip()).lower()   # "Chardok (Post-Revamp)" → chardok
    return n[4:] if n.startswith("the ") else n


def main():
    extract = json.loads((HERE / "layout-extract.json").read_text(encoding="utf-8"))
    names = extract["names"]

    graph = {}   # display name -> sorted set of display names

    def add(a, b):
        if a == b:
            return
        graph.setdefault(a, set()).add(b)
        graph.setdefault(b, set()).add(a)

    for code_a, code_b in extract["lines"]:
        if code_a in names and code_b in names:
            add(names[code_a], names[code_b])
    atlas_edges = sum(len(v) for v in graph.values()) // 2

    # Every atlas zone is a node even without lines (the oceans) so wiki edges
    # can attach to them under the atlas's own name.
    for display in names.values():
        graph.setdefault(display, set())

    by_norm = {norm(d): d for d in graph}

    def canonical(wiki_name):
        n = norm(wiki_name)
        if n in BLOCKLIST:
            return None
        if n in ALIASES:
            return ALIASES[n]
        return by_norm.get(n, wiki_name.strip())

    wiki = json.loads((ROOT / "scripts/harvests/eqlwiki/zones.json").read_text(encoding="utf-8"))
    added = []
    for wiki_zone, adjacent in wiki.items():
        a = canonical(wiki_zone)
        if a is None:
            continue
        for wiki_adj in adjacent:
            b = canonical(wiki_adj)
            if b is None or b == a:
                continue
            if b not in graph.get(a, set()):
                add(a, b)
                added.append(f"{a} ↔ {b}")

    out = {zone: sorted(adj) for zone, adj in sorted(graph.items())}
    DEST.write_text(json.dumps(out, ensure_ascii=False, separators=(",", ":")),
                    encoding="utf-8")

    report = [
        "# ZoneGraph merge report", "",
        f"- Atlas zones: {len(names)} · atlas edges (client-mined): {atlas_edges}",
        f"- Wiki-supplied edges (boats, ports, wiki-only zones): {len(added)}",
        f"- Final: {len(out)} nodes, {sum(len(v) for v in out.values()) // 2} edges",
        "", "## Edges added from the wiki", *[f"- {e}" for e in sorted(set(added))],
    ]
    (HERE / "merge-report.md").write_text("\n".join(report), encoding="utf-8")
    print(f"{DEST.name}: {len(out)} nodes; atlas {atlas_edges} edges + wiki {len(added)}")


if __name__ == "__main__":
    main()
