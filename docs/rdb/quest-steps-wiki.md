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

552 quest pages, of which **481 carry an ordered step list — 1,145 steps in total**. Discovery was closed by four
independent passes that converged (a recursive walk of the quest categories, an `allpages` prefix sweep, the
`Infobox Quest` transclusion list, and the links off the twelve area index pages); the last three found nothing
the first had missed.

| `parse_status` | pages | meaning |
|---|---:|---|
| `steps_found` | 481 | an ordered step list was parsed |
| `prose_only` | 31 | free prose, no structured objectives (mostly crafting/recipe grants) |
| `objectives_only` | 25 | the infobox objective list and nothing else |
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

A wiki **page** is one of our **`QuestLines`**; a wiki **step** is one of our **`QuestDefinitions`**. Matching
page titles against `QuestDefinitions.Name` therefore looks like near-total disjointness — only 25 of 552 — and
that reading is wrong. Against `QuestLines.Name` (with the leading `[Zone]` / `[NPE]` / `[Destiny …]` tag
stripped):

| | pages |
|---|---:|
| matched one of our 2,130 named quest lines | **442** of 552 |
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

⚠️ Two fields remain close to useless as shipped: `prerequisite` is filled on exactly **one** page of 552, so
chains are only walkable *forwards* via `next_quest` (149 pages); and `level` is present on 170 of 552, `zone`
null on 111.

⚠️ Coverage is still small in absolute terms — 552 pages against our 5,049 client-derived quests, and the wiki's
own zone indexes link far more quests than have pages. A quest absent from the wiki means nothing about our data.

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
