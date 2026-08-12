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

Serialization matches quests-promote.py: sorted keys, compact separators, so knowledge-refresh
PRs diff as DATA rather than formatting.
"""

import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "spells.json"
OUT = HERE.parents[2] / "src" / "EQBuddy.Core" / "Data" / "SpellDurations.json"


def promote(spells):
    durations = {}
    for s in spells:
        seconds = s.get("duration_seconds")
        if not isinstance(seconds, (int, float)) or seconds <= 0:
            continue
        name = s["name"].strip()
        if name:
            durations[name] = round(float(seconds), 1)
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
