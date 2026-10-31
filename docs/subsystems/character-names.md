# Character names — the rules a chosen name has to pass

The create editor asks the server whether a name is acceptable and gets one of three retail callbacks back:
`NicknameOk`, `NicknameTaken` or `NicknameInvalid` (`CreateFlowService`, `SendNicknameAccepted` /
`SendNicknameRejected`). This page is what drives the third one.

## What retail states

Two of the three rules come from the client's own text, and one does not.

| rule | evidence |
|---|---|
| 4–16 characters | *"The length of your character name is invalid. The length of the name must be between 4 and 16 characters."* — LDB 211711[2780]. The client enforces this locally, so a stock client never sends a name outside it. |
| already in use | *"The name %s is already taken, please choose another."* — LDB 211711[2385]. This is `NicknameTaken`. |
| forbidden content | *"The name %s is not valid, please choose another."* — LDB 211711[2154]. The message exists; the rule behind it does not appear anywhere. |

## The forbidden-word list

The client ships one: **RDB 1065100**, a single 19.4 KB record.

```
[u32 typeId = 1065100][u32 0][u32 1][u32 count]   then count x   [u32 byteLen][UTF-8 bytes]
```

1,819 entries, 926 distinct — most terms are listed twice, non-adjacently, which is the shape of a hash-table
dump rather than of two columns. It is genuinely multilingual: German, English, Spanish, French and Polish
alongside a large Korean block. `RdbNameFilterList` reads it; the chain tiles the record exactly, so a short read
is an error rather than something to resync past.

Two neighbouring types were checked and are not part of this. **1065102** is 1,142 sorted u32s that resolve
against the LDB as *spell* ids (category 50210), not names. **1065101** is 49 fixed 16-byte records keyed by a
single byte 0x41..0xFE — a per-character table of some kind, unrelated to the word list and still unread.

## Why the terms are stored hashed

`NameFilterTerm` holds `SHA-256(lowercased term)` and the term's length, never the term. This repo is meant to go
public and `faolan.sql` carries every game-data row as reviewable text — a plaintext column
would fill it with slurs. Hashing costs the check nothing, because the match runs the other way round from the
obvious one: instead of scanning terms for a substring of the name, `CharacterNameRules` hashes each of the
**name's** own substrings and asks the set. A 16-character name has 136 of them, and the stored `Length` narrows
that to the lengths that actually occur.

Import it with `dotnet run --project Faolan.Importer -- --only rdb-name-filter` (RDB-sourced, so opt-in and
needing the client install). A server whose database has no rows enforces the length rule alone and says so in
the log — it does not pass silently.

## The matching rule is ours, calibrated against the client's own words

No capture rejects a name, so retail's matching rule is unobservable. Ours: a term of **6 characters or more**
matches anywhere in the name; a shorter one has to be the whole name.

The number is not a guess. The client supplies an oracle — the words in its own NPC and item names are by
construction acceptable English — so the floor is set where it stops refusing them:

| substring floor | terms matched anywhere | client words refused |
|---:|---:|---:|
| 3 | 388 | 36 — `assassin`, `ambassador`, `occultist`, `acumen`, … |
| 4 | 218 | 16 |
| 5 | 170 | 5 |
| **6** | **128** | **3** — `bastardsword`, `bite`, `hack` |
| 7 | 94 | 3 |
| 8 | 66 | 2 |

It plateaus at 6, which is why 6 and not a rounder number. Below the floor the whole-name rule is weak — a padded
three-letter word gets through — but a filter that refuses `Assassin` is worse than one that misses it, and the
client's own content is the evidence for which.

Re-derive the table at any time with `dotnet run --project Faolan.Importer -- --report name-filter-check`, which
also fails if `faolan.db` holds a term the client no longer carries.
