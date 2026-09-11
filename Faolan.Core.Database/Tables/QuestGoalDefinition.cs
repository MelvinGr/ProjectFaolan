using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One goal of a quest as it appears in the snapshot's @609 objective-detail region (decoded:
	///     <c>tools/capture/decode_snapshot_609.py</c>, codec QuestObjectiveRegion). The numeric fields + the two text
	///     refs regenerate the per-goal record byte-exact via <c>QuestObjectiveGoal.BuildSingle</c>.
	/// </summary>
	/// <remarks>
	///     The two refs are typed LDB references, and the client's own category table names them (see
	///     <c>docs/rdb/ldb-string-index.md</c>): the objective ref is always category 50304
	///     <c>quest_goal_templates.goal_name</c> - the goal's display text, an encrypted category readable only with
	///     the per-string key - while the trailing ref names the goal's target and is plaintext, its category saying
	///     what kind of thing it points at: 50200 <c>item.name</c>, 51000 <c>npc_template.name</c>, 51100
	///     <c>categorytree.description</c>, 51200 <c>bounded_areas.bounded_area</c>.
	/// </remarks>
	public class QuestGoalDefinition
	{
		/// <summary>
		///     The goal's wire objective id - globally unique, so it is the PK. It is what
		///     <see cref="CharacterQuestGoal.GoalId" /> FKs, and what QuestGoal echoes on the wire.
		/// </summary>
		/// <remarks>
		///     <c>DatabaseGenerated(None)</c> is load-bearing. Without it EF's convention treats an integer [Key] as
		///     store-generated, so adding a goal that already carries its wire id is taken to be an existing row: EF
		///     marks it Modified and emits an update, which affects 0 rows and throws
		///     <c>DbUpdateConcurrencyException</c> mid-import. The id comes from the wire and is never generated here.
		/// </remarks>
		[Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint GoalId { get; set; }

		/// <summary>
		///     The designers' name for the goal, in plaintext, from the client's quest catalog (RDB 1000104,
		///     <c>QuestGoal.name</c>; see <c>docs/rdb/quest-catalog-1000104.md</c>) - "Kill the Scavenger", "Enter the
		///     jungle". It is the same sentence the encrypted LDB category 50304 carries, but free: no page, no captured
		///     key. That makes it a useful cross-check on <c>--report ldb-quest-text</c> as well as content in its own
		///     right, and it covers all 7301 catalog goals against the 168 the corpus decrypts. Null for a goal the
		///     catalog omits.
		/// </summary>
		[MaxLength(256)]
		public string? Name { get; set; }

		/// <summary>
		///     The goal's phase within its quest - <c>QuestGoal.phase</c>, populated on every catalog row. 1-based, and
		///     it is the catalog's ordering of a quest's goals (the beach starter's "Enter the jungle" is phase 1 and
		///     "Kill slavemaster Saddur" phase 2). 0 for a goal the catalog omits.
		/// </summary>
		/// <remarks>
		///     This is the play order, not <see cref="Sequence" /> - see <c>QuestProgress.InPlayOrder</c>, which is how
		///     the runtime must ask for it. The two disagree on exactly one shipped quest, 12369 "The Acheronian Gate",
		///     and the corpus backs the phase: retail's <c>Advanced</c> hand-off is only sent on a goal that has a
		///     successor, and g7134 "Find the gate key" (phase 1, <c>Sequence</c> 2) is the one that takes it. It is
		///     also the field that orders the 7,147 goals which come from the catalog and were never on the wire, so
		///     carry no <c>Sequence</c> at all.
		/// </remarks>
		public uint Phase { get; set; }

		/// <summary>
		///     The map the goal's objective/marker sits on (all goal maps are in the Maps catalog). Null when the goal
		///     has no world marker: 449 of the 2850 captured @609 goals are like this, and mapId==0 coincides with
		///     markerX==markerY==0 in every one of them, so it is the wire's way of saying "no marker" rather than a
		///     missing value. Modelled nullable rather than 0 because the column is an FK: a 0 points at no Maps row
		///     and fails the constraint outright.
		/// </summary>
		public uint? MapId { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map? Map { get; set; }

		/// <summary>
		///     The objective text (always a <c>&lt;remoteformat category=50304&gt;</c>).
		/// </summary>
		public uint ObjectiveRefId { get; set; }

		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		public uint QuestDefinitionId { get; set; }

		/// <summary>
		///     The <c>Items.Id</c> of the quest item this goal is about - read per <see cref="GoalType" />, and it is
		///     the goal's own server-side statement of which item, independent of the wire's trailing reference.
		///     <code>
		///     LootFromNpc  the item the TargetName NPC's corpse bag must contain. Null = no specific item (the goal
		///                  still completes on looting the bag - it matches on the source NPC, not contents). When
		///                  set, the bag-drop guarantees it so the player can see and take it (Scavenger Tuut's bag ->
		///                  319224 "Scavenger's Key").
		///     TalkToNpc    the item handed over. A hand-in is triggered by the talk, so it is typed TalkToNpc, and
		///                  this is what makes it a delivery rather than an ordinary talk goal - see
		///                  QuestProgress.IsDelivery, which reads exactly that pair. It is what the player must be
		///                  carrying (CanHandIn) and what is taken when the hand-in goes through.
		///     </code>
		/// </summary>
		/// <remarks>
		///     A captured delivery names its item in the wire record (<see cref="Separator" /> = Deliver + a 50200
		///     trailing reference), but the 7,147 goals that come from the client's quest catalog carry no kind tag
		///     and no reference at all - only the objective sentence. Resolving that sentence ("Give the Crate of
		///     Steel to Turach" -&gt; Items 31055) has to land in a server-side column rather than an invented wire
		///     field. Data-driven either way: any quest's item drops, or is handed over, the same way.
		/// </remarks>
		public uint? QuestItemId { get; set; }

		[ForeignKey(nameof(QuestItemId))]
		public virtual Item? QuestItem { get; set; }

		/// <summary>
		///     The world object this goal is about - the door, gate or lever a <see cref="QuestGoalType.UseWorldObject" />
		///     goal is solved by using. Server-side only, like <see cref="TargetName" /> and
		///     <see cref="TargetTemplateId" /> beside it, and for the same reason: the goal record names its target
		///     nowhere on the wire.
		/// </summary>
		/// <remarks>
		///     It cannot reuse <see cref="TrailingRefId" />, which is where an Escort goal keeps its escortee and a
		///     UseItem goal its item. That field is part of the byte-exact @609 record, and goal 3492's is 0 in the
		///     capture - writing a world-object id into it would put bytes on the wire retail never sent. Points at
		///     <see cref="WorldObject.Id" />, the importer's own derived key, so it survives a re-harvest (see
		///     <see cref="DerivedKey" />).
		/// </remarks>
		public uint? TargetWorldObjectId { get; set; }

		[ForeignKey(nameof(TargetWorldObjectId))]
		public virtual WorldObject? TargetWorldObject { get; set; }

		public uint SubId { get; set; } // the trailing record's id echoed in the header (= TrailingRefId)

		/// <summary>
		///     The trailing text record: a <c>&lt;remoteformat category=51200&gt;</c> or a
		///     <c>&lt;localized category=51000 flags=..&gt;</c>.
		/// </summary>
		public uint TrailingRefId { get; set; }

		/// <summary>
		///     What server-side action advances/completes this goal. The wire output of a completion is identical for
		///     every type (<c>QuestProgressionService</c>); the type only selects which trigger matches:
		///     <code>
		///     Kill        - a kill of the NPC named TargetName (QuestProgressionService.AdvanceKillGoals).
		///     LootFromNpc - looting the corpse bag of the NPC named TargetName (LootService); the actual quest item is
		///                   a quest-specific RDB item we do not materialise, so the bag's source NPC name is the key.
		///     TalkToNpc   - opening the greeting of the NPC named TargetName (ConversationService).
		///     Escort      - escorting the NPC in TrailingRefId to a destination.
		///     </code>
		/// </summary>
		/// <remarks>
		///     The default is <see cref="QuestGoalType.None" /> - no trigger - and that is load-bearing: an un-typed
		///     row must never claim a trigger, or the thousands of catalog goals with no verb and no target would
		///     false-fire on it. A goal becomes a kill goal by carrying the client's kill verb (<see cref="Separator" />
		///     = <see cref="QuestGoalKind.Kill" />), not by nobody having typed it.
		/// </remarks>
		public QuestGoalType GoalType { get; set; } = QuestGoalType.None;

		/// <summary>
		///     True when this row came from mid-session traffic (a QuestFullUpdate) instead of a login snapshot's
		///     Quest_c record. The full-update carries the verb and the count for every goal (the 34 goals the corpus
		///     shows in both sources agree on both fields), but never the target: <see cref="TargetName" /> is not on
		///     the wire in either source and is sourced by capture correlation and domain knowledge.
		/// </summary>
		/// <remarks>
		///     With no <see cref="TargetName" /> a Kill/LootFromNpc/TalkToNpc goal has nothing for a trigger to match,
		///     so nothing can ever advance it, and AllGoalsComplete requires every goal of a quest - which would leave
		///     its quest permanently incompletable. <c>QuestProgress.AllGoalsComplete</c> skips these for exactly that
		///     reason. They are worth having anyway: without them a two-goal quest silently looks like a one-goal
		///     quest. The wire proves 12369 (7134 -&gt; 3492) and 10287 (400 -&gt; 7363) each have two, and both
		///     second goals appear in the corpus only mid-session, because their quests were completed between login
		///     snapshots.
		/// </remarks>
		public bool Partial { get; set; }

		public bool TrailingLocalized { get; set; }

		/// <summary>
		///     The per-string LDB decryption key for the objective text (LdbKey) - stored as the 128-bit TEA key, the
		///     same <see cref="ProtoDecryptionKey" /> shape an item template's key uses, rather than as the
		///     20-character wire string. The string is a pure base-85 rendering of these four words and regenerates
		///     from them exactly (<c>LdbKey.ToWire</c>), so nothing is lost; it is never a key or a lookup here, only
		///     payload written into the outgoing attribute. An all-zero key means the capture carried none - see
		///     <see cref="ProtoDecryptionKey.IsEmpty" />.
		/// </summary>
		public virtual ProtoDecryptionKey ObjectiveKey { get; set; } = new ProtoDecryptionKey();

		/// <summary>
		///     A text record addresses its string by numeric id or by a name token (<c>token="Noname"</c>). Non-null
		///     selects the token form; the RefId is then unused. Modelling only the id form renders <c>id="0"</c> where
		///     the wire had a token - 8 bytes short, and everything after it desyncs.
		/// </summary>
		/// <remarks>
		///     Null on all 7,317 shipped rows, which is not evidence the column is dead. The token form is proven live
		///     on the sibling slot - <see cref="TrailingToken" /> is set on 52 rows, harvested from the same records
		///     by the same code - so the alternative is real in the grammar and this slot simply never takes it in the
		///     corpus we have. Dropping it would re-introduce the desync the first time one does.
		/// </remarks>
		[MaxLength(128)]
		public string? ObjectiveToken { get; set; }

		/// <summary>
		///     The NPC this goal is about, interpreted per <see cref="GoalType" />: the NPC to kill (Kill), whose corpse
		///     bag to loot (LootFromNpc), or to talk to (TalkToNpc / Escort). Server-side only - not on the wire: the
		///     goal record carries the count but not the target (it is in the RDB-resolved objective text). Sourced
		///     per quest by capture correlation (the NPC at the QuestGoal-complete frame) + domain knowledge (e.g.
		///     Casilda's chain: 13479 = kill "Scavenger Tuut", 13563 = loot "Scavenger Tuut"'s bag, 13480 = talk
		///     "Casilda"). Null/empty = target not yet sourced.
		/// </summary>
		[MaxLength(128)]
		public string? TargetName { get; set; }

		/// <summary>
		///     The NPC template this goal is about - the same target as <see cref="TargetName" />, resolved to the
		///     stable id. Resolved, not harvested: only 6 goals carry a <c>51000</c> reference it can be read off
		///     directly, and the other 272 get it by looking their name up in the catalog
		///     (<c>ImportOps.ResolveGoalTargetTemplates</c>). It adds no information - it freezes a lookup, which is
		///     the point: the ambiguity is settled once, at import, with the goal's own evidence to hand, instead of
		///     being re-guessed by every matcher from a string.
		/// </summary>
		/// <remarks>
		///     Prefer this over <see cref="TargetName" /> wherever a trigger matches a live entity: a template is
		///     exactly one kind of NPC, while a display name is ambiguous in the world - <c>Saddur</c> is two templates
		///     (2993 on the Stygian Galley, 9500 on Tortage Island) and <c>Halen van Dummy</c> is 27. Keying the
		///     quest-gate map on the name hides the galley Saddur from every player not holding a Tortage Island quest,
		///     because goal 3490 flags the beach one.
		///     <para>
		///         Null on 586 of the 864 goals that name a target, and that is not a resolution failure to fix by
		///         relaxing the rule - the world data is incomplete. Those goals name NPCs (<c>Genzio</c>,
		///         <c>Captain Sakumbe</c>, <c>Mithrelle</c>) on maps that have not been imported yet, so there is no
		///         <c>NpcTemplates</c> row to point at and an FK cannot hold a dangling id. They keep matching by name
		///         and light up on their own as those maps land.
		///     </para>
		///     <para>
		///         So <see cref="TargetName" /> stays and is not deprecated by this: it is the fallback for those 586,
		///         the only key a "kill N of &lt;group&gt;" goal can use (a group has no single template), and the
		///         human-readable label in reports. The pair is the arrangement <c>MatchingKillGoal</c> has via
		///         <see cref="TrailingRefId" /> - id first, name second - available to the talk and loot paths too.
		///     </para>
		/// </remarks>
		public uint? TargetTemplateId { get; set; }

		[ForeignKey(nameof(TargetTemplateId))]
		public virtual NpcTemplate? TargetTemplate { get; set; }

		/// <summary>
		///     True when this goal's target is streamed only to a player holding this quest active
		///     (<c>DatabaseRepository.GetQuestGatedNpcMap</c> -&gt; <c>WorldEntity.GateQuestIds</c>). Saddur and
		///     Scavenger Tuut are the two, and retail solo-instances both.
		/// </summary>
		/// <remarks>
		///     A hand-set column, never a rule implied from "a Kill goal that has a TargetName": the gate keys on the
		///     name, so it hides every same-named NPC from every player not on that quest, and pairing it with
		///     <see cref="TargetStaysDead" /> caps a goal asking for N kills at however many spawns the world holds
		///     (quest 12057 asks for 9 Vanir Polluter kills and the world holds 8 spawns). Whether a kill target is a
		///     unique story NPC or a population mob is an editorial call the client's data does not state anywhere
		///     readable.
		/// </remarks>
		public bool GatesTargetVisibility { get; set; }

		/// <summary>
		///     True when killing this goal's target is a one-time story event, so the NPC does not come back
		///     (-&gt; <c>WorldEntity.NoRespawn</c>).
		/// </summary>
		/// <remarks>
		///     Independent of <see cref="GatesTargetVisibility" />, even though the two flagged goals set both: a
		///     repeatable or retryable quest whose target is quest-only is gated but has to respawn, and a story kill
		///     target that everyone can see stays dead but is not hidden. One column cannot express either.
		///     <para>
		///         Being a story kill target is also not the same statement as the gate:
		///         <c>GetQuestGatedNpcMap</c> reveals a gated NPC to every quest that targets it (killing Tuut
		///         completes the quest that reveals him, and his looted-for key must stay obtainable), while this flag
		///         is read per target with no such widening. Editorial, like its neighbour: the client's data states
		///         neither anywhere readable.
		///     </para>
		/// </remarks>
		public bool TargetStaysDead { get; set; }

		/// <summary>
		///     A precondition the client states in the goal's own objective text, decoded with it. Two shapes exist in
		///     the corpus and both are recorded even though only one can be enforced yet:
		///     <code>
		///     "Talk to Sharak (level 30)"                                     -> RequiredLevel 30
		///     "…to Itsing When You Have Become an Acolyte (Rank 1)"           -> RequiredRankTitle "Acolyte", RequiredRank 1
		///     </code>
		/// </summary>
		/// <remarks>
		///     Recorded rather than acted on at wiring time, deliberately. A goal with an unmet precondition must not
		///     advance - a bare talk trigger would complete it early, a silent chain bug - so the goal is wired
		///     normally and the gate is enforced at the trigger (<c>QuestProgress.PreconditionMet</c>), which is the
		///     one place that knows the live player. When faction ranks exist, only the rank branch of that predicate
		///     changes; nothing has to be re-harvested and no goal has to be re-wired.
		/// </remarks>
		public uint? RequiredLevel { get; set; }

		/// <summary>
		///     The faction title the client names ("Acolyte", "Pickpocket", "Thug"), verbatim. A string rather than an
		///     FK because the faction/reputation system does not exist yet; when it does, this is what resolves to it.
		///     Open work tracked in Gitea #310.
		/// </summary>
		[MaxLength(64)]
		public string? RequiredRankTitle { get; set; }

		public uint? RequiredRank { get; set; }

		[MaxLength(64)]
		public string? TrailingFlags { get; set; }

		/// <summary>
		///     The trailing text record's LDB decryption key; see <see cref="ObjectiveKey" />.
		/// </summary>
		public virtual ProtoDecryptionKey TrailingKey { get; set; } = new ProtoDecryptionKey();

		[MaxLength(128)]
		public string? TrailingToken { get; set; }

		/// <summary>
		///     The goal record's reserved words [1..6] - three optional (marker, ACGHash-token) slots at
		///     [1..2]/[3..4]/[5..6], which refer to the goal's own target and NPC (<c>QuestObjectiveGoal.Reserved</c>
		///     carries the measured shape). Stored rather than derived because the token resolves to nothing we hold -
		///     the client never dereferences it either.
		/// </summary>
		/// <remarks>
		///     The numbering is the wire's, and it starts at 1 because reserved[0] is not definition data: it is
		///     <c>QuestGoal_c::SolvedTimes</c>, the goal's per-character progress counter. It must never be harvested
		///     here - that would ship one retail character's half-finished objectives to every player - and comes from
		///     the live character through <c>QuestObjectiveRegions.ForQuest</c>'s <c>solvedTimesOf</c>.
		///     <para>
		///         Harvested from both wire sources - the snapshot journal (<c>ObjectiveGoalExtractor</c>) and the
		///         live QuestFullUpdate (<c>QuestDefinitionExtractor</c>) - which agree on every goal seen by both.
		///     </para>
		/// </remarks>
		public uint Extra1 { get; set; }
		public uint Extra2 { get; set; }
		public uint Extra3 { get; set; }
		public uint Extra4 { get; set; }
		public uint Extra5 { get; set; }
		public uint Extra6 { get; set; }

		/// <summary>
		///     The word echoing the goal id - either the goal id or 0, stable per goal but not derivable from it (1567
		///     of the 2850 captured goals carry 0, 1283 carry the id).
		/// </summary>
		public uint GoalIdEcho { get; set; }
		public uint ObjectiveCategory { get; set; }

		/// <summary>
		///     The goal's position in the @609 objective region. Not derivable from <see cref="Sequence" />: several
		///     quests carry two goals both with N=1 (12068, 12057), so ordering by Sequence is ambiguous - quest
		///     12057's wire order is [3542, 2949] while Sequence gives [2949, 3542].
		/// </summary>
		public uint RegionOrder { get; set; }

		/// <summary>
		///     The goal's required amount - how many kills/collects/etc. complete it. Decoded from the QuestFullUpdate /
		///     snapshot @609 goal record (the word after goalIdRepeat; values 1/3/4/8/12/64 seen on the wire). 0 = not
		///     set, treated as 1 by QuestObjectiveRegion.Build (preserves byte-exact output for rows seeded before this
		///     field).
		/// </summary>
		public uint RequiredCount { get; set; }

		/// <summary>
		///     The [1][k] separator's k word - and it is the goal's kind tag, not a framing artefact: see
		///     <see cref="QuestGoalKind" /> for the decode and its evidence. Kept under its wire-derived name because it
		///     is a captured field.
		/// </summary>
		/// <remarks>
		///     Null = this goal has never been seen on the wire, so it has no kind - which is every goal that comes
		///     from the client's quest catalog (RDB 1000104) rather than a capture. Nullable rather than 0 because there
		///     is no kind 0 (see <see cref="QuestGoalKind" />): reusing 0 for "never captured" would make a real
		///     harvest miss indistinguishable from an uncaptured goal.
		///     <c>SeedContentTests.NoGoalCarriesTheUnharvestedKindZero</c> fails on a literal 0, so a harvest miss is
		///     caught.
		/// </remarks>
		public uint? Separator { get; set; }
		/// <summary>
		///     The per-goal ordinal <c>N</c> the wire carries (1, 2, ...) - a captured value, written straight back out
		///     by QuestObjectiveRegions and byte-exact verified.
		/// </summary>
		/// <remarks>
		///     It is not the play order, and using it as one is a live trap. Five shipped quests carry <c>N</c> = 1 on
		///     every goal - 14020, 16004, 16183, 16888, 17350 - because their objectives are parallel ("collect all
		///     seven faction documents", "kill all three", "use all five stones on the standing stone") and retail has
		///     no order to state. Renumbering them 1..n to make them sortable rewrites bytes retail sent, and
		///     <c>CompletedQuestJournalGenerationTests</c> fails on it at once. The play order is <see cref="Phase" />;
		///     ask for it through <c>QuestProgress.InPlayOrder</c>.
		/// </remarks>
		public uint Sequence { get; set; }
		public uint TrailingCategory { get; set; }

		/// <summary>
		///     The goal's map-marker radius, in world units - the word after the marker block, stable per goal across
		///     every capture. Named by the client: <c>QuestGoal_c</c>'s reader lands it at +0x68 and the map waypoint
		///     code (<c>FUN_013a26c0</c>) hands <c>(float)+0x68</c> to the goal's waypoint as a scalar of its own,
		///     beside the (MarkerX, 0, MarkerY) position. Two independent client-side namings agree: the argument list
		///     of <c>DispatchAddWayPoint</c> (<c>docs/re/aoc-client-rpc-methods.json</c>; its reader
		///     <c>FUN_004c9860</c> shows <c>i_vRadius</c> is one word at [10], after <c>i_cID</c> [4..5],
		///     <c>i_nPlayfieldID</c> and <c>i_cPos</c> [6..8]) and the <c>addmapmarker</c> console command, whose
		///     handler (<c>FUN_00a12680</c>) calls the same writer (<c>FUN_0139f520</c>) and registers its fourth
		///     parameter as "Radius of marker".
		/// </summary>
		/// <remarks>
		///     Over the 171 goals that carry a <see cref="Separator" /> it appears only on Collect (14 of 44) and Kill
		///     (10 of 30) goals - the kinds whose target roams - as 3, 10, 20, 25, 40, 50 (x11), 60, 80, 90, 100, 105,
		///     130, 150, 170, and it does not track <see cref="RequiredCount" /> (count 64 and count 1 both carry 50).
		///     0 means no extent: a plain pin.
		/// </remarks>
		public uint MarkerRadius { get; set; }

		/// <summary>
		///     The byte after <see cref="MarkerRadius" /> on the wire (QuestGoal_c +0x6c), named from its consumer:
		///     when set, the map waypoint code places no marker for a goal on the current playfield
		///     (<c>FUN_013a26c0</c>). Its 17 carriers are the dungeon objectives on quests 15378-17774 (Xibaluku, the
		///     Jade Artifact, Kian Lai, Lao-che, Kun Whu, Po-Sha, Zhu-la, Balbet-Zhun, Blood Ravine, ...), several with
		///     degenerate (1,1) markers - exactly the goals whose "where" cannot be a pin.
		/// </summary>
		public bool HideMapMarker { get; set; }

		/// <summary>
		///     The marker coordinates, null exactly when <see cref="MapId" /> is - the three travel as one block on
		///     the wire.
		/// </summary>
		/// <remarks>
		///     The marker is a map pin, not a trigger. For a ReachArea goal the real volume is <see cref="Region" />,
		///     and the two are not the same shape: goal 1448's marker (416, 701) sits inside <c>Trappers Haven</c>, a
		///     96 x 82 region, while the marker-radius fallback tests a 40-unit circle centred on the pin.
		/// </remarks>
		public uint? MarkerX { get; set; }
		public uint? MarkerY { get; set; }

		/// <summary>
		///     The client's trigger volume for this goal, resolved from the typed reference the goal already carries:
		///     <see cref="TrailingCategory" /> 51200 is <c>bounded_areas.bounded_area</c>, so
		///     LDB[51200][TrailingRefId] is the region's name and that names a region in the playfield's RDB 1000053
		///     record. Verified end to end on the beach starter - goal 1448 -&gt; LDB[51200][180] = "Trappers Haven"
		///     -&gt; a 7-vertex region on map 4010 that contains the goal's own marker.
		/// </summary>
		/// <remarks>
		///     Null for every goal that carries no 51200 reference (only 3 in the whole catalog do), and those keep the
		///     marker-radius test. SetNull so dropping a region degrades the goal rather than deleting it.
		///     <para>
		///         A 51200 reference does not by itself make a goal a ReachArea - goal 11712 has RequiredCount 3, so
		///         its region scopes the objective ("3 of something in there") instead of being it. Settling that
		///         needs the goal's own objective text. So this column is the geometry only; <see cref="GoalType" />
		///         stays whatever the wiring says.
		///     </para>
		/// </remarks>
		public uint? RegionId { get; set; }

		[ForeignKey(nameof(RegionId))]
		public virtual MapRegion? Region { get; set; }
	}
}
