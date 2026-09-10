using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// One live entity in the world simulation - a player or an NPC - identified by its session-scoped wire handle.
	// This is the authoritative, in-memory state the server owns (position/rotation, map), as opposed to the
	// per-session captured state the replay path used. The roadmap (Gitea #404) promotes the
	// scattered per-session maps into this shared model. Vitals/AI/buffs will hang off this as those systems land.
	public sealed class WorldEntity
	{
		// ⭐ The corpse is NOT the loot bag, and it does not pop after the death animation. Measured over the whole
		// retail corpus (`--report corpse-timing`, 275 NPC deaths across 134 captures): the bag is a SEPARATE dynel
		// spawned in the SAME packet burst as the death, and the body then lies BESIDE it for another half-minute:
		//
		//   death -> loot bag spawns (delay 0.00 s median over 100 matched kills, its own handle, 0.8 u away)
		//         -> n3ToClientFade on the corpse handle at 30.72 s (median, n=229)
		//         -> n3ToClientQuit on the corpse handle at 33.18 s (median, n=228)
		//
		// We used to do the opposite of all three: hold the bag back 3 s, then despawn the corpse to make room for it.
		// So the drop is now immediate (TickCorpse -> Drop) and these two govern only the BODY.
		public const double CorpseFadeSeconds = 30.7;

		// The gap between the corpse's fade and its quit (33.18 - 30.72). The fade itself plays a 2.0 s dissolve - see
		// CorpseFadeAnimationSeconds - so the handle is dropped just after the client has finished dissolving it.
		//
		// ⚠️ Measuring this needs the fade as the anchor, not "the first quit after the death". A dead entity is still
		// interest-managed - walk away from a body and it quits, walk back and it re-spawns - so the first quit is
		// often that one, tens of seconds early. Reading it that way put the median at 33.18 s but the MINIMUM at
		// 6.96 s, which is the tell.
		public const double CorpseQuitDelaySeconds = 2.5;

		// The `fadeSeconds` float the corpse's n3ToClientFade carries. INVARIANT by entity class across the corpus: a
		// Character (0xC350) fades in 2.0 s in all 229 captured corpse fades, a loot bag (0xC87A) in 3.0 s - which is
		// why this is here and not left on N3ToClientFadeCodec.DefaultFadeSeconds (that default IS the bag's 3.0).
		public const float CorpseFadeAnimationSeconds = 2.0f;

		public const uint DispositionFriendly = 0;

		public const uint DispositionHostile = 1;

		public const uint DispositionNeutral = 2;

		// The monotonic per-entity counter carried in field 3 of the continuous idle heartbeat (short-form
		// BehaviorControlCenterIIR, #225). Incremented once per emitted pulse so the client sees a steadily rising tick,
		// as retail's stream does.
		public uint HeartbeatTick;

		// How far this death's corpse timeline has already been driven, so each stage fires exactly once (see TickCorpse).
		private CorpseStage _corpseStage;

		// Time (seconds) until this NPC may swing again; counts down while it has a target in range.
		private double _attackCooldownRemaining;

		// Seconds since this entity died, advanced by TickCorpse only. Drives the drop -> fade -> quit sequence above.
		private double _corpseAge;

		// Time (seconds) until this defender moves a shield to another arc (#20); counts down while engaged.
		private double _shieldShiftRemaining;

		// Remaining time (seconds) until a loot bag expires.
		private double _expiryRemaining;

		// The MapArea.AreaNameId the player is currently standing in, or 0 for none (#348). Live world state, never
		// persisted: it exists so the tick can tell a CROSSING (banner) from merely still being inside the area.
		public uint CurrentAreaNameId { get; set; }

		// Fall tracking (#495/#504). The client tells us it is off the ground in every move record: `flag1` clears
		// NoAccelerationVector while airborne, because it starts sending an acceleration vector the instant the
		// character leaves the ground (see MoveFlag). So the server needs no physics of its own - it watches that bit
		// and measures the drop between the airborne run's HIGHEST point and where the run ended.
		//
		// The apex, not the takeoff height: a jump rises before it falls, and the fall that hurts is measured from the
		// top of the arc. In chat-annotated-3's 19:43:30 fall the player jumps from y=324.6 up to 327.0 and lands at
		// 315.1 - 11.9 units of fall, not the 9.5 the takeoff height would give.
		public bool Airborne { get; set; }

		// The highest Y reached during the current airborne run; meaningless while grounded.
		public float AirborneApexY { get; set; }

		// The fastest DOWNWARD speed the client itself reported during the current airborne run, in world units per
		// second (a positive number), or 0 if it reported none. #515: this is what fall damage is resolved from -
		// the client's own number, sampled at its own rate, where the apex-minus-landing drop above is reconstructed
		// from position samples ~200 ms apart and loses a fraction of a unit at each end. Over the corpus's 34 falls
		// v² explains the damage at r = 0.96 against the drop's 0.63.
		//
		// ⚠️ The MAXIMUM over the run rather than the last sample: a fall accelerates, so the fastest report IS the
		// one nearest the ground, and taking the maximum survives a stray record arriving after the landing.
		public float AirborneFallSpeed { get; set; }

		// The Y of the last move that was still on the GROUND. This, not the previous position, is what seeds a fall's
		// apex: a fall off a ledge has no rising samples at all, so its highest point is the ground it left. Tracked
		// here rather than read from Position because the move handler commits the new position BEFORE the fall check
		// runs, at which point "the previous position" is no longer available to compare against.
		public float LastGroundedY { get; set; }

		// The MapHazard.RegionId the player is currently standing in, or null for none (#504). Same crossing-vs-inside role
		// as CurrentAreaNameId above: a hazard fires on ENTRY (the client's own feedback token names the trigger
		// `playerenter(3)`), so re-firing it every tick while the player burns would be wrong.
		public uint? CurrentHazardId { get; set; }

		// The creature's death-animation behaviorId (the death BccIIR field-1, #362), from its NpcTemplate. Defaults to
		// the humanoid biped death (DeathSequence.HumanoidDeathBehaviorId); overridden per-type where harvested (demon /
		// quadruped). Read by CombatService.OnNpcKilled when it sends the death BccIIR.
		public uint DeathBehaviorId { get; set; } = DeathSequence.HumanoidDeathBehaviorId;

		// How long that animation runs for THIS creature, in centiseconds - from its NpcTemplate (#781). Null for a
		// creature no capture measured; `DeathSequence` then uses the animation's own modal length.
		public uint? DeathActivationTime { get; set; }

		// For an NPC: a quest that, once the VIEWER has COMPLETED it, HIDES this NPC (they don't spawn / are despawned).
		// The complement of GateQuestIds ("show while active") - e.g. Casilda vanishes from the beach once her escort
		// quest is completed. Data-driven from the Escort goal (escortee template -> escort quest id). null = never hidden.
		public uint? HideWhenQuestCompletedId { get; set; }

		// The spawn record to send once this escortee is FREED - its own body with the story pose swapped for the
		// creature's ordinary idle, and in WALK mode (#827). Null for everything that is not a posed escortee.
		//
		// Two bodies rather than a patch because the pose is a protobuf varint inside the CoreCharacter message, and
		// `SpawnTransformPatch` only stamps fixed-width fields into a built record. Chosen by `Following`, which is
		// what "freed" physically means - she is either chained to her post or walking behind someone - so the choice
		// costs no per-stream-in quest lookup.
		public byte[]? FreedSpawnBody { get; set; }

		// Whether this NPC is the ESCORTEE of a quest - the same Escort goal that fills HideWhenQuestCompletedId is what
		// says so, so the flag is data-driven and needs no per-NPC list.
		//
		// An escortee is never idle in the ambient sense (#827). Before its rescue it is held in a SCRIPTED POSE -
		// Casilda's captive pose is a BehaviorControlCenterIIR (#267) and an idle emote played over it simply replaces
		// the pose, which is what put a chained woman through a scratch-and-look-around routine. After the rescue it is
		// walking behind the player, and the standing gate already suppresses idles there.
		//
		// ⚠️ This is not the same thing as "has no ambient animations". Retail DOES animate her - reacting when an
		// enemy attacks the player and when that enemy dies - but those are REACTIONS to combat events, a different
		// system from the self-timed idle rotation. Our harvest cannot tell the two apart and filed hers as idles
		// (three rows on template 9411), which is tracked separately; suppressing the rotation is correct either way,
		// because none of those three is an idle.
		public bool IsQuestEscortee => HideWhenQuestCompletedId != null;

		// Which instance (copy) of MapId this entity lives in (#268). Equals its Zone's PlayfieldToken - the retail
		// playfield-instance id. 0 until placed; WorldState.Place sets it from the resolved/created instance so every
		// later ZoneFor(entity) lands in the same copy. Two entities only see each other if MapId AND InstanceId match.
		public uint InstanceId { get; set; }

		// Live transform + map. Mutable: updated by Move (players) and the AI tick (NPCs, later).
		public uint MapId { get; set; }

		// The NPC's TYPE id (NpcTemplateId), or 0 for players/loot. The stable per-type key used to look up shops and
		// conversations without the shared-placeholder-name collision (many NPCs share an internal name, e.g. 25 share
		// "Halen van Dummy"). 0 for players (they aren't shop/conversation NPCs).
		public uint NpcTemplateId { get; init; }

		// The Khitai faction this creature belongs to (`NpcStatCore.FactionId`), or null - which is every base-game
		// NPC, since the faction ids are the expansion's. Read by the kill path to award reputation (#310).
		public uint? FactionId { get; set; }

		// #363 ambient spawner: the NpcSpawnArea this NPC is a member of (its spawn-group pool), or null for a
		// capture-placed / named / summoned NPC. When set, the area POOL maintains this creature's population (respawns
		// a replacement at a free area point when it dies) instead of the per-entity respawn - and its wander waypoints
		// are the area's points, so it roams the group's area.
		public uint? SpawnAreaId { get; set; }

		// Active buffs/effects on this entity, keyed by buff (spell) id - reapplying refreshes the entry.
		// On a server-initiated cancel the server pushes the decoded buff-removal IIR (#253: the ApplySpell rank-1
		// "end" twin with an empty payload) so the client clears the icon live. On the client-initiated CancelBuff
		// (0x201E right-click) retail sends only a FormatFeedback text - the client removes its own icon - and a
		// timed buff is expired client-side from the ApplySpell duration. Durations come from the RDB spell defs once
		// imported (TODO 5); entries with one expire on the tick, the rest live until cancel/disconnect. Stat effects
		// land with the same spell-def work.
		public ConcurrentDictionary<uint, ActiveBuff> ActiveBuffs { get; } = new ConcurrentDictionary<uint, ActiveBuff>();

		// Removes every buff that has expired at `now` and returns them, so the caller can send each one's end-twin.
		//
		// ⚠️ The SNAPSHOT is load-bearing, not a style choice. The world tick used to `foreach (var b in ActiveBuffs)`
		// and `TryRemove` inside that loop; a ConcurrentDictionary enumerator walks the live buckets, so removing the
		// node it is standing on can skip the NEXT node in the same bucket. With one buff active that is invisible -
		// with several expiring together it silently left some behind, and because the client never self-expires an
		// icon (#274) those stayed on the bar at 0s forever. `ToArray()` takes a real snapshot first, so every expired
		// buff is considered exactly once no matter what the removals do to the bucket chains.
		//
		// It also closes a second hole: the tick awaited network sends INSIDE that enumeration, leaving a wide window
		// for a concurrent apply/cancel (Buffs.cs, the CancelBuff handler) to reshape the dictionary mid-walk. Deciding
		// and removing up front means an await can no longer invalidate the iteration, and TryRemove's return value
		// keeps the removal itself the single winner - a buff cancelled by the player in that window is not reported
		// here as well.
		public List<KeyValuePair<uint, ActiveBuff>> TakeExpiredBuffs(DateTimeOffset now)
		{
			var expired = new List<KeyValuePair<uint, ActiveBuff>>();
			foreach (var buff in ActiveBuffs.ToArray())
			{
				if (buff.Value.Expired(now) && ActiveBuffs.TryRemove(buff.Key, out _))
					expired.Add(buff);
			}

			return expired;
		}

		// The class/level BASE this entity's stats start from (#444), by wire stat id, in the WIRE scale - the five
		// attributes, MaxHealth, and the casters' Non Combat Mana Regen base. Loaded once at world entry from
		// ClassAttributeBases, which covers all 12 classes at all 80 levels. EMPTY means "no row for this (class,
		// level)" - UNKNOWN, not "no base" - and a character there regenerates and mitigates below retail.
		public IReadOnlyDictionary<CharacterStatId, int> ClassBase { get; set; } = new Dictionary<CharacterStatId, int>();

		// The GEAR-derived stat bonuses this entity entered the world with, in display units by wire stat id
		// (MaxStatAggregator over its equipped items). Kept, rather than only folded into the rating fields below, so
		// the buff engine can recompute `gear + active buffs` from a fixed baseline instead of adding and subtracting
		// deltas as buffs come and go (BuffStatEffects.Sync). Empty for NPCs and for a player with no stat gear.
		public IReadOnlyDictionary<CharacterStatId, int> GearBonuses { get; set; } = new Dictionary<CharacterStatId, int>();

		/// <summary>
		///     This entity's out-of-combat MANA regen base, in display units per second - the flat term
		///     <c>RegenRates.ManaPerSecond</c> adds on top of the Int+Wis sum. 0 for a class that has none, which is
		///     every rogue and soldier.
		/// </summary>
		/// <remarks>
		///     ⭐ Unlike health's and stamina's, mana's flat term is CLASS data rather than a universal seed, and it
		///     comes from two client sources that add: <see cref="ClassBase" />'s stat 280 and the auto-trained
		///     <c>Mage</c> / <c>Priest</c> feat (#863). Both are RAW WIRE (the stat's display scale is 100), which is
		///     why this divides and the attribute path does not - and no item in the catalog carries 280, so the
		///     bonus dictionary's entry can only have come from that feat.
		///     <para>
		///         It reproduces all three captured observations: 2200 for a level-80 Necromancer or Demonologist
		///         (a 200 class base plus the 2000 feat), 2000 for a level-24 Herald of Xotli (whose class templates
		///         carry no 280 row at all) and nothing for a level-23 Assassin, whose class trains neither feat.
		///         Those are the numbers <c>RegenRates</c> recorded as observed-but-unsourced.
		///     </para>
		/// </remarks>
		public double NonCombatManaRegenBase =>
			(ClassBase.GetValueOrDefault(CharacterStatId.NonCombatManaRegen)
			 + GearBonuses.GetValueOrDefault(CharacterStatId.NonCombatManaRegen))
			/ (double)CharacterStatId.NonCombatManaRegen.GetScale();

		/// <summary>
		///     The per-SPELL boosts this entity's trained feats grant (#590), as <c>spellId -> percent x 100</c> - a
		///     Necromancer with `Decomposition` rank 5 carries +3500 on every RANK of Flesh to Worms. The feat names a
		///     spell LINE; the fan-out to its ranks is resolved once at world entry (beside the gear bonuses) so the
		///     cast path stays a dictionary lookup. Empty for NPCs and for a character with no spell feats.
		/// </summary>
		public IReadOnlyDictionary<uint, int> SpellBoosts { get; set; } = new Dictionary<uint, int>();

		/// <summary>
		///     This entity's feat boost for one spell, in hundredths of a percent; 0 when it has none, which is most
		///     spells for most characters. Passed to <c>CombatResolver.ResolveSpellDamage</c>.
		/// </summary>
		public int SpellBoost(uint spellId)
		{
			return SpellBoosts.GetValueOrDefault(spellId);
		}

		/// <summary>
		///     What this entity's trained feats do to their COMBOS (#697), as <c>comboId -> bonus</c> - the opening
		///     and finishing weapon attacks' damage and crit rating (GameAction 53250) plus the whole-combo damage
		///     that lands on the finisher spell (53209). Resolved once at world entry beside
		///     <see cref="SpellBoosts" />, so a swing is a dictionary lookup. Empty for NPCs and for a character with
		///     no combo feats, which is most of them.
		/// </summary>
		public IReadOnlyDictionary<uint, ComboFeatBonus> ComboBonuses { get; set; } = new Dictionary<uint, ComboFeatBonus>();

		/// <summary>This entity's feat bonus for one combo; <see cref="ComboFeatBonus.None" /> when it has none.</summary>
		public ComboFeatBonus ComboBonus(uint comboId)
		{
			return ComboBonuses.GetValueOrDefault(comboId, ComboFeatBonus.None);
		}

		/// <summary>
		///     The PROCS this player's trained feats hang off its combos and spell lines (#697), resolved once at world
		///     entry like <see cref="ComboBonuses" />. ProcService reads them on a combo landing and a cast completing.
		///     Empty for NPCs and for a character with no proc feats.
		/// </summary>
		public IReadOnlyList<FeatProc> Procs { get; set; } = [];

		/// <summary>
		///     The per-SPELL scaling-attribute OVERRIDES among this entity's known spells (#592), as
		///     <c>spellId -> wire stat id</c> - RDB property field 847, see <c>Spell.ScalingAttribute</c>. Only the
		///     handful of spells that deviate from the caster's class attribute carry one; everything else takes the
		///     class rule in <c>CombatResolver.MagicDamageAttribute</c>. Empty for NPCs.
		/// </summary>
		public IReadOnlyDictionary<uint, uint> SpellScalingAttributes { get; set; } = new Dictionary<uint, uint>();

		/// <summary>
		///     This entity's attribute scores, in display units, as they were at world entry (class base + gear). The
		///     magic-damage scaling term reads them; the pools read them through <c>CharacterMaxStats</c>.
		/// </summary>
		public VitalAttributes Attributes { get; set; }

		/// <summary>
		///     The character's class, kept beside <see cref="Attributes" /> because both things that read the
		///     attributes need it: the magic-damage scaling attribute is chosen by class, and the pool curve is keyed
		///     on it. <c>ConanClass.Commoner</c> for NPCs, which takes the Wisdom branch and scores 0.
		/// </summary>
		public ConanClass CharacterClass { get; set; }

		/// <summary>
		///     This entity's Magic Damage for one spell, in display units: the flat worn-gear rating in
		///     <see cref="MagicDamage" /> plus the client's attribute-scaled term. Per-spell rather than cached
		///     because a spell may override which attribute it scales off (<see cref="SpellScalingAttributes" />).
		/// </summary>
		public uint MagicDamageFor(uint spellId)
		{
			return CombatResolver.ResolveMagicDamage(MagicDamage, CharacterClass, Attributes, SpellScalingAttributes.GetValueOrDefault(spellId));
		}

		// The last value BuffStatEffects computed for each stat it owns (gear + buffs, display units). Its purpose is
		// to answer "did this change?" so a sync pushes only what moved; the authoritative live values are the rating
		// fields themselves.
		public Dictionary<CharacterStatId, uint> BuffedStats { get; } = [];

		// This entity's score for one of the five attributes, in DISPLAY units: the class/level BASE plus the buffed value
		// once BuffStatEffects has synced (else gear alone, its baseline). Used by the attribute-driven formulas: regen
		// (RegenRates) and armour / protection mitigation (CombatResolver, which wants the ×10 wire scale - multiply
		// there).
		//
		// The base is decoded data rather than wire data: retail's snapshot stat sheet carries a flat 10 for every
		// attribute (#393) because the client computes the rest locally off its own class-feat templates, which the
		// class-attributes importer module reads out of the RDB (#444).
		public uint AttributeScore(CharacterStatId attribute)
		{
			var geared = BuffedStats.TryGetValue(attribute, out var buffed)
				? (int)buffed
				: GearBonuses.GetValueOrDefault(attribute);

			// ⚠️ The two sources are in DIFFERENT scales and the base has to be converted before it meets the bonus.
			// ClassAttributeBase.Amount is the RAW WIRE value (AssassinL23's Constitution base is 170) while
			// MaxStatAggregator has already divided gear by the stat's scale (their gear Constitution is 39) - and
			// their sheet reads 56, which is 170/10 + 39. Adding them raw returned 209, so every attribute-driven
			// formula (regen, armour and protection mitigation) read about ten times high for any character whose
			// class/level base is decoded.
			//
			// The base is empty for a (class, level) we have not decoded, in which case this is gear + buffs only and
			// reads LOW - the honest shortfall, not a silent zero base.
			return (uint)Math.Max(0, ClassBase.GetValueOrDefault(attribute) / (int)attribute.GetScale() + geared);
		}

		// The same score at the ×10 WIRE scale the client's stat store holds: `raw = display × 10 + 10`. The +10 is the
		// baseline the client seeds every attribute to before applying anything (observed on the ClientInterface channel:
		// `(804 10 old:0)` at world entry, then `(804 710 old:10)` for a display 70) - which is exactly why the client's
		// formulas read `(raw − 10) / 10`: that converts back to display units. Verified against the armour trace, whose
		// AttributeBonus 140 = (710 − 10) / 10 × 2.0 for NecroL80's display Strength of 70.
		public uint AttributeWireScore(CharacterStatId attribute)
		{
			return AttributeScore(attribute) * 10 + 10;
		}

		// UTC time this player last dealt or took damage. HP/mana regen is OUT-OF-COMBAT only in retail (the client's
		// OOCHealth/ManaRegeneration stats), so while within WorldSimulation.CombatRegenPause of this the HP+mana regen
		// is halted; stamina (the action resource) keeps regenerating. MinValue = never in combat (fresh player regens).
		public DateTimeOffset LastCombatAt { get; set; } = DateTimeOffset.MinValue;

		// Whether this entity has been in combat within the last `seconds`. On a PLAYER this gates regen; on an NPC it
		// is the combat MEMORY that keeps it engaged with whoever hit it and stops the anti-whittle full-heal from
		// erasing a fight it is still in (#695).
		public bool InCombatWithin(double seconds)
		{
			return (DateTimeOffset.UtcNow - LastCombatAt).TotalSeconds < seconds;
		}

		// Live vitals (NPCs are seeded at load; null for entities the combat/world layer doesn't track here yet).
		// The world is the authoritative owner - combat applies damage to this, and the tick respawns off it.
		public EntityVitals Vitals { get; set; } = null!;

		// For a PLAYER: the kill CHAIN (#446) - how many kills this player has strung together inside
		// ChainKillWindowSeconds of each other, and when the chain lapses. Both are session state; retail's
		// `ChainKillIIR` is a live announcement, not a persisted score (the DailyPvE/PvPKills counters are the
		// persisted ones and, measured over the corpus, they never move with it).
		public uint KillChain { get; set; }

		public DateTimeOffset KillChainExpiresAt { get; set; } = DateTimeOffset.MinValue;

		// How long a chain survives without another kill, MEASURED off the corpus (`--report chain-kill`): 32 captured
		// resets, median 8.97s, spread 7.90-9.22 - a 9-second timer with capture jitter. Escalations (2 -> 3) all
		// arrive inside it, at 1.36-7.83s.
		public const double ChainKillWindowSeconds = 9.0;

		// A chain is only ANNOUNCED from the second kill: retail sends counts 2 (x32) and 3 (x3) and never 1, so the
		// first kill starts a chain silently.
		public const uint ChainKillMinimumAnnounced = 2;

		// Records a kill at `now` and returns the count to announce, or null when there is nothing to say (the first
		// kill of a chain). Extends the window either way.
		public uint? RegisterKillInChain(DateTimeOffset now)
		{
			KillChain = now <= KillChainExpiresAt ? KillChain + 1 : 1;
			KillChainExpiresAt = now.AddSeconds(ChainKillWindowSeconds);
			return KillChain >= ChainKillMinimumAnnounced ? KillChain : null;
		}

		// True when an ANNOUNCED chain has just lapsed, and clears it - the caller then sends the closing count of 0
		// that retail pairs every non-zero with (32 of 32 captured non-zeros are followed by one).
		public bool TryLapseKillChain(DateTimeOffset now)
		{
			if (KillChain == 0 || now <= KillChainExpiresAt)
				return false;

			var announced = KillChain >= ChainKillMinimumAnnounced;
			KillChain = 0;
			KillChainExpiresAt = DateTimeOffset.MinValue;
			return announced;
		}

		// The owning connection for a player (so the broadcaster can send packets to it); null for NPCs.
		public INetworkClient Client { get; }

		// For an NPC: the quest ids that gate this NPC's visibility (null/empty = ungated, seen by everyone). A gated
		// NPC is streamed only to a viewer holding one of these quests active - e.g. Scavenger Tuut appears only while
		// you carry Casilda's kill quest. Data-driven from the quest goal definitions (kill-target NPCs that aren't
		// quest givers), so it generalises to any number of quests/NPCs. See IsVisibleTo.
		public IReadOnlyList<uint> GateQuestIds { get; set; } = null!;

		// Spells this CASTER NPC can cast at its target (#232), loaded from NpcSpell rows (by name) at zone load.
		// Empty for non-caster NPCs and players; IsCaster gates the sim's cast branch.
		public List<NpcSpellCast> Spells { get; } = [];

		// Item template ids carried by a loot bag, rolled when it drops; granted to the looter's backpack via
		// DispatchAddItem (0x2020) on TryLootAll and removed as they're taken, so each item is granted exactly once.
		public List<uint> LootItems { get; } = [];

		// The loot CONTAINER this creature drops on death (from its NpcTemplate.LootContainerTemplateId): a plain Bag
		// by default, a bigger chest for bosses/elites. Read by the kill-drop path (OnNpcKilled -> SpawnLootBagAt) to
		// pick the LootContainerTemplate variant. Stamped at spawn from the type; harmless (unused) on players.
		public LootContainerTemplateId LootContainer { get; set; } = LootContainerTemplateId.Bag;

		public Quaternion Rotation { get; set; }

		// #272: the FROM point of the segment the broadcaster actually sends (MoveFrom -> Position), captured when a move
		// effect is queued. Distinct from MoveOrigin (which re-anchors to Position at that moment to start the next
		// segment); the broadcaster reads MoveFrom so its segment isn't degenerate. Also the facing source for the StopIIR.
		public Vector3 MoveFrom { get; set; }

		// #272 smooth movement: MoveOrigin is the ACCUMULATION ANCHOR for the move-broadcast throttle - the start of the
		// segment currently being built up. The model steps Position every tick, but a MoveTask is only broadcast once
		// Position has travelled a retail-sized distance (MoveBroadcastSegment) from this anchor, at which point the anchor
		// re-advances to Position. So the client interpolates ~6u segments instead of a 1.2u-per-tick snap (the jitter).
		public Vector3 MoveOrigin { get; set; }

		public Vector3 Position { get; set; }

		// #400: a PLAYER's currently-drawn weapon set (Primary = equip slots 6/8, Secondary = 20/21). Toggled by the
		// SwitchWeaponsButton action and seeded from Character.ActiveWeaponSet on world entry; the swap is relayed to
		// viewers via the BCC behavior channel. Unused for NPCs (they carry a fixed loadout).
		public WeaponSet ActiveWeaponSet { get; set; }

		public bool Following => FollowTargetHandle != null;

		// GM toggles on a PLAYER entity (.godmode / .invisible). GodMode: the player takes no damage from NPC swings.
		// Invisible: hostile NPCs don't aggro the player (skipped in target selection). Default off; cleared on relog
		// since the entity is recreated at world entry.
		public bool GodMode { get; set; }

		// Whether this NPC is hostile to the player at all - the captured `cCategory` (only 1 = hostile). It drives
		// the NAME COLOUR and it gates fighting back; friendly/neutral NPCs (Citizens, vendors, wildlife) do neither.
		// Set at zone load. Players are never hostile here.
		//
		// ⚠️ This used to drive AGGRO as well, and that is #822: retail colours the Town Crier hostile - 62 unanimous
		// votes - and he never once attacks. See `Aggressive` below.
		public bool Hostile { get; set; }

		/// <summary>
		///     Whether this NPC INITIATES - engages a player who simply walks past. Distinct from
		///     <see cref="Hostile" />, which is the name colour.
		/// </summary>
		/// <remarks>
		///     ⭐ #822: one flag was doing both jobs, so every hostile-COLOURED NPC attacked. Retail colours civilians
		///     hostile without them ever fighting, the Town Crier being the reported case.
		///     <para />
		///     The rule is <see cref="NpcWeapon.Initiates" /> - an armed humanoid, or anything that is not humanoid -
		///     and its derivation and validation are documented there.
		///     <para />
		///     ⚠️ INITIATION only. Retaliation stays on <see cref="Hostile" />, so a passive NPC still fights back
		///     when attacked; this can make something unprovoked, never inert. Defaults TRUE so a mint site that does
		///     not think about it keeps the old behaviour rather than silently pacifying its NPC.
		/// </remarks>
		public bool Aggressive { get; set; } = true;

		// Whether this NPC SHOOTS rather than swings - its main hand holds a bow/crossbow/thrown weapon
		// (`NpcWeapon.IsRanged`). Stamped from the spawn's equipment at load, because that is where the rows are
		// already Included; combat then only needs the flag (#671). False for a player, an animal and anything
		// unarmed.
		public bool RangedAttacker { get; set; }

		// How far this creature attacks from, in world units - its main-hand weapon's `Attack Range` (#838,
		// NpcWeapon.AttackRange), defaulting to the melee constant. Stamped beside RangedAttacker, from the same
		// equipment rows, and read by the chase: a bow carrier stops here instead of walking into contact range.
		public float AttackRange { get; set; } = WorldState.NpcMeleeRange;

		// #623: the `combo_npc_generic_*` catalog id this NPC's melee swings carry, 0 when it has no melee weapon
		// (unarmed, an animal, an archer). Picked from the WEAPON (NpcWeapon.MeleeComboName) and stamped at load
		// beside RangedAttacker, for the same reason: that is where the equipment rows are already Included.
		// Whether a given swing actually carries it is a per-swing roll - see NpcWeapon.MeleeComboShare.
		public uint MeleeComboId { get; set; }

		// The creature's own ATTACK ABILITY, as a spell BASE id (`NpcSpells.SpellId >> 8`), 0 when it has none - and
		// most do not: 35 of 1,227 templates carry a row at all. It is what a health `StatDamageIIR` has to name, and
		// the reason the field exists: retail's physical hits on the player name exactly these (`Ripper Volley`,
		// `Rend`, `Butcher`), and a record naming anything the client's spell catalog lacks - 0, or a combo template -
		// is voided by it, taking the health with it. Not the same thing as MeleeComboId, which is a combo template
		// the client does NOT hold as a spell.
		public uint AttackSpellBaseId { get; set; }

		// #675: the CLASS-family rows this NPC may swing INSTEAD of its generic one, already level-gated at load.
		// Empty for every NPC whose archetype the weapon does not answer, which is most of them - see
		// `NpcWeapon.ClassComboFamily`. A swing that carries a combo at all then picks between this and MeleeComboId
		// at `NpcWeapon.ClassComboShare`.
		public uint[] ClassComboIds { get; set; } = [];

		// True while this NPC is engaged in combat (a target in aggro range, not leashing home). Drives the combat-mode
		// stat broadcast that makes the client DRAW the NPC's weapon - without it the NPC swings empty-handed (#218).
		// The tick flips this and emits an NpcEnterCombat/NpcExitCombat effect on the transition; reset on death/respawn.
		public bool InCombat { get; set; }

		// Directional combat (#20): this defender's blue shield rose - three guards spread over the left/mid/right
		// arcs, decimal-packed the way CombatShieldIIR carries it. A swing into a covered arc is mitigated by the
		// client's own PvE/PvP shield-bonus table. 0 = no rose (never engaged, or a non-combatant).
		//
		// The two sides own it differently (#620). An NPC's rose is OURS: rolled when it engages and shifted by one
		// shield every MinShiftSeconds..MaxShiftSeconds while it stays engaged. A PLAYER's rose is the PLAYER'S: the
		// client owns stat 0xA5, moves a guard on the shield keybinds and uploads the new spread as a c2s
		// CombatShieldIIR, which HandlePlayerShieldRose stores here. So the tick never rolls or shifts a player's.
		public uint ShieldDistribution { get; set; }

		public bool Invisible { get; set; }

		public bool IsCaster => Spells.Count > 0;

		// Dead = has vitals and they hit zero. A dead entity is excluded from combat and counts down to respawn.
		public bool IsDead => Vitals?.IsDead == true;

		// A transient loot bag (expires + despawns) rather than a player/NPC.
		public bool IsLoot { get; private init; }

		// True when this entity can be looted: a standalone loot bag, OR a DEAD NPC still carrying rolled loot (items
		// or cash). Loot is cleared when the NPC respawns (TickRespawn).
		public bool IsLootable => IsLoot || (IsDead && (LootItems.Count > 0 || CashTin > 0));

		// True while this player is riding a mount (#27). Pressing the SummonMount action toggles it: mount when false
		// (ride buff + mount StatUpd), dismount when true (end-twin + StatUpd 0xBA=0). Without this the toggle re-mounts
		// and the client gets stuck at the end of the dismount channel.
		public bool IsMounted { get; set; }

		// The SNEAK/HIDE stance (#505). A toggle: the same `PerformSpecialAction` selector both enters and leaves it,
		// so the server has to remember which way the next press goes - exactly like the mount toggle above.
		public bool IsHidden { get; set; }

		// When the `Resting` channel ends (#505). Rest is a 20.5 s self-cast during which the player regains vitals,
		// so it is a window rather than a flag - and the window's length is the spell row's own cast time, not a
		// number chosen here. Null = not resting.
		public DateTimeOffset? RestingUntil { get; set; }

		public bool IsResting => RestingUntil > DateTimeOffset.UtcNow;

		// The shadow coverage the CLIENT last reported for this entity (SetShadowCoverageIIR). Retail puts this number
		// in the hide-enter behaviour's tail, so it has to be remembered between the client's report and the press.
		// 100 in every observation, which is what the client sends when it has not reported anything else yet - not a
		// scale that has been established (docs/subsystems/player-actions.md).
		public uint ShadowCoverage { get; set; } = 100;

		// A per-entity monotonic counter for the behaviour records the server initiates on this entity's behalf: the
		// `CommandMessage` phase (f3) and the command id retail echoes in `CommandqueueClientId`. Retail's is
		// per-session and monotonic, and the client does not key anything off the exact value - what it must not do is
		// repeat. Starts at 3 because that is the first value both captured sessions used for a stance.
		public uint NextBehaviorPhase() => ++_behaviorPhase;

		// The command id retail echoes in `CommandqueueClientId`. A SEPARATE counter from the phase, because the two
		// disagree in the captures: one hide-enter carries phase 5 with command id 3, and the Herald's pair runs
		// phase 3/4 against command id 3/4. Only the stance records that carry the field advance it.
		public uint NextBehaviorCommandId() => ++_behaviorCommandId;

		private uint _behaviorCommandId = 2;

		private uint _behaviorPhase = 2;

		// The mount's RIDE STAT - the value the Mount stat carries on the wire, not the DB id - so a viewer arriving
		// after the mount can be told (#428). The player's SpawnBody is the FA015807 captured at their world ENTRY:
		// frozen, so it can never carry a mount taken later, and the client renders the horse from the Mount/MountSpeed
		// stats. A late viewer therefore gets that stat block right after the spawn record.
		public uint MountRideStat { get; set; }

		// Damage this rider has soaked since mounting (#142). Retail throws a rider off under fire, so the damage a
		// mounted player takes accumulates here and the mount comes off once it passes MountCatalog.ThrowOffDamage;
		// reset on every mount and every dismount. ⚠️ The CEILING is a placeholder (#653) - see MountCatalog - but the
		// meter itself is not: nothing else on the entity would let a hit reach the mount.
		public int MountDamageTaken { get; set; }

		public bool IsPlayer { get; }

		// #272: true while this entity is mid-move (chasing / returning home / following a player). Drives the segment
		// throttle (the anchor is seeded when a move starts) and the one-shot stop (so an arrival emits a single StopIIR).
		// Cleared on arrival, death and respawn.
		public bool Moving { get; set; }

		// #272: take the movement this entity has walked but NOT yet had broadcast, ending the move. Returns false when
		// there is nothing pending, so the caller sends no packet.
		//
		// ⚠️ It exists for DEATH, which is the one way a move ends without going through the tick's own `EndMove`.
		// The tick only emits a segment once `MoveOrigin -> Position` reaches `MoveBroadcastSegment` (6u), so a
		// creature killed MID-CHASE carries up to 6u the client was never told about - and the death POSE states the
		// true position, which makes the corpse jump that distance toward its killer right before the death animation.
		// Killing it in melee never showed it: the chase had already ended and flushed.
		//
		// Retail keeps the two in agreement instead: over the whole corpus, 250 of 267 death poses sit at distance
		// **0.00** from the end of that handle's preceding MoveTask (median 0.00). So the fix is to flush, not to move
		// the pose - the pose is right, the client was simply behind.
		public bool TakePendingMoveSegment()
		{
			if (!Moving || Vector3.DistanceSquared(MoveOrigin, Position) <= 1e-4f)
			{
				Moving = false;
				return false;
			}

			MoveFrom = MoveOrigin;
			MoveOrigin = Position;
			Moving = false;
			return true;
		}

		// When a TIMED summon should disappear, or null for anything that lives until killed/dismissed. The Frozen
		// Hatred fatality pet is the case it exists for (#140): it is granted for 60s, each further fatality pushes the
		// moment out by another 60s, and the total is capped 5 minutes ahead. Checked by the world tick (TimedDespawn),
		// so one field carries both the lifetime and its extension - there is no separate timer to keep in sync.
		public DateTimeOffset? DespawnAt { get; set; }

		// /falldamage on|off (#914). Retail's help states the default explicitly - "it is default on all players" - and
		// it is a PER-CHARACTER toggle there, not a server switch, so it lives on the entity and resets with the world
		// entry like any other live state.
		public bool FallDamageEnabled { get; set; } = true;

		// The FA015807 spawn BODY (the bytes after the RPC header) that renders this entity on a client. Constructed
		// FROM SCRATCH - never a verbatim capture: NPCs via NpcSpawnBuilder.TryBuildLive from the typed NpcSpawn columns
		// (the old captured NpcSpawn.Data blob is retired), pets via TryBuildFromType, loot via WorldObjectCodec, and
		// players via PlayerSpawnBuilder.BuildFromScratch (stashed by CaptureOwnSpawnBody at entry). This is a RUNTIME,
		// in-memory cache of that built body - never persisted, never touched by the importer - which the broadcaster
		// re-frames per recipient (current position + live playfield token) in SendWorldSpawn, so it isn't rebuilt for

		// The prepared FA015807 record for an NPC or a loot bag, built once at map load (NpcSpawnBuilder.TryBuildLive)
		// and re-framed per viewer. A RUNTIME field, never persisted.
		//
		// NOT used for players any more (#429): a player's record is BUILT per send from live state
		// (WorldBroadcaster.BuildPlayerSpawnBody), because a cached one describes them as they were at LOGIN - which is
		// how a viewer arriving after someone mounted saw them on foot, and would equally have missed an equipment or
		// level change.
		public byte[] SpawnBody { get; set; } = null!;

		// For a LOOT BAG dropped by a kill: the NAME of the NPC whose corpse it is (e.g. "Scavenger Tuut"). The bag is a
		// decoupled entity (#246) whose own Name is the generic "Bag of loot", so the source is recorded separately - it
		// is the match key for a LootFromNpc quest goal (#261), since the quest item itself is RDB content we don't
		// materialise. Null for a standalone bag (no source NPC).
		public string LootSourceName { get; set; } = null!;

		// The same corpse's NPC TEMPLATE id, carried beside the name so a LootFromNpc goal can match on the stable type
		// rather than a display string (#521) - `Saddur` is two different templates. 0 for a standalone bag, or one
		// dropped by something with no template behind it, which reads as "type unknown" and falls back to the name.
		public uint LootSourceTemplateId { get; set; }

		public string Name { get; }

		// #363 dev nameplate override: the FULL labelled overhead name ("Crocodile [Ape2 | id-prop x8]") to push via a
		// SetNameIIR right after this NPC's spawn, when the broadcaster's area-label debug toggle is on. The client resolves an NPC's floating
		// nameplate from its template_id LDB name and IGNORES the free spawn-body `Name` string, so the label can only
		// be shown by a SetNameIIR (0xADCE0CDA) - the same per-entity name-commit the create flow uses. Null = no label.
		public string OverheadNameOverride { get; set; } = null!;

		// For a LOOT BAG: the player handle allowed to loot it (the killer, or the spawning player). Sent as the
		// NewAllowedLooterIIR's looter at spawn (#263) - retail's loot-permission grant, so a drop belongs to whoever
		// earned it rather than everyone in range. 0 = unowned (anyone). Loot-permission ENFORCEMENT (gating the take
		// on this) is a later step (#101 shared-zone loot); today's Tortage is single-player-instanced so it's moot.
		public uint AllowedLooterHandle { get; set; }

		// Per-NPC swing counter, incremented each auto-attack to feed the attack-animation ack's per-actor counter
		// field (mirrors the small incrementing value the capture carries; not a persistence key).
		// The player's aggregated PHYSICAL armor rating from worn gear (#401), summed once at world entry and used to
		// mitigate incoming physical damage (CombatResolver.ApplyPhysicalMitigation). 0 for NPCs and unarmored players.
		public uint ArmorRating { get; set; }

		public uint AttackSwingCounter { get; set; }

		// Coins carried by a loot bag (tin, the wallet wire unit), rolled when the bag drops; zeroed once looted
		// (TryLootCash is granted exactly once per bag).
		public uint CashTin { get; set; }

		// The player's aggregated Combat Rating from worn gear (#399, generic 0x36B), added as a provisional flat
		// melee damage bonus (its real hit-rate role needs a hit/miss model). 0 for NPCs / no gear.
		public uint CombatRating { get; set; }

		public uint CriticalDamageRating { get; set; }

		// Aggregated crit stats from worn gear (#399): Critical Rating drives the melee crit CHANCE, Critical Damage
		// Rating the crit MAGNITUDE (CombatResolver). 0 for NPCs / no gear.
		public uint CriticalRating { get; set; }

		// The retail overhead-name disposition toward the player: 0 = friendly (green), 1 = hostile (red),
		// 2 = neutral (yellow). Sourced from NpcStatCore.Disposition (captured cCategory, #256/aggro) at zone load.
		// Drives BOTH the RequestAggroStatus name-colour reply AND Hostile (Hostile == Disposition == 1). NULL in the
		// DB -> DispositionFriendly here (matching the client's green default for an NPC it was never told about).
		public uint Disposition { get; set; } = DispositionFriendly;

		// The two template words the ALTERNATE-ADVANCEMENT pay-out multiplier reads (#150) - `NpcStatCore.NpcType`
		// and `NpcStatCore.NpcFlags`, stamped at spawn beside Disposition and FactionId. Null on a player, on a loot
		// bag and on any creature spawned from a pool rather than from a template row, which is the ordinary x1 case.
		public uint? NpcType { get; set; }
		public uint? NpcFlags { get; set; }

		// The wire handle other entities address this one by (the player's client instance / an NPC's spawn handle).
		public uint Handle { get; }

		// The player's aggregated Hit Rating from worn gear (#399). Hit Rating governs HIT chance (miss avoidance),
		// which isn't modelled yet, so it is currently unused. 0 for NPCs / no gear.
		public uint HitRating { get; set; }

		// The entity's level (NPCs: from their NpcSpawn row; drives the kill-XP award). 0 if unknown.
		public uint Level { get; set; }

		// The player's aggregated Magic Damage rating from worn gear (#399), summed at world entry; added flat to the
		// damage of spells they cast (CombatResolver.ResolveSpellDamage). 0 for NPCs and players with no such gear.
		public uint MagicDamage { get; set; }

		// The player's aggregated Natural Mana/Stamina Regen from worn gear (display units/sec), summed at world entry
		// and added onto the base regen tick (WorldSimulation). 0 for NPCs and players with no such gear.
		public uint NaturalManaRegen { get; set; }

		public uint NaturalStaminaRegen { get; set; }

		// The player's aggregated magical Protection rating from worn gear (#401); mitigates incoming MAGIC damage
		// (CombatResolver.ApplyMagicMitigation). 0 for NPCs and players with no Protection gear.
		public uint Protection { get; set; }

		// #262 companion-follow: the handle of the PLAYER this NPC follows (0 = not following). Set when the player
		// "releases" the NPC (e.g. Casilda after her rescue stage); the world tick steps the NPC toward that player and
		// broadcasts a MoveTask each step - the same server-driven incremental-step follow retail uses (decoded from the
		// Hoxxling gameplay capture: a stream of 0x96C46740 from->to ~6-7u steps tracking the player). Cleared when the
		// leader leaves the zone / logs out. The shared Moving flag tracks whether she stepped so a single Stop closes
		// the move when she catches up (avoids spamming a stop every idle tick).
		public uint? FollowTargetHandle { get; set; }

		// For a PLAYER: the handle of the NPC that last damaged them - used as the killer in the death's
		// SimpleCharKilledBy record. The resurrect window goes WAVE iff the killer resolves to the LOCAL player; a
		// real mob killer makes it INSTANT like retail (#214). Set by ApplyNpcAttack/ApplyNpcCast when a hit lands.
		public uint? LastAttackerHandle { get; set; }

		// If this entity is a PET, the handle of the owner (the summoner's client instance); null = not a pet (#307).
		// Set when the pet is bound via PetIIR owner-assign; QueuePetCommand (0x205b) validates the commander owns it.
		public uint? OwnerHandle { get; set; }
		// The three component shapes (#928 item 10): where an NPC lives and walks, what it shows, what a player knows.
		public NpcRoute Route { get; } = new();
		public NpcPresentation Presentation { get; } = new();
		public PlayerKnowledge Knowledge { get; } = new();


		public WorldEntity(uint handle, uint mapId, bool isPlayer, string name, INetworkClient client)
		{
			Handle = handle;
			MapId = mapId;
			IsPlayer = isPlayer;
			Name = name;
			Client = client;
			// #620: a PLAYER stands with the retail default guard - one shield per arc - from the moment they enter the
			// world, and moves it themselves from there (the client owns stat 0xA5 and uploads each change). An NPC
			// starts with no rose at all and rolls one when it engages.
			ShieldDistribution = isPlayer ? CombatShieldCodec.DefaultDistribution : 0;
		}

		// Resets the attack cooldown (e.g. when no target is in range), so re-aggro doesn't swing instantly.
		public void ArmAttackCooldown(double interval)
		{
			_attackCooldownRemaining = interval;
		}

		// Whether `viewer` (a player) may see this entity in interest streaming. Ungated entities are visible to all;
		// a gated NPC requires the viewer to hold one of its gating quests active.
		public bool IsVisibleTo(WorldEntity viewer)
		{
			// #262: hidden once the viewer has COMPLETED the gating quest (e.g. Casilda after her escort is done).
			if (HideWhenQuestCompletedId is { } hideQuest && viewer != null && viewer.Knowledge.CompletedQuestIds.Contains(hideQuest))
				return false;

			if (GateQuestIds == null || GateQuestIds.Count == 0)
				return true;
			if (viewer == null)
				return false;

			foreach (var questId in GateQuestIds)
			{
				if (viewer.Knowledge.ActiveQuestIds.Contains(questId))
					return true;
			}

			return false;
		}

		// Marks the entity dead (call once its vitals reach 0) and starts the corpse + respawn countdowns. The tick then
		// drops the loot bag beside the body, fades the body out at CorpseFadeSeconds and quits its handle; the respawn
		// timer revives it much later. Idempotent per death.
		public void MarkDead()
		{
			Route.ArmRespawn();
			_corpseAge = 0;
			_corpseStage = CorpseStage.None;
			InCombat = false; // combat ends on death; a respawn re-engages from a clean (sheathed) state
			ShieldDistribution = 0; // the shield rose belongs to the fight; the next engage rolls a fresh one (#20)
			Moving = false; // a mid-chase death stops moving (the corpse doesn't stream a trailing move, #272)
		}

		// Advances the attack cooldown by dt; returns true (and re-arms for `interval`) when a swing is ready. Used by
		// the AI tick so an aggroed NPC attacks at a fixed cadence regardless of the base tick rate.
		public bool TickAttackReady(double dt, double interval)
		{
			_attackCooldownRemaining -= dt;
			if (_attackCooldownRemaining > 0)
				return false;

			_attackCooldownRemaining = interval;
			return true;
		}

		// Rolls this defender's opening shield rose and arms the first shift (#20). Called on the engage transition, so
		// each fight opens on its own distribution the way retail's do.
		public void OpenShieldRose(Random random)
		{
			ShieldDistribution = CombatShieldRose.Roll(random);
			_shieldShiftRemaining = CombatShieldRose.NextShiftSeconds(random);
		}

		// Advances the shield-shift timer by dt; on elapse moves one shield to another arc, re-arms, and returns true so
		// the caller broadcasts the new rose. False while the rose is unrolled (never engaged) or the timer is running.
		public bool TickShieldShift(double dt, Random random)
		{
			if (ShieldDistribution == 0)
				return false;

			_shieldShiftRemaining -= dt;
			if (_shieldShiftRemaining > 0)
				return false;

			ShieldDistribution = CombatShieldRose.Shift(ShieldDistribution, random);
			_shieldShiftRemaining = CombatShieldRose.NextShiftSeconds(random);
			return true;
		}

		// Drives the corpse timeline (drop -> fade -> quit, see CorpseFadeSeconds) and returns the ONE stage that came
		// due on this tick, or null. Each stage fires exactly once per death and only while dead, so a revive mid-way
		// (TickRespawn / a resurrect) simply stops the sequence where it stands.
		//
		// One stage per tick even if a long dt crossed two: the next tick fires the follower. At 10 Hz against a 2.5 s
		// gap that cannot happen in practice, and letting a stalled tick emit a fade and a quit in the same breath
		// would put both on the wire with no dissolve in between.
		public CorpseStage? TickCorpse(double dt)
		{
			if (!IsDead || _corpseStage >= CorpseStage.Quit)
				return null;

			_corpseAge += dt;

			// The bag is dropped on the FIRST tick after death rather than waiting out any delay - retail spawns it in
			// the death's own packet burst, and a 10 Hz tick is the closest this side can get to that.
			if (_corpseStage < CorpseStage.Drop)
				return _corpseStage = CorpseStage.Drop;

			if (_corpseStage < CorpseStage.Fade && _corpseAge >= CorpseFadeSeconds)
				return _corpseStage = CorpseStage.Fade;

			if (_corpseStage == CorpseStage.Fade && _corpseAge >= CorpseFadeSeconds + CorpseQuitDelaySeconds)
				return _corpseStage = CorpseStage.Quit;

			return null;
		}

		// True once this death's corpse has been quit (the handle is no longer rendered for anyone). A NoRespawn
		// entity is removed from the world here rather than at its respawn timer - it has no respawn to wait for.
		public bool CorpseCleared => _corpseStage >= CorpseStage.Quit;

		// Counts down a loot bag's lifetime by dt; returns true the tick it expires (caller despawns + removes it).
		// No-op (false) for anything that isn't a loot bag.
		public bool TickExpiry(double dt)
		{
			if (!IsLoot)
				return false;

			_expiryRemaining -= dt;
			return _expiryRemaining <= 0;
		}

		// (A NoRespawn NPC used to run its respawn timer down as a DESPAWN timer here. It leaves the world at the end of
		// its corpse sequence instead - `CorpseStage.Quit` -> `NpcCorpseRemoved` - so a dead summon's exit is tied to its
		// own corpse rather than to a respawn delay it never uses and which has since grown tenfold.)

		// Advances the respawn countdown for a DEAD entity by dt; when it elapses, revives the entity (vitals back to
		// full, transform back to its spawn point) and returns true so the caller can re-spawn it for nearby players.
		// No-op (false) for a living entity or one with no vitals.
		public bool TickRespawn(double dt)
		{
			if (!IsDead)
				return false;

			if (!Route.TickRespawnDue(dt))
				return false;

			Vitals.Health = Vitals.MaxHealth;
			Position = Route.SpawnPosition;
			Rotation = Route.SpawnRotation;
			// Reset the move anchor too (#246): a chase/return leaves MoveOrigin at wherever the NPC last stepped
			// (the spot it died), so without this the first post-respawn MoveTask streams origin=death -> spawn and
			// the client renders the revived NPC back at its death location before walking home. Anchor it at spawn.
			MoveOrigin = Route.SpawnPosition;
			MoveFrom = Route.SpawnPosition;
			Moving = false; // a fresh NPC starts stationary (#272); the next chase/return re-seeds the move anchor
			// Drop any unlooted corpse-bag contents - the entity is a live NPC again, not a bag.
			CashTin = 0;
			LootItems.Clear();
			_corpseAge = 0;
			_corpseStage = CorpseStage.None;
			InCombat = false; // the fresh NPC starts idle (sheathed); re-aggro re-draws via the combat-mode broadcast
			ShieldDistribution = 0; // ...and unrolls its shield rose until it engages again (#20)
			Route.Returning = false;
			return true;
		}

		// A transient loot bag dropped on a kill (a world object, not a player/NPC): no vitals, no respawn; it sits at
		// its drop spot until its expiry elapses, then the tick despawns + removes it. `spawnBody` is its prebuilt
		// FA-family world-object body (re-framed per recipient like any spawn).
		public static WorldEntity ForLoot(uint handle, uint mapId, Vector3 position, Quaternion rotation, string name, byte[] spawnBody, double expirySeconds)
		{
			return new WorldEntity(handle, mapId, false, name, null!)
			{
				Position = position,
				Rotation = rotation,
				SpawnBody = spawnBody,
				IsLoot = true,
				_expiryRemaining = expirySeconds
			};
		}

		// Convenience factory for an NPC (no owning client) placed at a fixed transform. The transform is also recorded
		// as the spawn point the NPC returns to on respawn. Optional vitals make it killable + respawnable by the tick.
		public static WorldEntity ForNpc(uint handle, uint mapId, Vector3 position, Quaternion rotation, string name, EntityVitals? vitals = null, uint templateId = 0)
		{
			return new WorldEntity(handle, mapId, false, name, null!)
			{
				Position = position,
				Rotation = rotation,
				Route = { SpawnPosition = position, SpawnRotation = rotation },
				Vitals = vitals!,
				NpcTemplateId = templateId
			};
		}
	}

	// A spell a caster NPC casts at its target (#232): the full wire spell id + the captured ApplySpell effect
	// payload (interim verbatim). Sourced from the NpcSpell table by NPC name.
	public readonly record struct NpcSpellCast(uint SpellId, MagicBuffData Payload);

	// A pending ReachArea quest goal (#262), solved when the player enters it on map `MapId`. Two shapes, and
	// `Region` decides which:
	//
	//   - `Region` set (#521): the CLIENT'S OWN trigger volume, resolved from the goal's typed LDB reference
	//     (`TrailingCategory` 51200 -> region name -> that playfield's RDB 1000053 record). Containment is the
	//     polygon prism, exactly as for a hazard.
	//   - `Region` null: the fallback - within `RadiusSq` (horizontal) of the goal's map marker (X, Z).
	//
	// ⚠️ The marker is a map PIN, not the trigger, so the fallback is an approximation and the region is not. Goal
	// 1448's marker (416, 701) sits inside `Trappers Haven`, a 96 x 82 region, while the fallback tests a 40-unit
	// circle centred on the pin - overlapping, but neither contains the other. Only 3 goals carry a region today.
	// QuestId is kept so a solved goal names its quest in logs.
	public readonly record struct ReachGoalTarget(uint QuestId, uint GoalId, uint MapId, float X, float Z, float RadiusSq, MapRegion? Region = null);
}
