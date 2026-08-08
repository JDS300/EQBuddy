# eqltools.com atlas data

`layout-extract.json` is the zone-name map + zoneline pairs from
https://eqltools.com/atlas/world/layout.json (fetched 2026-08-07 via the site,
which parses them from client files of an EverQuest Legends install).

Their sources page (https://eqltools.com/sources): client-mined data, "a link
back is appreciated, not required". We cite eqltools.com in NOTICE. Zone lines
here are walking connections only — boats/oceans come from the eqlwiki
adjacency harvest (see ../eqlwiki/zones-harvest.py); zones-merge.py combines
the two into src/EQBuddy.Core/Data/ZoneGraph.json.
