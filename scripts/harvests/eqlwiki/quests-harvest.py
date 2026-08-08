#!/usr/bin/env python3
"""Harvest the EverQuest Legends quest catalog from eqlwiki.com (MediaWiki API).

Same polite client discipline as spells-harvest.py: ~1 request/second, exponential
backoff, resume-safe wikitext cache in cache/. Rerun any time; cached pages are not
refetched.

Outputs (all in this script's directory):
  quest-titles.json   - enumerated members of Category:Quests
  cache/quest-*.wikitext - raw wikitext per page
  quests.json         - parsed quest array (full)
  quests-report.md    - summary report incl. pages the parser couldn't read

Quest page shape (verified on Animal Skin Armor, 2026-08-07):
  - Infobox wiki-table rows: Start Zone / Quest Giver / Minimum Level / Classes
  - Turn-in items inside "Give <NPC> ..." lines as: N x [[Item Name]]
  - Rewards as {{:Item Name}} transclusions
"""

import json
import re
import time
import urllib.parse
import urllib.request

BASE = "https://eqlwiki.com/api.php"
UA = "EQBuddy-harvest/1.0 (david.edwards08@gmail.com; polite MediaWiki client; ~1 req/s)"
HERE = __import__("pathlib").Path(__file__).resolve().parent
CACHE = HERE / "cache"
CACHE.mkdir(exist_ok=True)
PACE_SECONDS = 1.0

_last_request = [0.0]
backoff_events = []


def api_get(params):
    params = dict(params, format="json")
    url = BASE + "?" + urllib.parse.urlencode(params)
    delay = 2.0
    for attempt in range(7):
        wait = PACE_SECONDS - (time.time() - _last_request[0])
        if wait > 0:
            time.sleep(wait)
        _last_request[0] = time.time()
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=60) as resp:
                data = json.loads(resp.read().decode("utf-8"))
            if "error" in data:
                raise RuntimeError("API error: %s" % data["error"])
            return data
        except Exception as e:
            code = getattr(e, "code", None)
            msg = f"attempt {attempt+1} failed ({e!r}); backing off {delay:.0f}s"
            print("  ! " + msg, flush=True)
            backoff_events.append(msg)
            if code is not None and code not in (429, 500, 502, 503, 504):
                raise
            time.sleep(delay)
            delay *= 2
    raise RuntimeError("Giving up after repeated failures: " + url)


# ---------------------------------------------------------------- enumeration

def enumerate_titles():
    titles_path = HERE / "quest-titles.json"
    if titles_path.exists():
        titles = json.loads(titles_path.read_text(encoding="utf-8"))
        print(f"quest-titles.json cached: {len(titles)} titles")
        return titles
    titles = []
    cont = {}
    while True:
        data = api_get({
            "action": "query", "list": "categorymembers",
            "cmtitle": "Category:Quests", "cmlimit": "200",
            "cmnamespace": "0", **cont,
        })
        titles += [m["title"] for m in data["query"]["categorymembers"]]
        print(f"  enumerated {len(titles)} quest titles", flush=True)
        if "continue" not in data:
            break
        cont = {"cmcontinue": data["continue"]["cmcontinue"]}
    titles_path.write_text(json.dumps(titles, indent=1), encoding="utf-8")
    return titles


def fetch_wikitext(title):
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", title)
    path = CACHE / f"quest-{safe}.wikitext"
    if path.exists():
        return path.read_text(encoding="utf-8")
    data = api_get({
        "action": "query", "prop": "revisions", "rvprop": "content",
        "redirects": "1", "titles": title,
    })
    pages = data["query"]["pages"]
    page = next(iter(pages.values()))
    revs = page.get("revisions")
    text = revs[0]["*"] if revs else ""
    path.write_text(text, encoding="utf-8")
    return text


# ------------------------------------------------------------------- parsing

# Infobox rows arrive in several table syntaxes; match "Label ... value" leniently.
INFOBOX_FIELDS = {
    "startZone": r"Start\s*Zone",
    "questGiver": r"Quest\s*Giver",
    "minLevel": r"(?:Minimum|Min\.?)\s*Level",
    "classes": r"Classes",
}

LINK = r"\[\[([^\]|#]+)(?:\|[^\]]*)?\]\]"


def strip_links(text):
    return re.sub(LINK, r"\1", text).strip(" '\"|{}")


def parse_infobox_field(wikitext, label_pattern):
    # Actual questTopTable shape (verified in cache): header cell on one line,
    # value cell on the next:
    #   ! ''' Start Zone: '''
    #   | [[Iceclad Ocean]]
    m = re.search(
        r"!\s*'*\s*" + label_pattern + r"\s*:?\s*'*\s*\n\|\s*([^\n]*)", wikitext,
        re.IGNORECASE)
    if not m:   # fallback: same-line "| Start Zone: value" variants
        m = re.search(label_pattern + r"\s*:\s*([^\n|!]+)", wikitext, re.IGNORECASE)
    return strip_links(m.group(1)) if m else ""


def parse_quest(title, wikitext, quest_item_set):
    q = {"name": title,
         "url": "https://eqlwiki.com/" + urllib.parse.quote(title.replace(" ", "_"))}
    for key, pat in INFOBOX_FIELDS.items():
        q[key] = parse_infobox_field(wikitext, pat)
    lvl = re.search(r"\d+", q["minLevel"] or "")
    q["minLevel"] = int(lvl.group(0)) if lvl else 0

    # Turn-in items: only lines that hand something to an NPC, so reward links and
    # narrative item mentions don't pollute the required set.
    items = {}
    for line in wikitext.splitlines():
        if not re.search(r"\b(give|hand|turn\s*in|return)\b", line, re.IGNORECASE):
            continue
        for qty, name in re.findall(r"(\d+)\s*x\s*" + LINK, line):
            name = name.strip()
            items[name] = max(items.get(name, 0), int(qty))
        # Bare links on a give-line with no "N x" prefix count as quantity 1, but
        # ONLY when the wiki's Quest Items category vouches for the name — give-lines
        # also link the receiving NPC, zones, and related mobs, and the category is
        # the cheap arbiter of "this is actually an item".
        for name in re.findall(LINK, line):
            name = name.strip()
            if name in items or name == q["questGiver"]:
                continue
            if re.search(r"\d+\s*x\s*\[\[" + re.escape(name), line):
                continue
            if name not in quest_item_set:
                continue
            items.setdefault(name, 1)
    q["items"] = [{"name": n, "qty": c} for n, c in sorted(items.items())]

    # Rewards: {{:Item}} transclusions plus links on lines under a Reward heading.
    rewards = set(re.findall(r"\{\{:\s*([^}|]+?)\s*\}\}", wikitext))
    reward_section = re.search(r"=+\s*Rewards?\s*=+([^=]*)", wikitext, re.IGNORECASE)
    if reward_section:
        rewards |= {n.strip() for n in re.findall(LINK, reward_section.group(1))}
    q["rewards"] = sorted(rewards)

    # Zone categories double as related-zone hints.
    q["categories"] = sorted(set(re.findall(r"\[\[Category:\s*([^\]|]+)\]\]", wikitext))
                             - {"Quests"})

    # Where the quest happens: the Related Zones infobox row (same two-line table
    # shape as the other fields) plus the start zone. Zone categories join in the
    # converter — non-zone categories can never collide with a real zone name.
    related = re.search(
        r"!\s*'*\s*Related\s*Zones?\s*:?\s*'*\s*\n\|\s*([^\n]*)", wikitext, re.IGNORECASE)
    q["relatedZones"] = sorted({z.strip() for z in
                                re.findall(LINK, related.group(1))} if related else set())

    # Repeatable: the category is the reliable marker; a "Repeatable:" infobox row
    # exists on a couple of pages as backup.
    q["repeatable"] = ("Repeatable Turn-in Quests" in q["categories"]
                       or bool(re.search(r"Repeatable\s*:?\s*'*\s*(?:\n\|\s*)?\s*yes",
                                         wikitext, re.IGNORECASE)))
    return q


def enumerate_quest_items():
    """Category:Quest Items — the wiki's own 'part of a quest' marker (4,148 pages).
    Broader than the turn-in sets we parse: includes quest REWARDS and multi-step
    intermediates, which is exactly the 'don't vendor this' signal the Loot views need."""
    path = HERE / "quest-items.json"
    if path.exists():
        items = json.loads(path.read_text(encoding="utf-8"))
        print(f"quest-items.json cached: {len(items)} items")
        return items
    items = []
    cont = {}
    while True:
        data = api_get({
            "action": "query", "list": "categorymembers",
            "cmtitle": "Category:Quest Items", "cmlimit": "200",
            "cmnamespace": "0", **cont,
        })
        items += [m["title"] for m in data["query"]["categorymembers"]]
        print(f"  enumerated {len(items)} quest items", flush=True)
        if "continue" not in data:
            break
        cont = {"cmcontinue": data["continue"]["cmcontinue"]}
    path.write_text(json.dumps(items, indent=1), encoding="utf-8")
    return items


def main():
    quest_items = enumerate_quest_items()
    titles = enumerate_titles()
    quest_item_set = set(quest_items)
    quests, empty = [], []
    for i, title in enumerate(titles, 1):
        text = fetch_wikitext(title)
        if not text.strip():
            empty.append(title)
            continue
        q = parse_quest(title, text, quest_item_set)
        quests.append(q)
        if i % 25 == 0:
            print(f"  parsed {i}/{len(titles)}", flush=True)

    (HERE / "quests.json").write_text(
        json.dumps(quests, indent=1, ensure_ascii=False), encoding="utf-8")

    with_items = [q for q in quests if q["items"]]
    no_items = [q["name"] for q in quests if not q["items"]]
    no_giver = [q["name"] for q in quests if not q["questGiver"]]
    unique_items = {i["name"] for q in quests for i in q["items"]}
    report = [
        "# Quest harvest report",
        "",
        f"- Quest Items category members: {len(quest_items)}",
        f"- Pages enumerated: {len(titles)}",
        f"- Parsed: {len(quests)} (empty pages: {len(empty)})",
        f"- With turn-in items: {len(with_items)}",
        f"- Unique turn-in item names: {len(unique_items)}",
        f"- Missing quest giver: {len(no_giver)}",
        f"- Backoff events: {len(backoff_events)}",
        "",
        "## Quests with no turn-in items parsed (review these)",
        *[f"- {n}" for n in no_items],
        "",
        "## Missing quest giver",
        *[f"- {n}" for n in no_giver],
    ]
    (HERE / "quests-report.md").write_text("\n".join(report), encoding="utf-8")
    print(f"Done: {len(quests)} quests, {len(unique_items)} unique turn-in items.")
    print(f"No-item pages: {len(no_items)} (see quests-report.md)")


if __name__ == "__main__":
    main()
