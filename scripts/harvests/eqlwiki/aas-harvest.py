#!/usr/bin/env python3
"""Harvest the EverQuest Legends AA catalog from eqlwiki.com.

Discovery result (2026-08-06): eqlwiki has NO per-AA pages, templates, or
categories (Template:AApage / Template:AA / Category:Alternate Advancement all
empty). The entire AA catalog lives on the single page "Alternate Advancement"
(pageid 56762) as 19 wikitables with uniform columns:
    ! Name !! Ranks !! Cost !! Description
organized under sections: General AAs, Archetype AAs, <Class> Class AAs (x16),
Special AAs.

This script:
  1. fetches that page's wikitext via the MediaWiki API (cached in cache/,
     delete cache/Alternate_Advancement.wikitext to force a refetch),
  2. parses every table row into aas.json (raw description preserved),
  3. writes report.md with a duration/timing-keyword analysis.

Usage: python harvest.py  [--refetch]
"""
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
CACHE = HERE / "cache"
API = "https://eqlwiki.com/api.php"
UA = "EQBuddy-harvester/1.0 (contact: david.edwards08@gmail.com; polite ~1 req/sec)"
PAGE_TITLE = "Alternate Advancement"

DURATION_KEYWORDS = [
    "duration", "extend", "mesmeri", "charm", "root", "snare", "lull",
    "buff", "tick", "recast", "reuse", "faster", "haste", "regen",
]


def api_get(params: dict) -> dict:
    params = dict(params, format="json", formatversion="2")
    url = API + "?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.load(resp)
    time.sleep(1.1)  # polite pacing
    return data


def fetch_page_wikitext(refetch: bool = False) -> str:
    CACHE.mkdir(exist_ok=True)
    cache_file = CACHE / (PAGE_TITLE.replace(" ", "_") + ".wikitext")
    if cache_file.exists() and not refetch:
        return cache_file.read_text(encoding="utf-8")
    data = api_get({
        "action": "query", "prop": "revisions", "titles": PAGE_TITLE,
        "rvprop": "content|timestamp", "rvslots": "main",
    })
    page = data["query"]["pages"][0]
    rev = page["revisions"][0]
    txt = rev["slots"]["main"]["content"]
    cache_file.write_text(txt, encoding="utf-8")
    (CACHE / "revision_meta.json").write_text(json.dumps(
        {"pageid": page["pageid"], "title": page["title"],
         "rev_timestamp": rev.get("timestamp")}, indent=2), encoding="utf-8")
    return txt


def strip_wiki_markup(s: str) -> str:
    s = re.sub(r"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", r"\1", s)  # [[x|y]] -> y
    s = re.sub(r"'''?", "", s)
    return s.strip()


def parse_tables(txt: str):
    """Yield (section_path, rows) where rows are lists of cell strings."""
    # Track headings as we walk the text.
    events = []  # (pos, kind, payload)
    for m in re.finditer(r"^(={2,3})\s*(.*?)\s*={2,3}\s*$", txt, re.M):
        events.append((m.start(), "h", (len(m.group(1)), m.group(2))))
    for m in re.finditer(r"\{\|.*?\n(.*?)\n\|\}", txt, re.S):
        events.append((m.start(), "t", m.group(1)))
    events.sort(key=lambda e: e[0])

    h2 = h3 = None
    for _pos, kind, payload in events:
        if kind == "h":
            level, title = payload
            if level == 2:
                h2, h3 = title, None
            else:
                h3 = title
            continue
        section = h3 or h2 or "(no section)"
        rows = []
        for raw_row in payload.split("|-"):
            cells = []
            for line in raw_row.strip().splitlines():
                line = line.strip()
                if not line or line.startswith("!") or line.startswith("{|"):
                    continue
                if line.startswith("|"):
                    cells.extend(c.strip() for c in line[1:].split("||"))
            if cells:
                rows.append(cells)
        yield section, rows


def classify(section: str):
    """Map a section title to (category, class_name_or_None)."""
    m = re.match(r"(.+?) Class AAs$", section)
    if m:
        return "Class", m.group(1)
    if section.startswith("General"):
        return "General", None
    if section.startswith("Archetype"):
        return "Archetype", None
    if section.startswith("Special"):
        return "Special", None
    return section, None


def parse_abilities(txt: str):
    abilities, unparseable = [], []
    for section, rows in parse_tables(txt):
        category, cls = classify(section)
        for cells in rows:
            if len(cells) != 4:
                unparseable.append({"section": section, "cells": cells})
                continue
            name, ranks, cost, desc = (strip_wiki_markup(c) for c in cells)
            if name.lower() == "name":
                continue  # stray header row
            try:
                max_rank = int(ranks)
            except ValueError:
                max_rank = None  # keep raw below
            req_m = re.search(r"((?:Requirements?:|Req level)\s*.*)$", desc)
            requirements = req_m.group(1).strip() if req_m else None
            lvl_m = re.search(r"(?:Requirements?:[^.]*?level|Req level)\s*(\d+)", desc, re.I)
            abilities.append({
                "name": name,
                "category": category,          # General / Archetype / Class / Special
                "class": cls,                  # class name for Class AAs, else null
                "max_rank": max_rank,
                "ranks_raw": ranks,
                "cost_per_rank_raw": cost,     # slash-separated, '?' = unknown on wiki
                "level_requirement": int(lvl_m.group(1)) if lvl_m else None,
                "requirements_raw": requirements,
                "effect_text": desc,           # full raw description (per-rank numbers inline as a/b/c)
            })
    return abilities, unparseable


def sentences(text: str):
    # Split on '. ' but keep enough context; wiki text is plain prose.
    return re.split(r"(?<=[.!?])\s+", text)


def duration_matches(abilities):
    out = []
    for ab in abilities:
        hits = {}
        for sent in sentences(ab["effect_text"]):
            low = sent.lower()
            kws = [k for k in DURATION_KEYWORDS if k in low]
            if kws:
                hits.setdefault(sent, kws)
        if hits:
            out.append((ab, hits))
    return out


def per_rank_numbers(sent: str):
    """Extract slash-separated per-rank number groups from a sentence."""
    return re.findall(r"\d[\d.,]*%?(?:/[\d?.,]+%?)+", sent)


def write_report(abilities, unparseable, matches):
    lines = []
    lines.append("# EQL Wiki AA Harvest Report")
    lines.append("")
    lines.append(f"Harvested: 2026-08-06 from https://eqlwiki.com/wiki/Alternate_Advancement (MediaWiki API)")
    lines.append("")
    lines.append("## Discovery route")
    lines.append("")
    lines.append("- `list=embeddedin` for Template:AApage / Template:AAPage / Template:AA: **empty**")
    lines.append("- `list=categorymembers` for Category:Alternate Advancement / Category:AAs: **empty**")
    lines.append("- `list=search` for \"Alternate Advancement\": **1 hit** - the single page `Alternate Advancement` (pageid 56762, 38,550 bytes)")
    lines.append("")
    lines.append("**eqlwiki has no per-AA pages.** The whole catalog is 19 uniform wikitables"
                 " (`Name / Ranks / Cost / Description`) on that one page, sectioned as General,"
                 " Archetype, one table per class (16 classes), and Special. This report parses those tables.")
    lines.append("")
    by_cat = {}
    for ab in abilities:
        by_cat.setdefault(ab["category"], []).append(ab)
    lines.append(f"## Totals")
    lines.append("")
    lines.append(f"- **Total abilities: {len(abilities)}**")
    for cat in ("General", "Archetype", "Class", "Special"):
        if cat in by_cat:
            lines.append(f"  - {cat}: {len(by_cat[cat])}")
    if "Class" in by_cat:
        per_class = {}
        for ab in by_cat["Class"]:
            per_class[ab["class"]] = per_class.get(ab["class"], 0) + 1
        lines.append("  - Per class: " + ", ".join(f"{c} {n}" for c, n in sorted(per_class.items())))
    lines.append("")
    lines.append("Cost and effect numbers are slash-separated per rank exactly as the wiki gives them;"
                 " `?` means the wiki itself doesn't know the value (unconfirmed ranks). Nothing is invented.")
    lines.append("")
    lines.append("## AAs with DURATION / TIMING effects")
    lines.append("")
    lines.append("Keyword scan (case-insensitive) of effect text for: " + ", ".join(DURATION_KEYWORDS) + ".")
    lines.append(f"**{len(matches)} abilities matched.** Exact effect sentences quoted; per-rank numbers pulled from each quoted sentence.")
    lines.append("")
    for ab, hits in matches:
        who = ab["class"] or ab["category"]
        lines.append(f"### {ab['name']} ({who}; {ab['ranks_raw']} rank(s), cost {ab['cost_per_rank_raw']})")
        if ab["requirements_raw"]:
            lines.append(f"*{ab['requirements_raw']}*")
        lines.append("")
        for sent, kws in hits.items():
            lines.append(f"- Keywords `{', '.join(kws)}`:")
            lines.append(f"  > {sent}")
            nums = per_rank_numbers(sent)
            if nums:
                lines.append(f"  - Per-rank numbers: {'; '.join(nums)}")
        lines.append("")
    lines.append("## Unparseable rows")
    lines.append("")
    if unparseable:
        for u in unparseable:
            lines.append(f"- [{u['section']}] cells={u['cells']}")
    else:
        lines.append("None - every table row parsed cleanly into the 4-column schema.")
    lines.append("")
    (HERE / "report.md").write_text("\n".join(lines), encoding="utf-8")


def main():
    refetch = "--refetch" in sys.argv
    txt = fetch_page_wikitext(refetch)
    abilities, unparseable = parse_abilities(txt)
    (HERE / "aas.json").write_text(
        json.dumps({"source": "https://eqlwiki.com/wiki/Alternate_Advancement",
                    "harvested": "2026-08-06",
                    "note": ("eqlwiki keeps all AAs on this single page; there are no per-AA pages. "
                             "Per-rank values are embedded in effect_text as slash-separated numbers "
                             "(e.g. 20/40/60%); '?' = value unknown on the wiki."),
                    "count": len(abilities),
                    "abilities": abilities,
                    "unparseable": unparseable},
                   indent=2, ensure_ascii=False), encoding="utf-8")
    matches = duration_matches(abilities)
    write_report(abilities, unparseable, matches)
    print(f"abilities={len(abilities)} unparseable={len(unparseable)} duration_matches={len(matches)}")


if __name__ == "__main__":
    main()
