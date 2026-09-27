# `remoteformat` grammar — from the client, not the corpus

Recovered from `AgeOfConan.exe` (Ghidra headless). Reproduce with
`NEEDLE=remoteformat <ghidra>/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe -noanalysis
-scriptPath tools/ghidra -postScript FindStr.java`, then `FNS=<addr> … -postScript DecompileFns.java`.

Captured bytes only show the shapes the corpus happens to contain. These are the client's own format strings and
its own tag table, so they say what the protocol allows rather than what has been seen.

## The element writers

`FUN_0061faf0` builds a `<localized>` element with one of exactly two `sprintf` templates, chosen on whether the
element carries a numeric id or a name token:

```
<localized id="%d" category="%d" flags="%s" key="%s" >
<localized token="%s" category="%d" flags="%s" key="%s" >
```

`FUN_0061fd80` is the `<stringentry>` writer.

These confirm, from the binary, three things our implementation depends on:

- **attribute order** is fixed — `id`/`token`, `category`, `flags`, `key`
- **the trailing space before `>`** is part of the format string, not an artefact of one capture
- **`id` and `token` are alternatives**, never both — which is why `InnerElement` keys on one or the other and
  `Localized` rows carry exactly one of them (781 by id, 49 by token in the shipped data)

## The tag table

`FUN_006220e0` is the parser. It dispatches with `_strnicmp` against, in order:

```
localized  stringentry  remoteformat  /remoteformat  parameter
```

That is the complete set of tags it recognises — matching the three element kinds `InnerElement` models plus the
`<parameter>` child and the root's closing tag. No shape in the corpus is unaccounted for, and no tag exists that
the corpus lacks.

## `flags` is opaque to the XML layer

This bears on changing the value: every captured screen carries `flags="m"` because every capturing player was
male, so a female character is declined as male.

The literal `"flags"` is referenced **only by the two writers above**. The parser never compares against it — it
reads attributes generically. So the flag is not validated by the XML layer at all: an unrecognised value reaches
the localization lookup rather than failing the parse. An unknown flag therefore cannot fail the parse step; the
open question is what the lookup itself does with the value.

Still unknown: what the female token is. The LDB's per-string `langOrFlags` word is a
language selector (per-page, see `LdbStringIndex`), so it is not the declension source; the flag must be consumed
by an inflection path further down. Open work tracked in Gitea #418.
