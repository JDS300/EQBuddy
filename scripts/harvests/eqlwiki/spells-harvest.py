#!/usr/bin/env python3
"""Harvest the EverQuest Legends spell catalog from eqlwiki.com (MediaWiki API).

Polite client: ~1 request/second, exponential backoff on errors/throttling,
resume-safe wikitext cache in cache/. Rerun any time; cached pages are not refetched.

Outputs (all in this script's directory):
  titles.json  - enumerated spell page titles (per source template)
  cache/*.wikitext - raw wikitext per page
  spells.json  - parsed spell array
  report.md    - summary report

Spell page templates on eqlwiki.com (verified 2026-08-06):
  Template:Spellpage and Template:Spellpagesmart
Message fields (verified on Charm/Color Flux/Mesmerize/Root):
  msg_cast_on_you, msg_cast_on_other, msg_wears_off
"""

import hashlib
import json
import re
import sys
import time
import urllib.parse
import urllib.request

BASE = "https://eqlwiki.com/api.php"
UA = "EQBuddy-harvest/1.0 (david.edwards08@gmail.com; polite MediaWiki client; ~1 req/s)"
HERE = __import__("pathlib").Path(__file__).resolve().parent
CACHE = HERE / "cache"
CACHE.mkdir(exist_ok=True)
PACE_SECONDS = 1.0
TEMPLATES = ["Template:Spellpage", "Template:Spellpagesmart"]

_last_request = [0.0]
backoff_events = []  # noted in report


def api_get(params):
    """GET with pacing and exponential backoff. Returns parsed JSON."""
    params = dict(params, format="json")
    url = BASE + "?" + urllib.parse.urlencode(params)
    delay = 2.0
    for attempt in range(7):
        # pacing
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
    titles_path = HERE / "titles.json"
    if titles_path.exists():
        data = json.loads(titles_path.read_text(encoding="utf-8"))
        print(f"titles.json cached: {sum(len(v) for v in data.values())} titles")
        return data
    result = {}
    for tmpl in TEMPLATES:
        titles = []
        cont = {}
        while True:
            params = {"action": "query", "list": "embeddedin", "eititle": tmpl,
                      "eilimit": "500", "einamespace": "0"}
            params.update(cont)
            data = api_get(params)
            for row in data["query"]["embeddedin"]:
                titles.append(row["title"])
            print(f"{tmpl}: {len(titles)} so far")
            if "continue" in data:
                cont = {"eicontinue": data["continue"]["eicontinue"]}
            else:
                break
        result[tmpl] = titles
    titles_path.write_text(json.dumps(result, indent=1), encoding="utf-8")
    return result


# ---------------------------------------------------------------- fetching

def slug_for(title):
    h = hashlib.md5(title.encode("utf-8")).hexdigest()[:8]
    safe = re.sub(r"[^A-Za-z0-9 '`().,_-]", "_", title).strip(" .")
    return f"{safe}.{h}"


def fetch_all(titles):
    """Fetch raw wikitext for every title, batches of 50, cache per page."""
    missing = [t for t in titles if not (CACHE / (slug_for(t) + ".wikitext")).exists()]
    print(f"{len(titles)} pages total, {len(titles)-len(missing)} cached, {len(missing)} to fetch")
    fetch_failures = []
    for i in range(0, len(missing), 50):
        batch = missing[i:i + 50]
        data = api_get({"action": "query", "prop": "revisions", "rvprop": "content",
                        "rvslots": "main", "titles": "|".join(batch)})
        pages = data["query"]["pages"]
        # map normalized titles back to requested ones
        norm = {n["to"]: n["from"] for n in data["query"].get("normalized", [])}
        got = set()
        for p in pages.values():
            title = p.get("title", "")
            requested = norm.get(title, title)
            revs = p.get("revisions")
            if not revs:
                fetch_failures.append(requested)
                continue
            text = revs[0].get("slots", {}).get("main", {}).get("*")
            if text is None:
                fetch_failures.append(requested)
                continue
            (CACHE / (slug_for(requested) + ".wikitext")).write_text(text, encoding="utf-8")
            got.add(requested)
        for t in batch:
            if t not in got and t not in fetch_failures:
                fetch_failures.append(t)
        print(f"fetched {min(i+50, len(missing))}/{len(missing)}")
    return fetch_failures


# ---------------------------------------------------------------- parsing

def split_template_params(body):
    """Split template body on top-level pipes (aware of {{ }} and [[ ]])."""
    parts, depth, buf = [], 0, []
    i, n = 0, len(body)
    while i < n:
        two = body[i:i + 2]
        if two in ("{{", "[["):
            depth += 1
            buf.append(two)
            i += 2
        elif two in ("}}", "]]"):
            depth -= 1
            buf.append(two)
            i += 2
        elif body[i] == "|" and depth == 0:
            parts.append("".join(buf))
            buf = []
            i += 1
        else:
            buf.append(body[i])
            i += 1
    parts.append("".join(buf))
    return parts


def extract_spell_template(text):
    """Find {{Spellpage|...}} or {{Spellpagesmart|...}}; return (name, body) or None."""
    for m in re.finditer(r"\{\{\s*(Spellpagesmart|Spellpage)\s*\|", text):
        start = m.start()
        depth = 0
        i = start
        while i < len(text) - 1:
            if text[i:i + 2] == "{{":
                depth += 1
                i += 2
            elif text[i:i + 2] == "}}":
                depth -= 1
                i += 2
                if depth == 0:
                    body = text[m.end():i - 2]
                    return m.group(1), body
            else:
                i += 1
    return None


def parse_params(body):
    params = {}
    for part in split_template_params(body):
        if "=" not in part:
            continue
        k, v = part.split("=", 1)
        k = k.strip()
        if k:
            params[k] = v.strip()
    return params


def parse_classes(raw):
    """Parse '* [[Enchanter]] - Level 11 (Autogranted)' bullet lines."""
    if not raw:
        return []
    out = []
    for m in re.finditer(
            r"\[\[\s*([^\]|#]+?)(?:\s*\|[^\]]*)?\s*\]\]\s*(?:-|–|—)?\s*Level\s*(\d+)\s*(\(([^)]*)\))?",
            raw, re.IGNORECASE):
        out.append({"class": m.group(1).strip(), "level": int(m.group(2)),
                    "note": m.group(4).strip() if m.group(4) else None})
    return out


def parse_number(raw):
    if raw is None:
        return None
    m = re.match(r"^\s*(\d+(?:\.\d+)?)\s*$", raw)
    return float(m.group(1)) if m else None


def parse_duration_seconds(raw):
    """Best-effort duration -> seconds. Returns None when not confidently parseable."""
    if not raw:
        return None
    s = raw.strip()
    if re.fullmatch(r"instant\.?", s, re.IGNORECASE):
        return 0.0
    # reject anything with templates/links/level-dependence
    if "{{" in s or "[[" in s or "@" in s or re.search(r"level|varies|up to", s, re.IGNORECASE):
        return None
    total, matched_len = 0.0, 0
    for m in re.finditer(r"(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|ticks?)\b",
                         s, re.IGNORECASE):
        n = float(m.group(1))
        unit = m.group(2).lower()
        if unit.startswith(("hour", "hr")) or unit == "h":
            total += n * 3600
        elif unit.startswith("min") or unit == "m":
            total += n * 60
        elif unit.startswith("tick"):
            total += n * 6
        else:
            total += n
        matched_len += len(m.group(0))
    # accept only if the string is mostly those tokens (defensive)
    residue = re.sub(r"[\s,()+-]", "", s)
    if matched_len == 0:
        return None
    consumed = sum(len(re.sub(r"\s", "", m.group(0))) for m in re.finditer(
        r"(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|ticks?)\b", s, re.IGNORECASE))
    if consumed < len(residue):
        return None
    return total


def parse_resist(raw):
    if not raw:
        return None
    m = re.match(r"^\s*([A-Za-z /]+?)\s*(?:\(\s*(-?\d+)\s*\))?\s*$", raw)
    if not m:
        return None
    return {"type": m.group(1).strip(), "modifier": int(m.group(2)) if m.group(2) else None}


def strip_wiki(s):
    if s is None:
        return None
    s = re.sub(r"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", r"\1", s)
    s = re.sub(r"'''?", "", s)
    return s.strip() or None


def parse_spell(title, text, source_template_hint):
    found = extract_spell_template(text)
    if not found:
        return None
    tmpl_name, body = found
    p = parse_params(body)

    def raw(key):
        v = p.get(key)
        return v if v not in ("", None) else None

    spell_type_raw = raw("spell_type")
    beneficial = None
    if spell_type_raw:
        low = spell_type_raw.lower()
        if "benef" in low:
            beneficial = True
        elif "detri" in low:
            beneficial = False

    slots_raw = raw("slots")
    slot_effects = []
    if slots_raw:
        for m in re.finditer(r"\{\{\s*SpellSlotRow(?:Smart)?\s*\|\s*(\d+)\s*\|\s*([^|}]+?)\s*(?:\|[^}]*)?\}\}",
                             slots_raw):
            slot_effects.append({"slot": int(m.group(1)), "effect": m.group(2).strip()})

    return {
        "name": raw("spellname") or title,
        "page_title": title,
        "template": tmpl_name,
        "classes": parse_classes(raw("classes")),
        "classes_raw": raw("classes"),
        "school": strip_wiki(raw("skill")),
        "school_raw": raw("skill"),
        "target_type": raw("target_type"),
        "spell_type_raw": spell_type_raw,
        "beneficial": beneficial,
        "mana": parse_number(raw("mana")),
        "mana_raw": raw("mana"),
        "cast_time_seconds": parse_number(raw("casting_time")),
        "casting_time_raw": raw("casting_time"),
        "recast_seconds": parse_number(raw("recast_time")),
        "recast_time_raw": raw("recast_time"),
        "duration_raw": raw("duration"),
        "duration_seconds": parse_duration_seconds(raw("duration")),
        "resist_raw": raw("resist"),
        "resist": parse_resist(raw("resist")),
        "range_raw": raw("range"),
        "description": raw("description"),
        "slot_effects": slot_effects,
        "msg_cast_on_you": raw("msg_cast_on_you"),
        "msg_cast_on_other": raw("msg_cast_on_other"),
        "msg_wears_off": raw("msg_wears_off"),
    }


# ---------------------------------------------------------------- CC classification

CC_FAMILIES = {
    "stun": re.compile(r"\bstun", re.IGNORECASE),
    "mez": re.compile(r"\bmesmeri|\bmez\b", re.IGNORECASE),
    "root": re.compile(r"\broot\b|prevent(?:ing|s)? them from moving|adhere", re.IGNORECASE),
    "charm": re.compile(r"\bcharm", re.IGNORECASE),
    "lull": re.compile(r"\blull|\bpacif|\bcalm|\bsoothe|\bharmony|\bassuag|reaction radius|aggro radius|\bwake of tranq", re.IGNORECASE),
}


def cc_families_for(spell):
    hay = " ".join(filter(None, [
        spell["name"],
        spell.get("description") or "",
        " ".join(e["effect"] for e in spell.get("slot_effects", [])),
    ]))
    return [fam for fam, rx in CC_FAMILIES.items() if rx.search(hay)]


# ---------------------------------------------------------------- main

def main():
    titles_by_tmpl = enumerate_titles()
    seen = set()
    all_titles = []
    tmpl_of = {}
    for tmpl, titles in titles_by_tmpl.items():
        for t in titles:
            if t not in seen:
                seen.add(t)
                all_titles.append(t)
                tmpl_of[t] = tmpl

    fetch_failures = fetch_all(all_titles)

    spells, parse_failures = [], []
    for t in all_titles:
        path = CACHE / (slug_for(t) + ".wikitext")
        if not path.exists():
            continue
        text = path.read_text(encoding="utf-8")
        spell = parse_spell(t, text, tmpl_of.get(t))
        if spell is None:
            parse_failures.append(t)
        else:
            spell["cc_families"] = cc_families_for(spell)
            spells.append(spell)

    spells.sort(key=lambda s: s["name"].lower())
    (HERE / "spells.json").write_text(
        json.dumps(spells, indent=1, ensure_ascii=False), encoding="utf-8")

    write_report(spells, titles_by_tmpl, fetch_failures, parse_failures)
    print(f"done: {len(spells)} spells parsed, {len(parse_failures)} parse failures, "
          f"{len(fetch_failures)} fetch failures")


def write_report(spells, titles_by_tmpl, fetch_failures, parse_failures):
    lines = []
    A = lines.append
    A("# eqlwiki.com spell harvest report")
    A("")
    A(f"Harvested: {time.strftime('%Y-%m-%d %H:%M')} local, via MediaWiki API at {BASE}")
    A("")
    A("## Enumeration")
    A("")
    A("`list=embeddedin` on `Template:Spellpage` worked; a second page-level template,")
    A("`Template:Spellpagesmart`, was also found (Template-namespace prefix search) and harvested.")
    A("")
    for tmpl, titles in titles_by_tmpl.items():
        A(f"- {tmpl}: {len(titles)} pages")
    A(f"- Unique spell pages: {len(set(t for v in titles_by_tmpl.values() for t in v))}")
    A(f"- Parsed spells: {len(spells)}")
    A("")
    A("Template message field names verified on real pages (Mesmerize, Color Flux, Root, Charm):")
    A("`msg_cast_on_you`, `msg_cast_on_other`, `msg_wears_off` — exactly as guessed, no variants found.")
    A("")

    A("## Counts per class")
    A("")
    per_class = {}
    no_class = 0
    for s in spells:
        if not s["classes"]:
            no_class += 1
        for c in s["classes"]:
            per_class[c["class"]] = per_class.get(c["class"], 0) + 1
    for cls in sorted(per_class):
        A(f"- {cls}: {per_class[cls]}")
    A(f"- (no parseable class list): {no_class}")
    A("")

    A("## Message field coverage")
    A("")
    for f in ("msg_cast_on_you", "msg_cast_on_other", "msg_wears_off"):
        n = sum(1 for s in spells if s[f])
        A(f"- {f}: {n} / {len(spells)}")
    A("")

    A("## Distinct msg_cast_on_other for stun/mez/root/charm/lull spells")
    A("")
    A("Family membership inferred from name/description/slot effects (keyword match, listed per family).")
    A("")
    for fam in ("stun", "mez", "root", "charm", "lull"):
        msgs = {}
        for s in spells:
            if fam in s.get("cc_families", []) and s["msg_cast_on_other"]:
                msgs.setdefault(s["msg_cast_on_other"], []).append(s["name"])
        A(f"### {fam} ({len(msgs)} distinct messages)")
        A("")
        for msg in sorted(msgs):
            examples = sorted(set(msgs[msg]))
            shown = ", ".join(examples[:6]) + (" …" if len(examples) > 6 else "")
            A(f"- `{msg}` — {shown}")
        A("")

    A("## Failures")
    A("")
    if fetch_failures:
        A(f"Fetch failures ({len(fetch_failures)}):")
        for t in fetch_failures:
            A(f"- {t}")
    else:
        A("No fetch failures.")
    A("")
    if parse_failures:
        A(f"Pages where no Spellpage/Spellpagesmart template could be parsed ({len(parse_failures)}):")
        for t in parse_failures:
            A(f"- {t}")
    else:
        A("No template parse failures.")
    A("")

    A("## Throttling / backoff")
    A("")
    if backoff_events:
        A(f"{len(backoff_events)} backoff events occurred:")
        for e in backoff_events:
            A(f"- {e}")
    else:
        A("No throttling encountered; requests paced at ~1/second throughout.")
    A("")

    (HERE / "report.md").write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
