# Corpus analysis pitfalls

How to measure a wire field, a rate or a mechanism against the capture dumps without the measurement inventing the
answer. The dumps are a small, biased sample: about a hundred captures of a handful of characters, each a few
minutes long, lossy in places, all against retail. Every pitfall below once produced a confident, plausible and
wrong result. Where to measure (the dumps, never the fixture slice) is in the root `AGENTS.md`, under "Measuring
against captures".

## Before any census

- **Print one event in order first.** Dump one instance as a chronological listing of every packet on the subject's
  handle before counting anything. It takes minutes and often answers what rounds of censuses could not.
- **Read one entity's own sightings in order** to separate a capability (this creature always has it) from a state
  (this entity has it now): every aggregate over entities is blind to that difference.
- **Never key a census on the thing being tested.** A table of "StatUpdate on every health drop" keyed on the
  StatUpdate proves itself; keyed on the drop it measures something.
- **Count arrivals before the parse.** "No packet carried this" and "every parse failed" print the same zero. The
  control counts the packets that reach the branch, before any parse, and a report prints it beside the result.
- **Push a known value through the same path** before reporting a miss: a lookup that returns nothing is a claim
  about the tool as much as the data. The control goes through the same call, flags and all, not a
  reimplementation.
- **Check the parser consumes each body exactly** before believing a negative result, and look for a repeated record
  type: `FirstOrDefault` on a record that repeats once per sub-entity is right on every capture with one and drops
  the rest. A parser that takes the longest run of a pattern never sees the earlier runs, and a decode failure in a
  stateful loop that keeps the previous value relabels data onto the neighbour: an empty bucket beside a
  suspiciously full one is one observation.
- **A tool's own confidence line is not evidence, and a control proves the method, not its scope**: a narrowed scan
  needs its control inside the narrowed scope. A code comment saying a field is "0 in every captured record" is a
  census with a date; re-run it. A permissive terminator does not stop a walk, it makes it drift, and row counts do
  not catch a relabelling (they are what hides it).

## Scoring a candidate

- **Score the non-modal half.** An 80% match against a field that is one value 83% of the time is base rate. Score
  only the records where the target is not its modal value, and print that beside the headline.
- **Report the excess over chance, not raw agreement**: two mostly-false columns agree on every row that has
  neither, so a rare bit beats a real one on raw agreement.
- **Purity is blind to a rate shift**, a three-class purity hides a binary signal, and purity rises for free with a
  candidate's cardinality (a handle, a position or a timer puts every sighting in its own group and scores 100%).
  A real answer is coarse and near-pure. Score candidates on the population they will see, not only the labelled
  set. `--report field-predictors` prints each candidate against the constant baseline.
- **Hits found by sweeping a parameter are not independent.** "Two 28-bit collisions cannot be chance" fails when the
  second came from the same sweep. Put every already-solved instance through the sweep and print where each ranks;
  estimate the false-positive rate empirically.
- **One outlier is a sampling question.** Raise the sample before building a theory on n = 1.
- **Count sightings, not distinct values.** A field can look continuous by distinct values and be bimodal by
  frequency; every integer id also parses as a float.
- **Before dividing observations by a catalog value, check the row's kind** (a percentage, a flat amount, a rate).
  **A ratio needs both sides in the same unit.** Normalising hits by a stated value in another unit yields a real
  quantity of the wrong kind that can mimic the answer. Two scales compared as one (wire against display) multiply
  every row by a constant and still read as evidence. A unit you cannot measure can often be absorbed by fitting the
  law in the unit you do measure.
- **A golden cannot catch a scale slip**, because it asserts the bytes chosen. Pin the relation between two fields of
  one record instead, and never argue from a number's roundness: a x100 scale makes every value round. Do not settle
  a scale from one sample, and remember an exact arithmetic fit can still have two readings.
- **`HealthMax` on the wire is a base, not the pool**; use the `HealthCurrent` high-water mark per handle as the
  denominator.
- **With one-sided noise the median is biased.** A missed tick only lengthens a gap, so a periodic interval is the
  tight cluster at the short end, and the single shortest sample is not a bound. Rounding amplifies a biased pivot.
- **Check two groups overlap on the uninteresting variables** before calling a split between them real, and say which
  error a classifier can afford (a false hostile is visible, a false friendly is not).

## Attribution

- **Key each record to its own event, never to a window.** "N records in the 45 packets after X" counts every other
  actor's traffic. The actor handle is the first cut; a behaviour's `CommandqueueServerId` groups its begin with its
  complete or abort.
- **Pair a counter's bumps with the burst they arrive in**, not with the event you assume they count. A counter that
  moves on a random-looking subset of events, with no property of the event behind it, is a chance roll.
  `IsDownload` marks compressed frames, not the server-to-client direction.
- **An annotation names an event, not the packets beside it.** Attribute traffic to it only when something
  corroborates it (the message text, a named token, a matching id).
- **`CMsg_StatChange` lines in the client log carry no entity id**, so "last value per stat" mixes the player with
  every NPC in the zone.
- **Find whose a field is by grouping by each candidate owner** (caster, target, spell, type) and counting how often
  it varies within a group: the owner it is most stable under is the answer. Deciding type versus placement needs
  both group-bys, keyed on the template id, never the name. Before calling a per-spawn field unstorable, cross-tab
  it against every other field of the same body: it may already be sent there. Key entities by `(capture, handle)`,
  since a handle is session-scoped, and run a control for the grouping method itself: it decides whether the method
  applies at all.
- **A per-event value that correlates with nothing may be a server-side counter.** Group by `(capture, source port)`:
  a capture spans several server processes, each with its own counter, and a per-capture sort interleaves them.
- **Dedup per handle, not per capture**: a moving entity is re-streamed within one session, so the per-capture
  reading inverts. Handles are recycled, and a respawn comes back on a new handle; prove a respawn timer from the
  spacing of the deaths, never from where the player stood. Timing from a capture is an upper bound: a re-sighting
  needs the player to come back. A counter's first push in a session has no previous value, so a delta-based count
  skips it, and a window count that comes out low can hide a real per-actor stream.

## What a constant or a gap means

- **Constant across the corpus can still be session state**: the corpus is a few characters whose state did not move
  while they were captured. Group the readings by capture and by character before calling a field definitional, and
  remember that first-writer-wins dedup in an extractor makes cross-capture disagreement invisible.
- **A value repeated across scopes that cannot share it is a placeholder or a coincidence**, never an identity.
  Count distinct parents, and compute the expected overlap before treating a non-overlap as a refutation.
- **A field that is always 0 may be the high half of a wider neighbour**; check the neighbour's type before hunting a
  consumer.
- **Readable text in a wire field can be leaked server memory.** Content is stable per type and belongs to the
  subsystem; a leak varies per sighting and mentions something else.
- **An all-NULL column is not dead schema** when it mirrors the client's protobuf or grammar; grep the codecs, not
  only the importer.
- **A documented enum member can be our own un-harvested default.** The tell is a decode that has to explain away
  its members' incoherence; check the value ever appears on the wire. A partially decoded row inherits C#
  defaults, so 0 must never double as a real enum member (`QuestGoalType` keeps 0 as "no trigger" for this reason).
- **A captured item that breaks a rule you just decoded may be legacy**: the captured characters are real retail
  characters carried across years of patches. Report such rows as a named state, and corroborate the rule from an
  independent field.
- **A counterexample that survives a good relation is often the relation at the wrong granularity**: test it one
  level up the containing structure (quest to quest line). State the direction of any bias in the population, and
  whether the relation is necessary or sufficient.
- **A condition written against the states a dump captured covers every state nobody captured.** Ask what each
  branch does for a player in a state no dump shows. `x != null` on a row that carries its own state column is the
  tell, and a fix is tested in both states, not only the one that reported the bug.

## Reporting a result

- **Print the watched count beside a non-observation.** "Never happened" and "never had time to happen" are the same
  zero; split by whatever decides how long each case was watched.
- **Print the rival explanation and the cases that contradict the tidy reading**; a report that prints only the
  supporting split is quoted as a decided fact.
- **A filter that infers a cause from a symptom eats the signal** whenever the signal shares the symptom ("a burst is
  a zone change", "not an NPC is the player"). Name the cause from the wire (a `MapChange`, a snapshot handle) and
  print the drop counts.
- **A per-file aggregate used as a per-event predictor invents outliers.** Resolve each event's own ends (its
  attacker and victim handles) and exclude the row when it cannot. A measurement wrong by a constant is worse than a
  missing one, because it still reads as evidence; a denominator the size of a dedup key space is the tell.
- **A count in a ticket is a hypothesis.** Re-derive it before doing the work it justifies, split it by a field that
  discriminates (it may be two phenomena), find the table that actually holds the thing (`Map.Name` is not unique:
  join on ids), and remember that a negative measured against our own import is a fact about the import. A row-count
  jump in an imported table invalidates every negative measured before it. A correction is not proof the row is now
  right, and a correction in a ticket comment leaves the ticket body wrong: fix the body.
- **Ask someone who played the game** before calling a decode refuted by a few odd captured rows.
