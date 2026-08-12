# Derived Durations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a countdown for DoTs and debuffs that have never been measured, derived from the wiki's base duration scaled by spell rank and marked as an estimate — and stop reporting every measured DoT one tick too long.

**Architecture:** A build-time promote script reduces the existing eqlwiki harvest to an embedded `SpellDurations.json`. `SpellDurationCatalog` resolves a cast name to a duration, asking the catalog for an exact name match before it treats a trailing Roman numeral as a rank. `DebuffTracker` gains a three-tier expiry lookup (per-rank samples → catalog-derived → null) and starts keying effects by base name while displaying the ranked name.

**Tech Stack:** C# / .NET 10, xUnit, Python 3 for the promote script (stdlib only, matching `quests-promote.py`).

## Global Constraints

- **Trust order is absolute:** Measured > Derived > Unknown. A measurement is never adjusted toward the catalog. Tepid Deeds keeps its measured ~126s against a catalog 150s.
- **No runtime network.** The catalog is an embedded resource. Do not add an HTTP client.
- **Never invent a number.** A spell with no catalog entry, or an unknown rank, shows `--`.
- **Duration formula:** `duration = base × (1 + 0.10 × tier)` — additive, not compounding.
- **Build/test commands** (the WPF app cannot build on this box — never run `dotnet build EQBuddy.slnx`):
  - `dotnet build src/EQBuddy.Avalonia/EQBuddy.Avalonia.csproj -c Release`
  - `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`
  - `dotnet test tests/EQBuddy.Avalonia.Tests/EQBuddy.Avalonia.Tests.csproj -c Release`
- **`dotnet test` can exit 0 despite `[FATAL ERROR] Catastrophic failure` — read the test count, never trust the exit code.** Baseline on branch `derived-durations`, verified 2026-08-12: **`EQBuddy.Tests` 842**, **`EQBuddy.Avalonia.Tests` 81** (923 combined). Every per-task count below is for `EQBuddy.Tests` alone.
- **Never run the Avalonia tests while the app is running.** Check `ps -C EQBuddy.Avalonia` first.
- Test line shapes must be verbatim from `tests/fixtures/eqlog_Daggo_freeport.txt`.

---

### Task 1: Drop the phantom trailing tick

The no-fade measurement path adds a `ServerTickSeconds` that the log says is not there. `OnFade` computes `fade − LandedAt` and is already correct; after this change both paths agree at 48s for Immolate.

**Files:**
- Modify: `src/EQBuddy.Core/DebuffTracker.cs:60-62` (delete `ServerTickSeconds`), `:242-246` (`Record(DebuffState)`)
- Test: `tests/EQBuddy.Tests/DebuffTrackerTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `DebuffTracker.LearnedDurations` now reports `lastTick − firstTick` for tick-retired effects. `DebuffTracker.ServerTickSeconds` is **deleted** — no later task may reference it. (`MezTracker.ServerTickSeconds` is a separate constant and stays.)

- [ ] **Step 1: Write the failing test**

Add to `tests/EQBuddy.Tests/DebuffTrackerTests.cs`:

```csharp
/// <summary>Immolate's wiki duration is 48s, and in the fixture its fade line arrives at the
/// last tick, not a tick after it: nine ticks spanning 48s, then "worn off" in the same second.
/// The tick-retired path used to add a phantom trailing tick and teach 54s - the number that
/// looked like a 6s anchoring error and is not one.</summary>
[Fact]
public void ATickRetiredDotMeasuresFirstTickToLastTick()
{
    var tracker = new DebuffTracker();
    for (var i = 0; i <= 48; i += 6)
        tracker.Apply(Tick("a sand giant", "Immolate", i));

    // Ticks stop; the effect retires once TickGrace has passed.
    tracker.Active(T0.AddSeconds(48 + 13));

    Assert.Equal(48, tracker.LearnedDurations["Immolate"]);
}

/// <summary>The fade path and the tick-retired path must agree. They measure the same event
/// by different evidence, so a disagreement means one of them is wrong.</summary>
[Fact]
public void TheFadePathAndTheTickPathMeasureTheSameDuration()
{
    var faded = new DebuffTracker();
    for (var i = 0; i <= 48; i += 6)
        faded.Apply(Tick("a sand giant", "Immolate", i));
    faded.Apply(new SpellWornOffEvent(T0.AddSeconds(48), "Immolate", "a sand giant"));

    var ticked = new DebuffTracker();
    for (var i = 0; i <= 48; i += 6)
        ticked.Apply(Tick("a sand giant", "Immolate", i));
    ticked.Active(T0.AddSeconds(48 + 13));

    Assert.Equal(faded.LearnedDurations["Immolate"], ticked.LearnedDurations["Immolate"]);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~DebuffTrackerTests"`

Expected: FAIL. `ATickRetiredDotMeasuresFirstTickToLastTick` reports `Assert.Equal() Failure: Expected: 48, Actual: 54`. `TheFadePathAndTheTickPathMeasureTheSameDuration` reports `Expected: 48, Actual: 54`.

- [ ] **Step 3: Delete the constant**

In `src/EQBuddy.Core/DebuffTracker.cs`, delete these three lines (the `<summary>` and the `const`):

```csharp
    /// <summary>A DoT ticks on the six-second server heartbeat, and the first tick lands one
    /// heartbeat after the cast, so a cast's length is (last - first) + one tick.</summary>
    public const double ServerTickSeconds = 6;
```

- [ ] **Step 4: Fix the measurement**

Replace `Record(DebuffState state)`:

```csharp
    private void Record(DebuffState state)
    {
        if (state.LastTickAt <= state.LandedAt) return;   // a single tick measures nothing
        Record(state.Spell, (state.LastTickAt - state.LandedAt).TotalSeconds);
    }
```

Then correct the class docstring, which currently states the wrong model. Replace the paragraph beginning "The log never states a duration" with:

```csharp
/// The log never states a duration, but it does not have to. The first tick IS the landing,
/// ticks arrive every ~6 seconds naming the spell, and the last tick falls on the expiry - the
/// fade line arrives in the same second, not one tick later. So a completed cast measures
/// itself as first tick to last tick, and that measurement drives the NEXT cast of the same
/// spell, which is why the first cast of anything shows no countdown and every one after does.
///
/// Measured across the 690k-line fixture (six DoTs, 138 completed casts): first-tick-to-fade
/// equals the wiki duration exactly for every spell whose wiki value is given in exact seconds
/// or ticks - Immolate 48, Drones of Doom 48, Gasping Embrace 48, Stinging Swarm 54. Anchoring
/// on the CAST line instead matches none of them, running long by each spell's own cast time
/// (Immolate 2.5s, Shiftless Deeds 6.0s), which is why the error is not a constant six seconds.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`

Expected: PASS **only after the step below**. Four existing tests encode the phantom tick and must be corrected — they are the regression evidence, not collateral damage. Make exactly these edits in `tests/EQBuddy.Tests/DebuffTrackerTests.cs`, changing expected values and their explanatory comments only:

| Test | Tick span | Was | Becomes |
|---|---|---|---|
| `ADurationLearnedFromOneCastCountsDownTheNext` | 0→48 | `54` (twice: `LearnedDurations` and `RemainingSeconds`) | `48` |
| `RecastingRestartsTheClockRatherThanExtendingIt` | 36→90 | `60` | `54` |
| `TheRepeatedMeasurementWinsOverAnOddOne` | 0→48 | `54` | `48` |
| `AGapBetweenTicksEndsTheEffectEvenWithoutAnActiveCall` | 0→6 | `12` | `6` |

Also update the inline comments that state the old arithmetic:
- In `RecastingRestartsTheClockRatherThanExtendingIt`: `// 36..90 is the second cast: 54s + the tick already paid for = 60, not 96.` becomes `// 36..90 is the second cast: 54s, not 96. The last tick falls on the expiry.`
- In `TheRepeatedMeasurementWinsOverAnOddOne`: the `// 54s` comments become `// 48s`, and `// 24s - the odd one out` becomes `// 18s - the odd one out`.

**These four must NOT change:** `AFadeLineEndsTheEffectAndMeasuresItExactly` (54), `ATheirSlowBorrowsADurationYouMeasuredYourself` (60), `ASlowSurvivesLongerThanTheTickGapAndIsEndedByItsFade` (60), `AnUnmeasuredSpellHasNoCountdownRatherThanAGuess` (nulls). They all exercise the fade path or the no-duration path, which this task does not touch — if any of them fails, **stop and report**, because that means the change reached further than intended.

Then run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`

Expected: PASS. Read the total — it must be 844 (842 baseline + 2 new), with 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/EQBuddy.Core/DebuffTracker.cs tests/EQBuddy.Tests/DebuffTrackerTests.cs
git commit -m "The DoT measurement that was one tick long"
```

---

### Task 2: Rank parsing

A pure string helper: split a trailing Roman numeral off a spell name. It makes no judgement about whether the numeral is a rank — Task 3 owns that decision, because only the catalog can tell `Shiftless Deeds IV` from `Clarity II`.

**Files:**
- Create: `src/EQBuddy.Core/SpellRank.cs`
- Test: `tests/EQBuddy.Tests/SpellRankTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `public static (string Base, int Tier) SpellRank.Split(string name)` — `("Shiftless Deeds", 4)` for `"Shiftless Deeds IV"`; `(name.Trim(), 0)` when there is no trailing numeral.
  - `public static double SpellRank.Scale(double baseSeconds, int tier)`
  - `public const double SpellRank.PerTier = 0.10`

- [ ] **Step 1: Write the failing test**

Create `tests/EQBuddy.Tests/SpellRankTests.cs`:

```csharp
using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// Splitting a rank off a spell name. This helper deliberately does NOT decide whether the
/// numeral it found is a rank at all - 121 spells in the wiki catalog are genuinely NAMED with
/// a trailing numeral ("Clarity II", "Burnout IV"), and only the catalog can tell them apart.
/// See SpellDurationCatalog.
/// </summary>
public class SpellRankTests
{
    [Theory]
    [InlineData("Shiftless Deeds IV", "Shiftless Deeds", 4)]
    [InlineData("Mesmerization V", "Mesmerization", 5)]
    [InlineData("Shiftless Deeds VI", "Shiftless Deeds", 6)]
    [InlineData("Beguile II", "Beguile", 2)]
    [InlineData("Heroic Leap I", "Heroic Leap", 1)]
    [InlineData("Efflorescing Heal III", "Efflorescing Heal", 3)]
    public void ATrailingRomanNumeralIsSplitOff(string name, string expectedBase, int expectedTier)
    {
        Assert.Equal((expectedBase, expectedTier), SpellRank.Split(name));
    }

    [Theory]
    [InlineData("Immolate")]
    [InlineData("Drifting Death")]
    [InlineData("Vengeance of the Wild")]
    public void AnUnrankedNameIsTierZero(string name)
    {
        Assert.Equal((name, 0), SpellRank.Split(name));
    }

    /// <summary>Real spell words that happen to be Roman letters must not be eaten. "Ice" is a
    /// spell Daggo casts 285 times in the fixture; "Mix" and "Dim" are Roman-parseable strings.</summary>
    [Theory]
    [InlineData("Ice")]
    [InlineData("Mana Sieve")]
    [InlineData("Chaos Flux")]
    public void ASingleWordNameIsNeverTreatedAsARank(string name)
    {
        Assert.Equal((name, 0), SpellRank.Split(name));
    }

    /// <summary>The formula is additive, not compounding: 1.1^6 is 1.77, and Shiftless Deeds VI
    /// shows exactly 4 minutes in game against a 150s base.</summary>
    [Theory]
    [InlineData(150, 6, 240)]   // Shiftless Deeds VI - 4 min, confirmed in game
    [InlineData(150, 4, 210)]   // Shiftless Deeds IV
    [InlineData(24, 5, 36)]     // Mesmerization V - measured ~36s
    [InlineData(48, 0, 48)]     // unranked is untouched
    public void DurationScalesTenPercentPerTier(double baseSeconds, int tier, double expected)
    {
        Assert.Equal(expected, SpellRank.Scale(baseSeconds, tier), precision: 6);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~SpellRankTests"`

Expected: FAIL to compile — `The name 'SpellRank' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/EQBuddy.Core/SpellRank.cs`:

```csharp
namespace EQBuddy.Core;

/// <summary>
/// The rank suffix on a spell name. EverQuest Legends adds roman-numeral ranks to spells
/// ("Shiftless Deeds IV"), and each tier adds 10% duration - additive, confirmed three ways:
/// Shiftless Deeds VI shows 4 minutes in game against a 150s base (x1.6), the wiki's Spell
/// Level slider reads "Duration +60%" at level 6, and Mesmerization V measures ~36s against a
/// 24s base (x1.5). Compounding would give 1.1^6 = 1.77, which matches no observed value.
///
/// This splitter is deliberately naive about WHETHER the numeral is a rank. 121 spells in the
/// wiki catalog are genuinely named with a trailing numeral - "Clarity II", "Burnout IV",
/// "Cannibalize IV" - and are not ranks of anything. Only the catalog can tell the two apart,
/// so that call lives in <see cref="SpellDurationCatalog"/>.
/// </summary>
public static class SpellRank
{
    /// <summary>Duration added per rank tier.</summary>
    public const double PerTier = 0.10;

    /// <summary>Splits a trailing roman numeral off a name. Returns tier 0 when there is none.
    /// A single-word name is never split: the whole name would vanish, and "Ice" is a spell.</summary>
    public static (string Base, int Tier) Split(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space <= 0) return (trimmed, 0);

        var tier = ParseRoman(trimmed[(space + 1)..]);
        return tier > 0 ? (trimmed[..space], tier) : (trimmed, 0);
    }

    public static double Scale(double baseSeconds, int tier) =>
        baseSeconds * (1 + PerTier * tier);

    /// <summary>I..XXXIX, or 0 for anything that is not a well-formed roman numeral. Only
    /// I/V/X are accepted - L and beyond cannot be a spell rank, and "Cazic" should not be
    /// read as a number because it starts with C.</summary>
    private static int ParseRoman(string token)
    {
        if (token.Length is 0 or > 6) return 0;
        var total = 0; var previous = 0;
        for (var i = token.Length - 1; i >= 0; i--)
        {
            var value = token[i] switch { 'I' => 1, 'V' => 5, 'X' => 10, _ => 0 };
            if (value == 0) return 0;
            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }
        // Round-trips only for canonical spellings, so "IIII" and "VV" are rejected.
        return total is > 0 and < 40 && ToRoman(total) == token ? total : 0;
    }

    private static string ToRoman(int value)
    {
        var result = "";
        foreach (var (number, symbol) in
            (ReadOnlySpan<(int, string)>)[(10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")])
            while (value >= number) { result += symbol; value -= number; }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~SpellRankTests"`

Expected: PASS, 16 tests (4 theories, 16 cases).

- [ ] **Step 5: Commit**

```bash
git add src/EQBuddy.Core/SpellRank.cs tests/EQBuddy.Tests/SpellRankTests.cs
git commit -m "Ranks add ten percent each, and Ice is not a numeral"
```

---

### Task 3: The duration catalog

Promote the existing harvest into an embedded catalog, and resolve a cast name against it. The catalog is the authority on names: an exact hit always beats a rank interpretation.

**Files:**
- Create: `scripts/harvests/eqlwiki/spells-promote.py`
- Create: `src/EQBuddy.Core/Data/SpellDurations.json` (generated by that script)
- Create: `src/EQBuddy.Core/SpellDurationCatalog.cs`
- Modify: `src/EQBuddy.Core/EQBuddy.Core.csproj:19-27` (add the `EmbeddedResource`)
- Test: `tests/EQBuddy.Tests/SpellDurationCatalogTests.cs`

**Interfaces:**
- Consumes: `SpellRank.Split`, `SpellRank.Scale` from Task 2
- Produces:
  - `public enum DurationCertainty { Unknown, Derived, Measured }`
  - `public sealed record ResolvedDuration(string BaseName, int Tier, double Seconds)`
  - `public sealed class SpellDurationCatalog`
    - `public SpellDurationCatalog(IReadOnlyDictionary<string, double>? durations = null)`
    - `public static SpellDurationCatalog Embedded { get; }` — lazily loaded shared instance
    - `public ResolvedDuration? Resolve(string castName)` — null when the catalog cannot answer
    - `public string BaseNameOf(string castName)` — the catalog-aware base name, used as a tracking key

- [ ] **Step 1: Write the promote script**

Create `scripts/harvests/eqlwiki/spells-promote.py`:

```python
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
```

- [ ] **Step 2: Run it and sanity-check the output**

```bash
python3 scripts/harvests/eqlwiki/spells-promote.py
python3 -c "
import json;d=json.load(open('src/EQBuddy.Core/Data/SpellDurations.json'))['durations']
print('entries',len(d))
for n in ['Shiftless Deeds','Tepid Deeds','Immolate','Mesmerization','Clarity II','Burnout IV']:
    print(f'  {n}: {d.get(n)}')
print('Cripple present?', 'Cripple' in d)
"
```

Expected: **exactly 680 entries**; `Shiftless Deeds: 150.0`, `Tepid Deeds: 150.0`, `Immolate: 48.0`, `Mesmerization: 24.0`, `Clarity II: 2100.0`, `Burnout IV: 900.0`; `Cripple present? False`.

If any value differs, **stop and report** — the catalog is the evidence base for the whole feature.

- [ ] **Step 3: Register the embedded resource**

In `src/EQBuddy.Core/EQBuddy.Core.csproj`, alongside the existing `Data\*.json` entries, add:

```xml
    <EmbeddedResource Include="Data\SpellDurations.json" />
```

- [ ] **Step 4: Write the failing test**

Create `tests/EQBuddy.Tests/SpellDurationCatalogTests.cs`:

```csharp
using EQBuddy.Core;
using Xunit;

namespace EQBuddy.Tests;

/// <summary>
/// Resolving a cast name to a base duration. The whole contract is the ordering: an exact
/// catalog hit beats a rank interpretation, because 121 wiki spells are genuinely NAMED with a
/// trailing numeral and reading "Clarity II" as tier-2 Clarity would scale a duration the
/// catalog already knows exactly.
/// </summary>
public class SpellDurationCatalogTests
{
    private static readonly SpellDurationCatalog Catalog = new(new Dictionary<string, double>
    {
        ["Shiftless Deeds"] = 150,
        ["Mesmerization"] = 24,
        ["Immolate"] = 48,
        ["Clarity II"] = 2100,
    });

    /// <summary>The trap. "Clarity II" is a spell, not a rank of "Clarity" - and note the
    /// fixture catalog has no "Clarity" entry at all, so a rank reading would resolve nothing
    /// while the correct reading answers exactly.</summary>
    [Fact]
    public void ASpellNamedWithANumeralResolvesExactlyAndIsNotScaled()
    {
        var resolved = Catalog.Resolve("Clarity II");

        Assert.Equal(new ResolvedDuration("Clarity II", 0, 2100), resolved);
    }

    [Fact]
    public void ARankedSpellDerivesFromItsBase()
    {
        Assert.Equal(new ResolvedDuration("Shiftless Deeds", 4, 210), Catalog.Resolve("Shiftless Deeds IV"));
        Assert.Equal(new ResolvedDuration("Shiftless Deeds", 6, 240), Catalog.Resolve("Shiftless Deeds VI"));
        Assert.Equal(new ResolvedDuration("Mesmerization", 5, 36), Catalog.Resolve("Mesmerization V"));
    }

    [Fact]
    public void AnUnrankedSpellResolvesToItsOwnDuration()
    {
        Assert.Equal(new ResolvedDuration("Immolate", 0, 48), Catalog.Resolve("Immolate"));
    }

    /// <summary>No base page means no number. Heroic Leap has no wiki duration entry, so the
    /// panel must say "--" rather than reach for something plausible.</summary>
    [Fact]
    public void AnUnknownSpellResolvesToNothing()
    {
        Assert.Null(Catalog.Resolve("Heroic Leap I"));
        Assert.Null(Catalog.Resolve("Some Spell That Does Not Exist"));
    }

    /// <summary>The tracking key: ticks and fade lines never carry the rank, so a ranked cast
    /// has to collapse onto the same key the tick lines will use.</summary>
    [Fact]
    public void TheBaseNameIsTheNameTickLinesWillUse()
    {
        Assert.Equal("Shiftless Deeds", Catalog.BaseNameOf("Shiftless Deeds IV"));
        Assert.Equal("Immolate", Catalog.BaseNameOf("Immolate"));
        // A spell genuinely named with a numeral keeps it - its tick lines carry it too.
        Assert.Equal("Clarity II", Catalog.BaseNameOf("Clarity II"));
        // Unknown to the catalog: fall back to the naive split rather than refuse to track.
        Assert.Equal("Heroic Leap", Catalog.BaseNameOf("Heroic Leap I"));
    }

    /// <summary>Guards the shipped data, not the code. These four are the values the whole
    /// feature was verified against.</summary>
    [Fact]
    public void TheEmbeddedCatalogCarriesTheVerifiedDurations()
    {
        var catalog = SpellDurationCatalog.Embedded;

        Assert.Equal(150, catalog.Resolve("Shiftless Deeds")!.Seconds);
        Assert.Equal(48, catalog.Resolve("Immolate")!.Seconds);
        Assert.Equal(24, catalog.Resolve("Mesmerization")!.Seconds);
        // Confirmed in game: Shiftless Deeds VI shows 4 minutes.
        Assert.Equal(240, catalog.Resolve("Shiftless Deeds VI")!.Seconds);
    }

    /// <summary>Cripple's wiki duration is the level-scaled range "6.3 minutes @L53 to 7.0
    /// minutes @L60". There is no single base to multiply, so it is absent by design.</summary>
    [Fact]
    public void ALevelScaledDurationIsAbsentRatherThanAveraged()
    {
        Assert.Null(SpellDurationCatalog.Embedded.Resolve("Cripple"));
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~SpellDurationCatalogTests"`

Expected: FAIL to compile — `The name 'SpellDurationCatalog' does not exist in the current context`.

- [ ] **Step 6: Write the implementation**

Create `src/EQBuddy.Core/SpellDurationCatalog.cs`:

```csharp
using System.Reflection;
using System.Text.Json;

namespace EQBuddy.Core;

/// <summary>How much to trust a countdown. The ordering is the feature: a measurement always
/// beats an estimate, and an estimate always beats a guess - of which there are none.</summary>
public enum DurationCertainty
{
    /// <summary>Nobody knows. The chip shows "--", never a number.</summary>
    Unknown,
    /// <summary>Wiki base duration, scaled for rank. Shown marked, and discarded the moment a
    /// real measurement lands.</summary>
    Derived,
    /// <summary>Measured from this log. Authoritative - never adjusted toward the wiki.</summary>
    Measured,
}

/// <summary>A cast name resolved against the catalog. <paramref name="Tier"/> is 0 when the
/// spell is unranked or is genuinely named with a numeral.</summary>
public sealed record ResolvedDuration(string BaseName, int Tier, double Seconds);

/// <summary>
/// Base spell durations from eqlwiki, embedded rather than fetched (Data/SpellDurations.json).
/// A game overlay should not make an HTTP request mid-fight for a number that is only a
/// fallback estimate, and the harvest already on disk answers 1,589 spells offline.
///
/// The resolution order exists because of a trap worth stating plainly: 121 spells in the wiki
/// catalog END in a roman numeral as their real name - "Clarity II", "Burnout IV",
/// "Cannibalize IV", "Berserker Madness III". They are distinct spell pages, not ranks. So the
/// catalog is asked for the full name FIRST, and only a miss is reinterpreted as a rank. Read
/// the other way round, "Clarity II" would scale a duration the catalog already knows exactly.
/// </summary>
public sealed class SpellDurationCatalog
{
    private readonly IReadOnlyDictionary<string, double> _durations;
    private static SpellDurationCatalog? _embedded;

    public SpellDurationCatalog(IReadOnlyDictionary<string, double>? durations = null) =>
        _durations = durations is null
            ? LoadEmbedded()
            : new Dictionary<string, double>(durations, StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped catalog, loaded once. 680 spells.</summary>
    public static SpellDurationCatalog Embedded => _embedded ??= new SpellDurationCatalog();

    /// <summary>Base seconds for a cast name, scaled for rank - or null when the catalog
    /// cannot answer, which the panel renders as "--".</summary>
    public ResolvedDuration? Resolve(string castName)
    {
        var name = castName.Trim();
        if (name.Length == 0) return null;

        // Exact first: the catalog is the authority on what is a NAME and what is a rank.
        if (_durations.TryGetValue(name, out var exact))
            return new ResolvedDuration(name, 0, exact);

        var (baseName, tier) = SpellRank.Split(name);
        if (tier > 0 && _durations.TryGetValue(baseName, out var seconds))
            return new ResolvedDuration(baseName, tier, SpellRank.Scale(seconds, tier));

        return null;
    }

    /// <summary>The name that this spell's tick and fade lines will use. Those lines never
    /// carry the rank - measured across the fixture, 0 of all tick lines have a numeral, and
    /// "Your Mesmerization spell has worn off" appears 1197 times against 630 casts of
    /// "Mesmerization V" - so a ranked cast has to collapse onto its base to be tracked at all.
    /// A spell genuinely NAMED with a numeral keeps it, because its own tick lines will too.</summary>
    public string BaseNameOf(string castName)
    {
        var name = castName.Trim();
        return _durations.ContainsKey(name) ? name : SpellRank.Split(name).Base;
    }

    private static Dictionary<string, double> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQBuddy.Core.Data.SpellDurations.json")
            ?? throw new InvalidOperationException("SpellDurations.json missing from resources");
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.GetProperty("durations").EnumerateObject())
            result[entry.Name] = entry.Value.GetDouble();
        return result;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`

Expected: PASS. Total 867 (844 + Task 2's 16 theory cases + 7 new), 0 failed.

- [ ] **Step 8: Commit**

```bash
git add scripts/harvests/eqlwiki/spells-promote.py src/EQBuddy.Core/Data/SpellDurations.json \
        src/EQBuddy.Core/SpellDurationCatalog.cs src/EQBuddy.Core/EQBuddy.Core.csproj \
        tests/EQBuddy.Tests/SpellDurationCatalogTests.cs
git commit -m "Base durations, shipped rather than fetched"
```

---

### Task 4: Wire the catalog into the tracker

Three changes that have to land together, because they share the same key: effects become keyed by base name while displaying the ranked name, samples become per-rank, and expiry consults the catalog when no sample exists.

**Files:**
- Modify: `src/EQBuddy.Core/DebuffTracker.cs` (`DebuffState` record, `_active` key, `_recastPending`, `OnLanding`, `OnTick`, `Record`, `Expiry`)
- Test: `tests/EQBuddy.Tests/DebuffTrackerTests.cs`

**Interfaces:**
- Consumes: `SpellDurationCatalog.Resolve`, `SpellDurationCatalog.BaseNameOf`, `DurationCertainty`, `ResolvedDuration` from Task 3
- Produces:
  - `DebuffState` gains `string BaseName` (after `Spell`) and `DurationCertainty Certainty` (last, defaults `Unknown`)
  - `public DebuffTracker(SpellDurationCatalog? catalog = null)`

- [ ] **Step 1: Write the failing tests**

Add to `tests/EQBuddy.Tests/DebuffTrackerTests.cs`:

```csharp
private static DebuffTracker WithCatalog() => new(new SpellDurationCatalog(
    new Dictionary<string, double> { ["Shiftless Deeds"] = 150, ["Immolate"] = 48 }));

/// <summary>Cold start: nothing has been measured, so the catalog answers - marked as an
/// estimate so the chip can say so.</summary>
[Fact]
public void AnUnmeasuredSpellFallsBackToTheDerivedDuration()
{
    var tracker = WithCatalog();

    tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds VI"));
    tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(6), "a sand giant", DebuffKind.Slow));

    var state = Assert.Single(tracker.Active(T0.AddSeconds(6)));
    Assert.Equal(DurationCertainty.Derived, state.Certainty);
    Assert.Equal(240, state.RemainingSeconds(T0.AddSeconds(6))!.Value, precision: 3);
}

/// <summary>The trust order. Tepid Deeds measures ~126s while its wiki page says 150 - and
/// that page contradicts itself. The measurement wins and is never corrected toward the wiki.</summary>
[Fact]
public void AMeasurementSupersedesTheDerivedDuration()
{
    var tracker = new DebuffTracker(new SpellDurationCatalog(
        new Dictionary<string, double> { ["Immolate"] = 48 }));

    // First cast: nothing measured yet, so the estimate is shown.
    tracker.Apply(new SpellCastEvent(T0, "Immolate"));
    for (var i = 0; i <= 126; i += 6)
        tracker.Apply(Tick("a sand giant", "Immolate", i));
    tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(126), "Immolate", "a sand giant"));

    // Second cast on a fresh mob: the measured 126 is used, not the catalog's 48.
    tracker.Apply(new SpellCastEvent(T0.AddSeconds(200), "Immolate"));
    tracker.Apply(Tick("a griffon", "Immolate", 206));

    var state = Assert.Single(tracker.Active(T0.AddSeconds(206)));
    Assert.Equal(DurationCertainty.Measured, state.Certainty);
    Assert.Equal(126, state.RemainingSeconds(T0.AddSeconds(206))!.Value, precision: 3);
}

/// <summary>Ranks have genuinely different durations, so their samples must not pool - a
/// rank-I measurement must never shorten a rank-V countdown.</summary>
[Fact]
public void SamplesForTwoRanksOfOneSpellDoNotPool()
{
    var tracker = WithCatalog();

    tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
    tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));
    tracker.Apply(new SpellWornOffEvent(T0.AddSeconds(101), "Shiftless Deeds", "a sand giant"));

    Assert.Equal(100, tracker.LearnedDurations["Shiftless Deeds IV"]);
    Assert.False(tracker.LearnedDurations.ContainsKey("Shiftless Deeds VI"));
}

/// <summary>The rank lives on the cast line and nowhere else, so a tick nobody cast has an
/// unknown tier. Assuming base rank would read 48s against a real 72s for a rank-V DoT and
/// warn early on every single cast.</summary>
[Fact]
public void ATickWithNoExplainingCastShowsNoDerivedDuration()
{
    var tracker = WithCatalog();

    tracker.Apply(Tick("a sand giant", "Immolate", 0));

    var state = Assert.Single(tracker.Active(T0));
    Assert.Equal(DurationCertainty.Unknown, state.Certainty);
    Assert.Null(state.RemainingSeconds(T0));
}

/// <summary>A cast from long ago must not supply a rank. _recentCasts is pruned only when a
/// new cast arrives, so without a window this quietly becomes "the last rank I ever saw" -
/// the guess this design rejected.</summary>
[Fact]
public void AStaleCastDoesNotSupplyTheRank()
{
    var tracker = WithCatalog();

    tracker.Apply(new SpellCastEvent(T0, "Immolate III"));
    // Two minutes later, a tick with no cast of its own to explain it.
    tracker.Apply(Tick("a sand giant", "Immolate", 120));

    var state = Assert.Single(tracker.Active(T0.AddSeconds(120)));
    Assert.Equal("Immolate", state.Spell);
    Assert.Equal(DurationCertainty.Unknown, state.Certainty);
}

/// <summary>The chip shows the rank you actually cast, while tracking keys on the base name
/// the tick and fade lines use.</summary>
[Fact]
public void TheChipShowsTheRankedNameButTracksByBaseName()
{
    var tracker = WithCatalog();

    tracker.Apply(new SpellCastEvent(T0, "Shiftless Deeds IV"));
    tracker.Apply(new DebuffLandedEvent(T0.AddSeconds(1), "a sand giant", DebuffKind.Slow));

    var state = Assert.Single(tracker.Active(T0.AddSeconds(1)));
    Assert.Equal("Shiftless Deeds IV", state.Spell);
    Assert.Equal("Shiftless Deeds", state.BaseName);
}

/// <summary>_recastPending held the RANKED cast name and was looked up with the UNRANKED tick
/// name, so recast detection could never fire for a ranked DoT - the exact failure the tracker
/// documents ("Immolate 115s against 54-60s for every sibling"). The fixture never caught it
/// because none of Daggo's DoTs are ranked.</summary>
[Fact]
public void ARecastOfARankedDotRestartsTheClock()
{
    var tracker = new DebuffTracker(new SpellDurationCatalog(
        new Dictionary<string, double> { ["Immolate"] = 48 }));

    tracker.Apply(new SpellCastEvent(T0, "Immolate III"));
    tracker.Apply(Tick("a sand giant", "Immolate", 6));
    tracker.Apply(Tick("a sand giant", "Immolate", 12));

    // Refresh before it drops. The clock must restart from the new cast's first tick.
    tracker.Apply(new SpellCastEvent(T0.AddSeconds(18), "Immolate III"));
    tracker.Apply(Tick("a sand giant", "Immolate", 24));

    var state = Assert.Single(tracker.Active(T0.AddSeconds(24)));
    Assert.Equal(T0.AddSeconds(24), state.LandedAt);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~DebuffTrackerTests"`

Expected: FAIL to compile — `'DebuffState' does not contain a definition for 'BaseName'` and `'Certainty'`.

- [ ] **Step 3: Extend the state record**

In `src/EQBuddy.Core/DebuffTracker.cs`, replace the `DebuffState` declaration's parameter list:

```csharp
public sealed record DebuffState(
    string Target,
    /// <summary>What to show on the chip - the ranked name when a cast supplied one.</summary>
    string Spell,
    /// <summary>What to key on. Tick and fade lines never carry the rank, so tracking keys on
    /// the base name while the chip displays the rank.</summary>
    string BaseName,
    string Caster,
    bool IsMine,
    DateTime LandedAt,
    DateTime LastTickAt,
    DateTime? ExpiresAt,
    /// <summary>True for DoTs, which announce themselves every six seconds. A slow announces
    /// itself once and then says nothing until it fades, so silence means nothing for it.</summary>
    bool Ticks = false,
    /// <summary>Whether the countdown was measured, derived from the wiki, or is unknown.</summary>
    DurationCertainty Certainty = DurationCertainty.Unknown)
```

- [ ] **Step 4: Rework the tracker's keying and lookup**

In `src/EQBuddy.Core/DebuffTracker.cs`:

Add the catalog field, the pairing window, and the constructor, next to the existing field
declarations:

```csharp
    /// <summary>How long after a cast a first TICK can still be attributed to it, and so
    /// supply the rank. Wider than <see cref="CastToLand"/> because the cast line precedes the
    /// landing by the spell's own cast time, which reaches 6s (Shiftless Deeds) before the
    /// first tick is even due. Measured across the fixture, cast-to-first-tick runs a median of
    /// 5s and a 90th percentile of 8s. Still far below the shortest DoT duration (30s), so this
    /// can never reach back and grab the PREVIOUS cast of the same spell.</summary>
    public static readonly TimeSpan CastToTick = TimeSpan.FromSeconds(15);

    private readonly SpellDurationCatalog _catalog;

    public DebuffTracker(SpellDurationCatalog? catalog = null) =>
        _catalog = catalog ?? SpellDurationCatalog.Embedded;
```

Replace `RememberCast`'s call site in `Apply` for `SpellCastEvent` so the pending key is the base name:

```csharp
            case SpellCastEvent cast:
                _recastPending.Add(_catalog.BaseNameOf(cast.Spell));
                RememberCast(cast.Time, "", cast.Spell, mine: true);
                break;
```

Replace `OnLanding`'s body after the cast lookup:

```csharp
        var key = (landed.Target, _catalog.BaseNameOf(cast.Spell));
        var (expires, certainty) = Expiry(cast.Spell, cast.Spell, landed.Time);
        _active[key] = new DebuffState(
            landed.Target, cast.Spell, key.Item2, cast.Caster, cast.Mine,
            LandedAt: landed.Time, LastTickAt: landed.Time,
            ExpiresAt: expires, Certainty: certainty);
```

In `OnTick`, replace the key and both construction sites. The key is already the tick's (base) name, so only the lookup of the ranked cast changes:

```csharp
    private void OnTick(DamageDealtEvent tick)
    {
        var key = (tick.Target, tick.Source);
        // The rank is on the cast line and nowhere else, so a tick nobody cast has an unknown
        // tier - and an unknown tier cannot be derived, only measured.
        //
        // The window matters. _recentCasts is pruned only when a new cast arrives, so an
        // unbounded search would match a cast from ten minutes ago and silently become "the
        // last rank I ever saw" - which is exactly the guess this design rejected.
        var castName = _recentCasts
            .LastOrDefault(c => c.Mine && tick.Time - c.Time <= CastToTick
                && _catalog.BaseNameOf(c.Spell) == tick.Source).Spell;
        ...
```

Then in the recast branch, replace the `with` expression:

```csharp
                var (refreshedAt, refreshedCertainty) =
                    Expiry(castName ?? tick.Source, castName, tick.Time);
                _active[key] = existing with
                {
                    Spell = castName ?? existing.Spell,
                    LandedAt = tick.Time,
                    LastTickAt = tick.Time,
                    ExpiresAt = refreshedAt,
                    Certainty = refreshedCertainty,
                    Ticks = true,
                };
                return;
```

And the new-effect branch at the end:

```csharp
        var (at, howSure) = Expiry(castName ?? tick.Source, castName, tick.Time);
        _active[key] = new DebuffState(
            tick.Target, castName ?? tick.Source, tick.Source, Caster: "", IsMine: true,
            LandedAt: tick.Time, LastTickAt: tick.Time,
            ExpiresAt: at, Ticks: true, Certainty: howSure);
```

Replace `Record(DebuffState)` and `Expiry` so samples key on the displayed (ranked) name:

```csharp
    private void Record(DebuffState state)
    {
        if (state.LastTickAt <= state.LandedAt) return;   // a single tick measures nothing
        Record(state.Spell, (state.LastTickAt - state.LandedAt).TotalSeconds);
    }

    /// <summary>Measured samples first, catalog second, nothing third - the trust order the
    /// whole panel rests on. Samples key on the RANKED name because ranks genuinely differ:
    /// pooling Immolate I with Immolate V would corrupt both. A measurement is never adjusted
    /// toward the catalog; Tepid Deeds keeps its measured 126s against a wiki 150.
    ///
    /// <paramref name="castName"/> is null when no cast explained this effect, and then NOTHING
    /// is derived. The rank lives on the cast line alone, so deriving from the base name would
    /// silently assume tier 0 - reading 48s for a rank-V Immolate that runs 72s, and warning
    /// early on every cast. An unknown rank is an unknown duration.</summary>
    private (DateTime? At, DurationCertainty Certainty) Expiry(
        string sampleKey, string? castName, DateTime from)
    {
        if (_samples.TryGetValue(sampleKey, out var samples) && samples.Count > 0)
            return (from.AddSeconds(Consensus(samples)), DurationCertainty.Measured);
        if (castName is not null && _catalog.Resolve(castName) is { } derived)
            return (from.AddSeconds(derived.Seconds), DurationCertainty.Derived);
        return (null, DurationCertainty.Unknown);
    }
```

Finally, in `OnFade`, the lookup key becomes the base name — fade lines never carry the rank:

```csharp
        var key = (fade.Target, _catalog.BaseNameOf(fade.Spell));
        if (!_active.Remove(key, out var state)) return;
        if (_died.Contains(fade.Target)) return;

        var measured = (fade.Time - state.LandedAt).TotalSeconds;
        if (measured > 0) Record(state.Spell, measured);   // ranked name: samples are per-rank
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`

Expected: PASS. Total 874 (867 + 7 new), 0 failed.

Existing `DebuffState` constructions in tests will need the new `BaseName` argument. Where a test constructs one positionally, pass the same value as `Spell`. **Do not** change any existing assertion's expected value to make it pass — if a pre-existing behavioural test fails, stop and report it.

- [ ] **Step 6: Commit**

```bash
git add src/EQBuddy.Core/DebuffTracker.cs tests/EQBuddy.Tests/DebuffTrackerTests.cs
git commit -m "Derived durations, and the rank that only the cast line knows"
```

---

### Task 5: Mark the estimate on the chip

**Files:**
- Modify: `src/EQBuddy.UI.Shared/DebuffChipPresentation.cs`
- Test: `tests/EQBuddy.Tests/DebuffChipPresentationTests.cs`

**Interfaces:**
- Consumes: `DebuffState.Certainty`, `DurationCertainty` from Tasks 3–4
- Produces: `DebuffChipPresentation.EstimatePrefix` (`"~"`)

- [ ] **Step 1: Write the failing test**

Add to `tests/EQBuddy.Tests/DebuffChipPresentationTests.cs`. Note the local `State` helper needs the `BaseName` argument added from Task 4 — update it as shown:

```csharp
private static DebuffState State(string target, string spell, double? remaining,
    DurationCertainty certainty = DurationCertainty.Measured) =>
    new(target, spell, BaseName: spell, Caster: "", IsMine: true, LandedAt: T0, LastTickAt: T0,
        ExpiresAt: remaining is { } r ? T0.AddSeconds(r) : null, Certainty: certainty);

/// <summary>A derived countdown says it is one. Without the mark, a wiki estimate and a
/// measurement read identically, and the panel exists to support "do I recast now".</summary>
[Fact]
public void ADerivedCountdownIsMarkedAsAnEstimate()
{
    var chips = DebuffChipPresentation.Chips(
        [State("a sand giant", "Shiftless Deeds VI", 240, DurationCertainty.Derived)], T0, 10);

    Assert.Equal("~4:00", Assert.Single(chips).CountdownText);
}

[Fact]
public void AMeasuredCountdownIsNotMarked()
{
    var chips = DebuffChipPresentation.Chips(
        [State("a sand giant", "Immolate", 48, DurationCertainty.Measured)], T0, 10);

    Assert.Equal("0:48", Assert.Single(chips).CountdownText);
}

/// <summary>An estimate about to drop is still worth warning about - it is the best
/// information available, and suppressing the warning would make the estimate pointless.</summary>
[Fact]
public void ADerivedCountdownStillWarnsWhenItIsAboutToDrop()
{
    var chips = DebuffChipPresentation.Chips(
        [State("a sand giant", "Shiftless Deeds VI", 8, DurationCertainty.Derived)], T0, 10);

    Assert.True(Assert.Single(chips).IsDue);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release --filter "FullyQualifiedName~DebuffChipPresentationTests"`

Expected: FAIL — `ADerivedCountdownIsMarkedAsAnEstimate` reports `Expected: "~4:00", Actual: "4:00"`.

- [ ] **Step 3: Write the implementation**

In `src/EQBuddy.UI.Shared/DebuffChipPresentation.cs`, add the constant beside `UnknownCountdown`:

```csharp
    /// <summary>Prefixes a countdown derived from the wiki's base duration rather than measured
    /// from this log. One character, because the chip is narrow and the slider makes it
    /// narrower - but the distinction has to survive a glance mid-fight, so it is in the text
    /// rather than in an opacity a screenshot would lose.</summary>
    public const string EstimatePrefix = "~";
```

Change the `CountdownText` argument in the `Select` to pass certainty:

```csharp
                CountdownText: Countdown(s.RemainingSeconds(now), s.Certainty),
```

And replace `Countdown`:

```csharp
    private static string Countdown(double? remaining, DurationCertainty certainty)
    {
        if (remaining is not { } seconds) return UnknownCountdown;
        var whole = (int)Math.Round(seconds);
        var prefix = certainty == DurationCertainty.Derived ? EstimatePrefix : "";
        return $"{prefix}{whole / 60}:{whole % 60:00}";
    }
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release`

Expected: PASS. Total 877 (874 + 3 new), 0 failed.

- [ ] **Step 5: Run the Avalonia suite**

First check nothing is running: `ps -C EQBuddy.Avalonia`. If a process is listed, stop — the running app holds the X11 hotkey grabs.

Run: `dotnet test tests/EQBuddy.Avalonia.Tests/EQBuddy.Avalonia.Tests.csproj -c Release`

Expected: PASS, 0 failed. Read the count, not the exit code.

- [ ] **Step 6: Commit**

```bash
git add src/EQBuddy.UI.Shared/DebuffChipPresentation.cs tests/EQBuddy.Tests/DebuffChipPresentationTests.cs
git commit -m "An estimate says that it is one"
```

---

### Task 6: Verify in the real app

Headless Avalonia tests cannot confirm this panel — replaying the real log has caught two bugs the suite could not see. This task has no unit test; its deliverable is evidence.

**Files:** none modified unless a defect is found.

**Interfaces:**
- Consumes: everything above.
- Produces: a report of what the panel showed, for the user.

- [ ] **Step 1: Confirm settings are untouched by the test run**

```bash
stat -c '%y %n' ~/.config/EQBuddy/settings.json
```

The mtime must not have moved during the test runs above. `AppSettings.Load()` is a pure read and must stay one; if the mtime changed, **stop and report** — a test run has written to the live profile.

- [ ] **Step 2: Publish**

```bash
dotnet publish src/EQBuddy.Avalonia/EQBuddy.Avalonia.csproj -c Release -r linux-x64 \
  --self-contained -p:PublishSingleFile=true -o /tmp/claude-1000/eqbuddy-derived
```

Expected: build succeeds, no warnings about the missing embedded resource.

- [ ] **Step 3: Replay the real log against an isolated profile**

Do **not** point the app at `~/.config/EQBuddy`. Launch with a temp profile so the user's live settings cannot be touched:

```bash
mkdir -p /tmp/claude-1000/eqbuddy-profile
EQBUDDY_APPDATA=/tmp/claude-1000/eqbuddy-profile \
  setsid nohup /tmp/claude-1000/eqbuddy-derived/EQBuddy.Avalonia \
  > /tmp/claude-1000/eqbuddy-run.log 2>&1 &
```

Point it at `tests/fixtures/eqlog_Daggo_freeport.txt` and open the DoT chip panel.

- [ ] **Step 4: Check the panel and the error log**

Screenshot the window (find its id via `xprop -root _NET_CLIENT_LIST`, match `_NET_WM_PID` to the pid you launched — **not** the window title, since the user's own app titles its window `EQBuddy` too):

```bash
import -window <id> /tmp/claude-1000/eqbuddy-panel.png
cat /tmp/claude-1000/eqbuddy-profile/error.log
```

Confirm: a `Shiftless Deeds IV` chip reads `~3:30` before any measurement lands; an `Immolate` chip reads `0:48` rather than `0:54` once measured; no chip shows a number where the spell is absent from the catalog. `error.log` must be empty or unchanged.

- [ ] **Step 5: Close it gracefully and report**

Close via a `WM_DELETE_WINDOW` ClientMessage scoped to the pid you launched (not `kill`, which skips `OnClosing`; not a broadcast, which would close the user's real app). Then report the screenshot and the three observations to the user.

Do **not** install over `~/.local/share/EQBuddy-app/` — that is the user's call, and it wants the `.prev-<date>` rollback copy made first.

---

## Self-Review

**Spec coverage:** Part 1 anchoring → Task 1. Part 2 catalog → Task 3 (steps 1–3). Part 3 rank resolution → Tasks 2 and 3. Part 4 model + latent recast bug → Task 4. Part 5 presentation → Task 5. Testing section → tests in Tasks 1–5 plus real-app verification in Task 6. Out-of-scope items are not implemented anywhere.

**Placeholders:** none — every code step carries the actual code.

**Type consistency:** `DurationCertainty` and `ResolvedDuration` are defined in Task 3 and consumed with the same member names in Tasks 4 and 5. `SpellRank.Split`/`Scale` defined in Task 2, consumed in Task 3. `DebuffState.BaseName` and `.Certainty` added in Task 4, consumed in Task 5's test helper. `DebuffTracker.ServerTickSeconds` is deleted in Task 1 and referenced by no later task.

**Known ripple:** Task 4's `DebuffState` gains a positional parameter, so existing positional constructions in `DebuffTrackerTests.cs` and `DebuffChipPresentationTests.cs` need `BaseName` supplied. Task 4 step 5 and Task 5 step 1 both call this out.
