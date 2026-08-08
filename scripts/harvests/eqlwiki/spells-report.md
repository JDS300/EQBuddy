# eqlwiki.com spell harvest report

Harvested: 2026-08-06 07:50 local, via MediaWiki API at https://eqlwiki.com/api.php

## Enumeration

`list=embeddedin` on `Template:Spellpage` worked; a second page-level template,
`Template:Spellpagesmart`, was also found (Template-namespace prefix search) and harvested.

- Template:Spellpage: 1960 pages
- Template:Spellpagesmart: 996 pages
- Unique spell pages: 1960
- Parsed spells: 1927

Template message field names verified on real pages (Mesmerize, Color Flux, Root, Charm):
`msg_cast_on_you`, `msg_cast_on_other`, `msg_wears_off` — exactly as guessed, no variants found.

## Counts per class

- Bard: 91
- Beastlord: 77
- Cleric: 207
- Druid: 268
- Enchanter: 239
- Magician: 202
- Necromancer: 192
- Paladin: 91
- Ranger: 92
- Rogue: 9
- Shadow Knight: 75
- Shadowknight: 15
- Shaman: 211
- Wizard: 235
- (no parseable class list): 471

Notes: "Shadow Knight" (75) vs "Shadowknight" (15) is a wiki spelling inconsistency,
preserved as-is rather than merged. The 471 spells with no parseable class list are
mostly NPC-only/item-effect spells whose `classes` field is empty or free-text.

The `Someone ` prefix in message fields (note the trailing double space in many, e.g.
`Someone  is stunned by scintillating colors.`) is the wiki's placeholder for the
target's name — in live logs it appears as `<Mobname> is stunned by scintillating colors.`

## Message field coverage

- msg_cast_on_you: 1353 / 1927
- msg_cast_on_other: 1528 / 1927
- msg_wears_off: 972 / 1927

## Distinct msg_cast_on_other for stun/mez/root/charm/lull spells

Family membership inferred from name/description/slot effects (keyword match, listed per family).

### stun (66 distinct messages)

- `Soandso is struck by a shock of lightning.` — Lightning Call
- `Someone  begins to spin from one hundred blows.` — One Hundred Blows
- `Someone  begins to weep.` — Largarn`s Lamentation
- `Someone  convulses.` — Denon's Bereavement
- `Someone  doubles over in pain as the noxious poison enters their lungs.` — Ceticious Cloud
- `Someone  gapes in reverent awe.` — Enforced Reverence
- `Someone  gathers glowing blue strands of mana.` — Harvest
- `Someone  has been engulfed in the maelstrom.` — Storm Strike
- `Someone  has been force struck.` — Force Shock, Force Strike
- `Someone  has been mesmerized.` — Sathir's Mesmerization
- `Someone  has been poisoned.` — System Shock I, System Shock II, System Shock III, System Shock IV, System Shock V
- `Someone  has been struck by lightning.` — Fury of Air
- `Someone  has been struck by the force of Ykesha.` — Ykesha
- `Someone  has been thunder struck.` — Thunderclap
- `Someone  has been thunder-stunned.` — Thunderbold
- `Someone  is assaulted by the wrath of nature.` — Nature's Holy Wrath
- `Someone  is blasted by energy laden winds.` — Force Spiral of Al'Kabor
- `Someone  is blasted by freezing winds.` — Wrath of Ap'Sagor
- `Someone  is blasted by the Vengeance of Al'Kabor.` — Vengeance of Al`Kabor
- `Someone  is burnt by the wrath of the heavens.` — Holy Shock
- `Someone  is caught in a torrent of reckless magic.` — Draught of Jiva
- `Someone  is covered with a light layer of stone.` — Stone Breath
- `Someone  is crushed by a wall of water.` — Tsunami
- `Someone  is dazzled by scintillating colors.` — Color Slant
- `Someone  is deafened.` — Shrieking Howl
- `Someone  is entombed in ice.` — Entomb in Ice
- `Someone  is slammed by a pulse of static energy.` — Jyll`s Static Pulse
- `Someone  is slammed by an intense gust of wind.` — Breath of Karana, Dizzying Wind, Whirling Wind
- `Someone  is struck by a sudden force.` — Force, Markar`s Clash, Markar`s Discord, Stun Command, Tishan's Clash, Tishan`s Discord …
- `Someone  is struck down.` — Divine Wrath
- `Someone  is stunned by scintillating colors.` — Color Flux, Color Shift, Color Skew
- `Someone  is stunned.` — Holy Might, Sound of Force, Stun
- `Someone  is surrounded by fluxing strands of chaos.` — Chaos Flux
- `Someone  lets out a high pitched scream.` — Sonic Scream
- `Someone  looks delirious.` — Sanity Warp
- `Someone  reels from a stunning blow.` — Stunning Blow
- `Someone  reels in pain.` — Brusco's Bombastic Bellow
- `Someone  reels.` — Envenomed Heal
- `Someone  shrieks as their bones are set ablaze.` — Incinerate Bones
- `Someone  staggers with intense pain.` — Stun Breath
- `Someone  stands rigid in pain.` — Fist of Sentience
- `Someone  writhes and staggers.` — The Unspoken Word
- `Someone 's body spasms as the lightning bolt arcs through them.` — Lightning Bolt
- `Someone 's bones freezes and crack.` — Conglaciation of Bone
- `Someone 's brain begins to melt.` — Discordant Mind
- `Someone 's brain begins to smolder.` — Chaotic Feedback
- `Someone 's mind warps.` — Dementia
- `Someone 's skin burns away.` — Ignite Bones
- `Someone 's skin burns.` — Wave of Flame
- `Someone 's skin freezes and cracks off.` — Chill Bones
- `Someone 's skin is torn by the Judgment of Ice.` — Judgment of Ice
- `Someone 's weapons gleam.` — Call of Fire
- `Someone 's world dissolves into anarchy.` — Anarchy
- `Someone Gates.` — Song of Highsun
- `Someone has been struck by a Thunder Bolt.` — Thunder Strike
- `Someone is covered with a light layer of stone.` — Stone Spider Stun
- `Someone is knocked backwards by a concussion of air.` — Call of Sky Strike
- `Someone is smothered in a rolling wave of flame.` — Call of Fire Strike
- `Someone is struck by a sudden force.` — Monkey Stun
- `Someone is stunned by a gust of air.` — Air Elemental Attack
- `Someone staggers as spirits of frost slam against them.` — Blast of Frost, Frost Dagger, Frost Shard, Ice Spear
- `Someone staggers back.` — Static Strike
- `Target has been force struck.` — Force Snap
- `begins to sway!` — Stunning Strike, Stunning Venom
- `is consumed in a magic pulse.` — Static
- `is struck by a sudden force.` — Cease, Desist, Sacred Word

### mez (15 distinct messages)

- `Player gawks at the glowing lights.` — Entrancing Lights
- `Someone  begins to scream.` — Screaming Terror
- `Someone  has been enthralled.` — Enthrall
- `Someone  has been entranced.` — Entrance
- `Someone  has been fascinated.` — Fascination
- `Someone  has been mesmerized by an eerie melody.` — Melodious Befuddlement
- `Someone  has been mesmerized by the Glamour of Kintaz.` — Glamour of Kintaz
- `Someone  has been mesmerized.` — Dazzle, Mesmerization, Mesmerize, Sathir's Mesmerization
- `Someone  is surrounded by a cloud of silence.` — Mesmerizing Breath
- `Someone  looks peaceful.` — Harpy Voice
- `Someone  stumbles toward you.` — Song of Twilight
- `Someone  swoons in raptured bliss.` — Rapture
- `Someone 's eyes glaze over.` — Crission's Pixie Strike
- `Someone 's head nods.` — Kelin's Lucid Lullaby
- `Target's eyes glaze over.` — Sionachie's Dreams

### root (18 distinct messages)

- `'s feet won't budge!` — Grounding Strike
- `Someone  begins to scream.` — Screaming Terror
- `Someone  glances nervously about.` — Wind of Tishanian
- `Someone  is entrapped by roots.` — Entrapping Roots
- `Someone  is stuck to the ground as they begin to regenerate.` — Stalwart Regeneration
- `Someone  is trapped within a whirling wind.` — WhirlBolt, Whirlbolt
- `Someone  sinks into the ground.` — EarthElementalAttack
- `Someone  stumbles.` — GelatRot, Ghoul Root
- `Someone  turns into a tree.` — Spirit of Oak, Treeform
- `Someone 's feet adhere to the ground.` — Fetter, Instill, Paralyzing Earth, Root
- `Someone 's feet become entangled.` — Engorging Roots
- `Someone 's feet become entwined.` — Engulfing Roots, Enveloping Roots, Grasping Roots
- `Someone 's feet sink into the ground.` — Hungry Earth
- `Someone 's image shimmers.` — Illusion: Tree
- `Someone sinks into the ground.` — Earth Elemental Attack
- `Someone's feet adhere to the ground.` — Immobilize
- `Someone's feet become entwined.` — Ensnaring Roots
- `Target is entombed by elemental ice.` — Elnerick's Entombment of Ice

### charm (5 distinct messages)

- `Someone  blinks.` — Allure of the Wild, Befriend Animal, Beguile Animals, Beguile Plants, Call of Karana, Charm Animals …
- `Someone  glances nervously about.` — Wind of Tishanian
- `Someone  has been charmed.` — Alluring Whispers, Beguile, Boltran's Agacerie, Cajoling Whispers, Charm, Dictate …
- `Someone  moans.` — Beguile Undead, Cajole Undead, Dominate Undead, Enslave Death, Thrall of Bones
- `Someone 's eyes glaze over.` — Solon's Bravura, Solon's Song of the Sirens

### lull (13 distinct messages)

- `Player takes on a non-threatening visage.` — Calming Visage
- `Someone  glances nervously about.` — Wind of Tishanian
- `Someone  looks ambivalent.` — Numb the Dead, Rest the Dead
- `Someone  looks less aggressive.` — Calm, Calm Animal, Lull, Pacify, Soothe, Wake of Tranquility
- `Someone  looks peaceful.` — Symphonic Harmony
- `Someone  looks sad.` — Kelin's Lugubrious Lament
- `Someone  looks tranquil.` — Boon of the Clear Mind, Clarity
- `Someone  looks very tranquil.` — Clarity II, Gift of Pure Thought
- `Someone  sighs in tranquility.` — Breeze
- `Someone  stumbles toward you.` — Song of Twilight
- `Someone 's eyes glaze over.` — Crission's Pixie Strike
- `Someone 's head nods.` — Kelin's Lucid Lullaby
- `Target's eyes glaze over.` — Sionachie's Dreams

## Failures

No fetch failures.

Pages where no Spellpage/Spellpagesmart template could be parsed (33).
Verified cause (inspected Bryrym): these are all `Template:Namedmobpage` NPC pages that
embed Template:Spellpage *indirectly* through transcluded spell/loot boxes, so
`list=embeddedin` returns them. They are not spells and were correctly excluded
from spells.json (1960 pages - 33 NPC pages = 1927 spells):
- Bryrym
- Zyerek Onyxblood
- Vyldin Flamereaver
- Quellod Earthspirit
- Malteor Flamecaller
- Kalkar of the Maelstrom
- Carx`Vean
- Gra`Vloren
- Hsrek
- Kal`Vunar
- Kedrak
- Lurian
- Mazrien
- Nir`Tan
- Vukuz
- Yrrindor Emerald Claw
- Yendilor the Cerulean Wing
- Ajorek the Crimson Fang
- A Lava Defender
- A Sky Defender
- An Emerald Defender
- An Onyx Defender
- Beldion Icewind
- Belijor the Emerald Eye
- Cyndor Lightningfang
- Degta`Glis
- Dktan`Nirsl
- Nelaarn the Ebon Claw
- Rlinf`Tae
- Sarek`Relan
- Velcra`Dron
- Wel`Wnas
- Zed`Renzicd

## Throttling / backoff

No throttling encountered; requests paced at ~1/second throughout.
