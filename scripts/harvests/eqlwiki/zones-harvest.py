#!/usr/bin/env python3
"""Harvest the EverQuest Legends zone adjacency graph from eqlwiki.com.

Same polite client as quests-harvest.py (~1 req/s, cache/, resume-safe).
Zone pages carry an infobox row:
    ! ''' Adjacent Zones: '''
    | [[Felwithe]], [[Butcherblock Mountains]], ... [[Kelethin]] (within the zone)

Outputs:
  zone-titles.json  - members of Category:Zones
  cache/zone-*.wikitext
  zones.json        - {zone: [adjacent zones]}
  zones-report.md
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
            print(f"  ! attempt {attempt+1} failed ({e!r}); backing off {delay:.0f}s", flush=True)
            if code is not None and code not in (429, 500, 502, 503, 504):
                raise
            time.sleep(delay)
            delay *= 2
    raise RuntimeError("Giving up: " + url)


def enumerate_titles():
    path = HERE / "zone-titles.json"
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    titles, cont = [], {}
    while True:
        data = api_get({"action": "query", "list": "categorymembers",
                        "cmtitle": "Category:Zones", "cmlimit": "200",
                        "cmnamespace": "0", **cont})
        titles += [m["title"] for m in data["query"]["categorymembers"]]
        print(f"  enumerated {len(titles)} zones", flush=True)
        if "continue" not in data:
            break
        cont = {"cmcontinue": data["continue"]["cmcontinue"]}
    path.write_text(json.dumps(titles, indent=1), encoding="utf-8")
    return titles


def fetch_wikitext(title):
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", title)
    path = CACHE / f"zone-{safe}.wikitext"
    if path.exists():
        return path.read_text(encoding="utf-8")
    data = api_get({"action": "query", "prop": "revisions", "rvprop": "content",
                    "redirects": "1", "titles": title})
    page = next(iter(data["query"]["pages"].values()))
    revs = page.get("revisions")
    text = revs[0]["*"] if revs else ""
    path.write_text(text, encoding="utf-8")
    return text


LINK = r"\[\[([^\]|#]+)(?:\|[^\]]*)?\]\]"


def parse_adjacent(wikitext):
    m = re.search(r"!\s*'*\s*Adjacent\s*Zones?\s*:?\s*'*\s*\n\|\s*([^\n]*)", wikitext,
                  re.IGNORECASE)
    if not m:
        m = re.search(r"Adjacent\s*Zones?\s*:\s*([^\n!]+)", wikitext, re.IGNORECASE)
    if not m:
        return []
    return [z.strip() for z in re.findall(LINK, m.group(1)) if z.strip()]


def main():
    titles = enumerate_titles()
    graph, missing = {}, []
    for i, title in enumerate(titles, 1):
        adj = parse_adjacent(fetch_wikitext(title))
        if adj:
            graph[title] = adj
        else:
            missing.append(title)
        if i % 25 == 0:
            print(f"  parsed {i}/{len(titles)}", flush=True)

    # Adjacency should be symmetric; wiki pages drift. Mirror every edge whose
    # endpoint is a known zone page so BFS never depends on which page was tidier.
    known = set(titles)
    for zone, adj in list(graph.items()):
        for n in adj:
            if n in known:
                graph.setdefault(n, [])
                if zone not in graph[n]:
                    graph[n].append(zone)

    (HERE / "zones.json").write_text(
        json.dumps(graph, indent=1, ensure_ascii=False), encoding="utf-8")
    edges = sum(len(v) for v in graph.values())
    report = [
        "# Zone harvest report", "",
        f"- Zone pages: {len(titles)}",
        f"- With adjacency: {len(graph)} ({edges} directed edges after mirroring)",
        f"- No adjacency parsed: {len(missing)}", "",
        "## Zones with no adjacency parsed",
        *[f"- {z}" for z in missing],
    ]
    (HERE / "zones-report.md").write_text("\n".join(report), encoding="utf-8")
    print(f"Done: {len(graph)} zones, {edges} edges; {len(missing)} without adjacency.")


if __name__ == "__main__":
    main()
