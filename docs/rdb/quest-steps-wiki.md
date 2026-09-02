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

## ⚠️ Coverage: it is small, and it barely overlaps what we have

Two numbers decide how far to trust this as a cross-check, and both are discouraging:

* **It is ~11% the size of our client-derived set** — 552 pages against 5,049 quests. The wiki is markedly
  incomplete against the real game: its own Aquilonia index links far more quests than have pages, and Khitai has
  no quest category at all.
* **Only 25 of the 552 name-match a quest in `QuestDefinitions`**, against 4,302 of our quests carrying a name.
  Titles like *A Beloved Friend* or *A Missing Filly* appear nowhere in our data under any name.

That near-disjointness is itself the interesting result, and it is **not** yet explained. It could mean the wiki
covers content our extraction has not reached, or that the two sides name the same quests differently, or that
our `Name` column is populated for a different subset than the wiki documents. Until that is run down, treat a
non-match as "unknown", never as "missing from the client".

Two fields are close to useless as shipped: `prerequisite` is filled on exactly **one** of 552 pages (the wiki
almost never fills `|prereqs=`), so chains are only walkable *forwards* via `next_quest` (149 pages); and `level`
is present on 170 of 552, `zone` null on 111.

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
