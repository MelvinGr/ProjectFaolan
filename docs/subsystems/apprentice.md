# Apprenticeship / mentoring (#144)

A higher-level **teacher** takes a lower-level team-mate as an **apprentice**; the apprentice then fights at the
teacher's level for as long as the bond lasts. Age of Conan's sidekick system.

**No capture performs an apprenticeship.** Like quest sharing, the grammar is the client's own method table and
the rules are the client's own strings — neither is inferred from traffic. What that buys and what it does not is
the same as everywhere else: the BODIES are exact (a signature names its arguments), the ENDPOINT pair is taken by
analogy from the neighbouring dispatches that are observed, and only an in-client check settles that. `[V0]`

## Messages

Client → server, `GameCharAgentProject`:

| method | id | body |
|---|---|---|
| `AcceptApprenticeTeacher(const ID32& i_cTeacherID)` | `0x02` | one ID32 |
| `DeclineApprenticeTeacher(const ID32& i_cTeacherID)` | `0x03` | one ID32 |
| `RequestApprenticeStudent(const ID32& i_cStudentID)` | `0x04` | one ID32 |
| `LeaveApprenticeMode()` | `0x05` | empty |

Server → client:

| method | interface | id | body | codec |
|---|---|---|---|---|
| `DispatchRequestApprentice(i_cTeacher)` | `GameCharInterfaceProject` | `0x03` | one ID32 | `RequestApprenticeCodec` |
| `DispatchAddApprenticeEntry(i_cTeacher, i_cStudent)` | `GameCharInterfaceProject` | `0x04` | two ID32 | `ApprenticeEntryCodec` |
| `DispatchRemoveApprenticeEntry(i_cTeacher, i_cStudent)` | `GameCharInterfaceProject` | `0x05` | two ID32 | `ApprenticeEntryCodec` |
| `DispatchApprenticeModeOnOff(i_bOnOff)` | `GameCharInterface` | `0x81` | one byte | `ApprenticeModeCodec` |

An ID32 is `[u32 class][u32 instance]` (`EntityIdCodec`); a character's class word is `0xC350`. The roster pair is
ordered **teacher then student**, as the signature names them.

⚠️ The mode flag is on `GameCharInterface`, the two roster methods on `GameCharInterfaceProject` — different
interfaces, so different endpoint pairs, which is exactly what a `0x2NNN` wire rendering cannot tell you.

## The rules are the client's, quoted

Every rule `ApprenticeService` enforces is a client string in LDB category 211711 (the English UI page), not a
guess:

| string | rule |
|---|---|
| `[993]` "The teacher and apprentice need to be in the same group." | both in one team |
| `[2748]` "The teacher needs to be at least 2 levels higher than the apprentice." | gap ≥ 2 |
| `[2828]` "The teacher cannot be more than 20 levels higher than the apprentice." | gap ≤ 20 |
| `[1147]` "The apprentice already has a teacher." | a student has at most one teacher |
| `[1031]` / `[1296]` "%s declined/accepted apprentice request" | the teacher is told either way |
| `[1879]` "Your apprentice level increased to %d." | the effect is a level, pushed as a stat |

Both bounds are inclusive, so a teacher exactly 2 levels up and exactly 20 levels up are the legal extremes. The
client applies these before it sends, so a refusal from the server is the belt-and-braces half — still answered
rather than dropped, because a client that got past its own check is telling us something.

A teacher may hold several apprentices (which is why the roster message is per ENTRY rather than per character);
a student may not hold several teachers.

## The effect

Accepting pushes stat `ApprenticeLevel` (`0x3C5`) on the STUDENT, set to the teacher's level. That is the term the
client's armour mitigation divides the real level by:

```
ApprenticeAdjustedArmor = (2.0 - level(0x36) / ApprenticeLevel(0x3c5)) * TotalArmorValue
```

so equal values are the neutral `1.0` we send everywhere else, and a raised `ApprenticeLevel` is what makes an
apprentice survivable beside their teacher. Ending the bond RESTORES the student's own level rather than clearing
the stat — `ApprenticeLevel` 0 would divide by zero in that formula.

⚠️ **That the boost is the teacher's level exactly is our call, not retail's.** The client's string says the
apprentice level "increased to %d" without saying to what, and no capture shows one. Teacher's level is the
reading that makes the sidekick work; a capture would settle whether retail uses `teacher - 1` or ramps it.

Nothing is persisted. A bond cannot outlive either connection — `ApprenticeService.Forget` ends every bond a
disconnecting character is in, on either side, which is what stops the other party's client keeping a pushed
level boost with nobody on the other end of it.

## Code map

- `Faolan.Core.Network/Codecs/Apprentice/` — the four message bodies.
- `Faolan/GameServer/Apprentice/ApprenticeService.cs` — the rules and the live pairing state.
- `Faolan/GameServer/Apprentice/IApprenticeOutbound.cs` — the narrow port the wire side implements.
- `Faolan/GameServer/Apprentice/ApprenticeOutbound.cs` — that wire side. It was on `GameServerListener` until
  #826 moved it off; every op is addressed by CHARACTER ID (mentoring spans two connections), which is why it
  needed `ConnectionRegistry` before it could move.
- `Faolan/GameServer/GameServerListener.Inbound.cs` — the four c2s cases.
- `Faolan.Tests/ApprenticeTests.cs` — the bodies against the signatures, the rules against the strings.
