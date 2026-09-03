# Quest step structure, read off the community wiki

Our quest content comes from the client: `QuestDefinitions` (5,049 rows) and `QuestGoalDefinitions` (7,318),
extracted by the importer. What the client's records do **not** hand over in a human-readable form is the
*narrative decomposition* a player sees — a quest broken into ordered numbered steps, each with its own
objectives and rewards. The Age of Conan Fandom wiki does carry that, written up by players, so it was scraped
into [`quest-steps-wiki.json`](quest-steps-wiki.json) beside this page.

⛔ **It is a reference, not a source.** Nothing here is imported into `faolan.db` and nothing here belongs on the
wire. Retail leads: the client's own records are what the server is built from. This is player-written prose
about the same game, useful for reading a chain's shape and for sanity-checking goal ordering, and wrong in the
ways any wiki is wrong.

## What is in it

**566 quest pages, of which 491 carry an ordered step list — 1,160 steps in total.** Each record carries a
`discovery` field saying how it was found, because the two routes have different coverage guarantees:

* **`convention` (552)** — the pages that follow the wiki's own quest conventions. Two independent enumerations
  agree exactly on this set: 519 mainspace pages titled `Quest:*`, 494 pages in the 21-category quest tree,
  union 552.
* **`sweep` (14)** — quest pages that follow *none* of those conventions: plainly titled, in no quest category,
  no `{{Infobox Quest}}`. They were found by reading **all 5,793 mainspace pages** and classifying on structure
  alone (an `Objectives` / `Journal Entry` / roman-or-chapter step heading / `Reward` section), ignoring title,
  category and infobox entirely.

⚠️ **The first pass claimed its discovery was "closed by four independent passes that converged". That was
wrong, and the convergence was the tell rather than the proof** — all four keyed on the same three conventions,
so a page following none of them was invisible to all of them and their agreement said nothing about it. The
sweep is what actually bounds the gap, and it bounds it at 14.

The sweep's own recall was calibrated rather than assumed: run against the known 552, the structural markers
light up 487 and miss 43 — and those 43 are exactly the `prose_only` / crafting-stub records that carry no
structure by construction. Four further recall passes (weak tells such as `XP:` and objective counters, a scored
heuristic, the index-page and `{{quest}}` link targets, and a census of all 96 `*Quest*` categories) found
nothing beyond the 14.

⛔ The limit that remains, stated plainly: a quest page that states **no** objective, reward or step anywhere is
still invisible, because there is nothing structural left to match on.

⚠️ Separately, the wiki's own indexes list far more quests than it documents: the six zone index pages link
**754** distinct quest titles of which only **196 exist** — 558 are red links.

| `parse_status` | pages | meaning |
|---|---:|---|
| `steps_found` | 491 | an ordered step list was parsed |
| `prose_only` | 31 | free prose, no structured objectives (mostly crafting/recipe grants) |
| `objectives_only` | 29 | the objective list and nothing else |
| `index_page` | 8 | a hub/listing page, not a quest |
| `variant_hub` | 5 | points only at per-class or per-race variants |
| `no_steps_section` / `empty` | 1 / 1 | |

Each record carries the page identity, zone/subzone, level, quest giver and end NPC, chain links, rewards, and
the step array; each step is `{index, roman, title, kind, objectives[], reward, notes[]}`. Absent data is `null` —
nothing is inferred.

**Structure only.** The per-step journal narrative and the page overview prose were deliberately dropped. That
text is Funcom's, and the emulator already sources journal text from the client's own records — carrying a second
transcription would duplicate it and set up a rival source of truth for the same strings. What is kept is the
part the wiki uniquely adds.

## ⭐ The join is at the QUEST LINE, not the quest

A wiki **page** is one of our **`QuestLine`** rows; a wiki **step** is one of our **`QuestDefinition`** rows —
the three-level model is set out in
[`../subsystems/quests.md`](../subsystems/quests.md). Matching page titles against `QuestDefinition.Name`
therefore compares a chain name against a step name, which looks like near-total disjointness — only 25 of the 552 —
and that reading is wrong. Against `QuestLines.Name` (with the leading `[Zone]` / `[NPE]` / `[Destiny …]` tag
stripped):

| | pages |
|---|---:|
| matched one of our 2,130 named quest lines | **451** of 566 |
| …and the step titles equal our member quest names, **in order** | **286** |
| …matched, but the step list differs | 141 |
| unmatched | 110 |

The 110 unmatched are mostly not quests at all — tradeskill recipe sets, arenas, and index pages, which is what
the `parse_status` column already separates out.

**The starting content matches exactly.** Both chains a new character sees agree step for step, and the objective
text agrees too — our goal on the first Casilda step reads `Kill the Scavenger`, and so does the wiki's:

| our quest line | ours | wiki | agreement |
|---|---:|---:|---|
| `[Destiny Quest 1-5] Shipwrecked` (auto-granted) | 8 quests | 8 steps | 8 / 8 identical, in order |
| `[Destiny 1-5 Sidequest] Casilda` (the first quest picked up) | 4 quests | 4 steps | 4 / 4 identical, in order |

## Where the two disagree, and which side is usually right

Of the 52 differences across the `[NPE]` and `[Destiny …]` lines:

* **18 — the wiki stops short of the turn-in.** Its step list is a strict prefix of ours, missing the closing
  *Return to X* step (`Dead Meat` → the wiki has `Salted Meat`, we also have `Return to Ninus`). **Ours is the
  more complete side**, and this is the single most common difference.
* **8 — the wiki page is a stub** with no steps parsed at all.
* **10 — spelling**, almost always the wiki's (`The West Guard Wall` for our `The West Wall Guard`,
  `Crameleico`, `Truth Revealad`, a stray trailing `=`). Two are ours to check: `Judgment`/`Judgement` and
  `Report back to Bartholomo`.
* **4 — the same steps in a different order.** These are the ones worth an actual look, because journal order is
  something we serve: `[NPE] Tarisha's Sacrifice` has *The Bodyguard* first on the wiki and eighth for us, and
  `[Destiny 30 Cimmeria] The Phoenix of the South` has the two halves of its five steps swapped.
* the remaining 12 are a **matching artifact, not a data difference** — the wiki carries one page per class
  variant, all titled e.g. *The Awakening I*, so a Rogue page gets compared against the Mage line. Any real check
  of the class Destiny chains has to disambiguate on class first.

⚠️ Two fields remain close to useless as shipped: `prerequisite` is filled on exactly **one** page of the 552, so
chains are only walkable *forwards* via `next_quest` (149 pages); and `level` is present on 170 of the 552, `zone`
null on 111.

⚠️ Coverage is still small in absolute terms — 566 pages against our 5,049 client-derived quests, and the wiki's
own zone indexes link far more quests than have pages. A quest absent from the wiki means nothing about our data.

## Verifying every quest line against it

All 2,210 of our quest lines were run through this scrape; the per-line result is committed as
[`quest-line-verification.tsv`](quest-line-verification.tsv) (one row per line: member count, ordering state,
wiki verdict, flags). ⚠️ Its `ordering` column is a snapshot of `JournalSequence` as of 2026-09-02, BEFORE the
index-block harvest ordered 47 more lines (#926, 2026-09-03) - the wiki-verdict columns do not depend on it and
are current; the ordering state of those 47 lines is in `faolan.db`, not here. The file was produced by a one-off
script that is not in the repo, which is why it is annotated rather than regenerated.

| wiki verdict | lines | |
|---|---:|---|
| `no-wiki-page` | 1,794 | the wiki does not document it — says nothing either way |
| `exact` | 281 | step titles identical to our member quests, in order |
| `mismatch` | 70 | see below — all wiki-side |
| `wiki-omits-turn-in` | 22 | its list is a prefix of ours, missing the closing *Return to X* |
| `minor-text-diff` | 16 | a word or spelling apart |
| `wiki-page-has-no-steps` | 16 | stub page |
| `same-steps-order-differs` | 11 | same set, different order |

⭐ **The wiki contradicts our data in zero cases.** Every apparent disagreement resolves into one of two things,
and neither is a defect on our side:

* the 70 `mismatch` rows are wiki-side noise — step headings that did not parse and came out blank, wiki typos
  (`Disembowling` for our `Disembowelling`, `Bartholmo` for `Bartholomo`), a `Completion` pseudo-step the wiki
  appends, and **class-name collisions**: the wiki has one page per class all titled e.g. *The Awakening I*, so a
  Priest page can be scored against the Warrior line. Disambiguate on class before reading anything into those.
* the 11 `same-steps-order-differs` rows are **our missing ordering**, not a wrong order — every one of them is a
  line whose `JournalSequence` is 0 on all or most members, so any order we display is an artifact of the
  fallback sort, not a claim.

## ⚠️ Journal ordering is absent on 1,009 lines, and the wiki can say what it should be

| `JournalSequence` state | lines |
|---|---:|
| `full` — a complete 1..N | 889 |
| `none` — every member at 0 | 1,009 |
| `single` — a one-quest line, order moot | 288 |
| `partial` — some numbered, some 0 | 24 |

⛔ This is a known, deliberately handled condition, not a bug: `QuestMarkerResolver.LineReached` skips the
line-reached gate whenever either side's sequence is 0, because a line that cannot say what comes first must not
gate — otherwise every giver on it would go silver forever.

⭐ **What the wiki adds is evidence about the `partial` case.** On lines where exactly one member carries
`JournalSequence = 1` and the rest are 0, the wiki's first step is that member — **5 of 5, none against**. So the
1 is a real head marker and the 0s are simply unwritten, rather than 0 being a position that sorts before 1.
Anything ordering members must therefore put unknown **last**, not first; sorting `0` ahead of `1` silently
reverses those lines.

### Could the wiki simply FILL the missing order? Measured: accurate, but it reaches only 4% of the gap

Held out against the lines we *can* check — those with a full captured `JournalSequence` where the wiki lists
exactly the same step set — the wiki's order is **right on 207 of 207**, including three 6-step lines, three
7-step, three 8-step and one 11-step, where agreeing by chance is 1 in 39 million. Its ordering is not the weak
half of this source.

| our line length | wiki order == captured order |
|---|---|
| 2 steps | 155 / 155 |
| 3–5 steps | 42 / 42 |
| 6–11 steps | 10 / 10 |

⚠️ **But it can only fill 41 of the 1,009 unordered lines** — the wiki largely documents the same lines our
captures already cover. So it is not a fix for the gap; it is a small, high-confidence increment (though a
useful one: it includes several 6–11 step Destiny class chains such as the Warrior and Priest *Awakening* lines
and `[NPE] Tarisha's Sacrifice`).

⛔ If those 41 are ever written into the database they must carry their **provenance**, not be merged into
`JournalSequence` as though captured — the column reads as client fact everywhere else, and 41 rows of inferred
order silently mixed in would make every consumer's trust in it wrong. Open work tracked in Gitea #926.

## Reproducing

Read through the MediaWiki API, not a browser fetch — Fandom answers `WebFetch` with HTTP 402:

```zsh
curl -sL -H "User-Agent: Mozilla/5.0" \
  "https://aoc.fandom.com/api.php?action=parse&page=Quests&prop=wikitext&format=json&formatversion=2" \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['parse']['wikitext'])"
```

Pages are laid out as an `{{Infobox Quest}}` plus per-step `== I. Title ==` sections carrying `Objectives` /
`Journal Entry` / `Reward` subsections, with three variants to handle: prose-titled step headings where the
`Objectives` subsection is the only marker, `Chapter I:` / `Part 1` numbering, and a flat
`==Objectives==` / `==Rewards==` layout. Link and template markup is resolved rather than stripped
(`[[A|B]]` → `B`, `{{cost|c=21|t=92}}` → a structured amount).

Source: the Age of Conan Fandom wiki (<https://aoc.fandom.com/wiki/Quests>), read 2026-09-02, CC-BY-SA. The
`revision_timestamp` on each record is the page revision it was read from.
