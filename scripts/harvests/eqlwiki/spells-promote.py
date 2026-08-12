#!/usr/bin/env python3
"""Promote the spell harvest into the embedded duration catalog (SpellDurations.json).

Feeds the DoT/debuff panel's DERIVED durations - the cold-start fallback shown, marked as an
estimate, until the log measures the real thing. Base durations come from the wiki's
| duration = field, already parsed into duration_seconds by spells-harvest.py.

Excluded on purpose:
  - duration_seconds of 0 or None ("Instant") - not a debuff, nothing to count down.
  - the ~337 entries whose raw duration is a LEVEL-SCALED RANGE that the harvest could not
    reduce to a number ("6.3 minutes @L53 to 7.0 minutes @L60", Cripple). These have no single
    base to multiply, so they are absent from the catalog and the panel shows "--". Inventing
    a midpoint here would put a confident wrong number in front of the one decision the panel
    exists to serve.

Ranks are NOT expanded here. "Shiftless Deeds IV" is derived at runtime from the base entry
(see SpellRank); pre-expanding would bloat the catalog and freeze the formula into data.

Name collisions: two different wiki pages occasionally share a spell NAME with genuinely
different durations - not a rank variant, just two spells the wiki happens to call the same
thing. Last-wins (source array order) picks which value survives, same as a plain dict
literal would. That is a real, silent decision, so it is logged rather than swallowed. Two
known cases as of the 2026-08-06 harvest:
  - "Rabies": page "Rabies" (2880.0s) vs page "Putrid Breath" (314.0s) -> keeps 314.0
  - "Solon's Bravura": page "Solon's Bravura" (18.0s) vs page "Solon's Bewitching Bravura"
    (60.0s) -> keeps 60.0
A future refresh could flip either pick if the wiki reorders pages; the warning is what
makes that visible in a refresh PR instead of silently changing behavior.

Serialization matches quests-promote.py: sorted keys, compact separators, so knowledge-refresh
PRs diff as DATA rather than formatting.
"""

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "spells.json"
OUT = HERE.parents[2] / "src" / "EQBuddy.Core" / "Data" / "SpellDurations.json"


def promote(spells):
    durations = {}
    sources = {}
    for s in spells:
        seconds = s.get("duration_seconds")
        if not isinstance(seconds, (int, float)) or seconds <= 0:
            continue
        name = s["name"].strip()
        if not name:
            continue
        value = round(float(seconds), 1)
        page = s.get("page_title") or name
        if name in durations and durations[name] != value:
            print(
                f"warning: duration collision for {name!r}: "
                f"page {sources[name]!r} = {durations[name]}, "
                f"page {page!r} = {value} -> keeping {value}",
                file=sys.stderr,
            )
        durations[name] = value
        sources[name] = page
    return durations


def main():
    spells = json.loads(SRC.read_text(encoding="utf-8"))
    durations = promote(spells)
    payload = {
        "comment": (
            "Base spell durations from eqlwiki.com's | duration = field, promoted by "
            "spells-promote.py. Seconds, at BASE rank. Roman-numeral ranks add 10% per tier "
            "and are derived at runtime (SpellRank). Level-scaled and Instant durations are "
            "excluded, so an absent spell means unknown - never a guess."
        ),
        "durations": dict(sorted(durations.items())),
    }
    OUT.write_text(
        json.dumps(payload, ensure_ascii=False, separators=(",", ":"), indent=1) + "\n",
        encoding="utf-8",
    )
    print(f"{len(durations)} durations -> {OUT}")


if __name__ == "__main__":
    main()
