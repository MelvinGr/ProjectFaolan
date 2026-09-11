using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     One live entity in the world simulation - a player, an NPC or a loot bag - identified by its session-scoped
	///     wire handle. This is the authoritative, in-memory state the server owns: transform, map and instance, vitals,
	///     buffs, combat state and the per-entity timers the world tick drives.
	/// </summary>
	public sealed class WorldEntity
	{
		// The corpse is not the loot bag, and it does not pop after the death animation. Measured over the whole retail
		// corpus (`--report corpse-timing`, 275 NPC deaths across 134 captures): the bag is a separate dynel spawned in
		// the same packet burst as the death, and the body then lies beside it for another half-minute:
		//
		//   death -> loot bag spawns (delay 0.00 s median over 100 matched kills, its own handle, 0.8 u away)
		//         -> n3ToClientFade on the corpse handle at 30.72 s (median, n=229)
		//         -> n3ToClientQuit on the corpse handle at 33.18 s (median, n=228)
		//
		// So the drop is immediate (TickCorpse -> Drop) and this and CorpseQuitDelaySeconds govern only the body.
		public const double CorpseFadeSeconds = 30.7;

		// The gap between the corpse's fade and its quit (33.18 - 30.72). The fade itself plays a 2.0 s dissolve - see
		// CorpseFadeAnimationSeconds - so the handle is dropped just after the client has finished dissolving it.
		//
		// Measuring this needs the fade as the anchor, not "the first quit after the death": a dead entity is still
		// interest-managed - walk away from a body and it quits, walk back and it re-spawns - so the first quit is
		// often that one, tens of seconds early (median 33.18 s but minimum 6.96 s read that way).
		public const double CorpseQuitDelaySeconds = 2.5;

		// The `fadeSeconds` float the corpse's n3ToClientFade carries. Invariant by entity class across the corpus: a
		// Character (0xC350) fades in 2.0 s in all 229 captured corpse fades, a loot bag (0xC87A) in 3.0 s - which is
		// why this is here and not left on N3ToClientFadeCodec.DefaultFadeSeconds (that default is the bag's 3.0).
		public const float CorpseFadeAnimationSeconds = 2.0f;

		public const uint DispositionFriendly = 0;

		public const uint DispositionHostile = 1;

		public const uint DispositionNeutral = 2;

		// The monotonic per-entity counter carried in field 3 of the continuous idle heartbeat (short-form
		// BehaviorControlCenterIIR). Incremented once per emitted pulse so the client sees a steadily rising tick, as
		// retail's stream does.
		public uint HeartbeatTick;

		// How far this death's corpse timeline has already been driven, so each stage fires exactly once (see TickCorpse).
		private CorpseStage _corpseStage;

		// Time (seconds) until this NPC may swing again; counts down while it has a target in range.
		private double _attackCooldownRemaining;

		// Seconds since this entity died, advanced by TickCorpse only. Drives the drop -> fade -> quit sequence above.
		private double _corpseAge;

		// Time (seconds) until this defender moves a shield to another arc; counts down while engaged.
		private double _shieldShiftRemaining;

		// Remaining time (seconds) until a loot bag expires.
		private double _expiryRemaining;

		// The MapArea.AreaNameId the player is currently standing in, or 0 for none. Live world state, never
		// persisted: it exists so the tick can tell a crossing (banner) from merely still being inside the area.
		public uint CurrentAreaNameId { get; set; }

		// Fall tracking. The client reports being off the ground in every move record: `flag1` clears
		// NoAccelerationVector while airborne, because it starts sending an acceleration vector the instant the
		// character leaves the ground (see MoveFlag). So the server needs no physics of its own - it watches that bit
		// and measures the drop between the airborne run's highest point and where the run ended.
		//
		// The apex, not the takeoff height: a jump rises before it falls, and the fall that hurts is measured from the
		// top of the arc. In chat-annotated-3's 19:43:30 fall the player jumps from y=324.6 up to 327.0 and lands at
		// 315.1 - 11.9 units of fall, not the 9.5 the takeoff height would give.
		public bool Airborne { get; set; }

		// The highest Y reached during the current airborne run; meaningless while grounded.
		public float AirborneApexY { get; set; }

		// The fastest downward speed the client itself reported during the current airborne run, in world units per
		// second (a positive number), or 0 if it reported none. This is what fall damage is resolved from - the
		// client's own number, sampled at its own rate, where the apex-minus-landing drop above is reconstructed from
		// position samples ~200 ms apart and loses a fraction of a unit at each end. Over the corpus's 34 falls v^2
		// explains the damage at r = 0.96 against the drop's 0.63.
		//
		// The maximum over the run rather than the last sample: a fall accelerates, so the fastest report is the one
		// nearest the ground, and taking the maximum survives a stray record arriving after the landing.
		public float AirborneFallSpeed { get; set; }

		// The Y of the last move that was still on the ground. This, not the previous position, is what seeds a fall's
		// apex: a fall off a ledge has no rising samples at all, so its highest point is the ground it left. Tracked
		// here rather than read from Position because the move handler commits the new position before the fall check
		// runs, at which point "the previous position" is no longer available to compare against.
		public float LastGroundedY { get; set; }

		// The MapHazard.RegionId the player is currently standing in, or null for none. Same crossing-vs-inside role
		// as CurrentAreaNameId above: a hazard fires on entry (the client's own feedback token names the trigger
		// `playerenter(3)`), so re-firing it every tick while the player burns would be wrong.
		public uint? CurrentHazardId { get; set; }

		// The creature's death-animation behaviorId (the death BccIIR field-1), from its NpcTemplate. Defaults to the
		// humanoid biped death (DeathSequence.HumanoidDeathBehaviorId); overridden per-type where harvested (demon /
		// quadruped). Read by CombatService.OnNpcKilled when it sends the death BccIIR.
		public uint DeathBehaviorId { get; set; } = DeathSequence.HumanoidDeathBehaviorId;

		// How long that animation runs for this creature, in centiseconds - from its NpcTemplate. Null for a creature
		// no capture measured; `DeathSequence` then uses the animation's own modal length.
		public uint? DeathActivationTime { get; set; }

		// For an NPC: a quest that, once the viewer has completed it, hides this NPC (not spawned / despawned). The
		// complement of GateQuestIds ("show while active") - e.g. Casilda vanishes from the beach once her escort
		// quest is completed. Data-driven from the Escort goal (escortee template -> escort quest id). null = never hidden.
		public uint? HideWhenQuestCompletedId { get; set; }

		// The spawn record to send once this escortee is freed - its own body with the story pose swapped for the
		// creature's ordinary idle, and in walk mode. Null for everything that is not a posed escortee.
		//
		// Two bodies rather than a patch because the pose is a protobuf varint inside the CoreCharacter message, and
		// `SpawnTransformPatch` only stamps fixed-width fields into a built record. Chosen by `Following`, which is
		// what "freed" physically means - she is either chained to her post or walking behind someone - so the choice
		// costs no per-stream-in quest lookup.
		public byte[]? FreedSpawnBody { get; set; }

		// Whether this NPC is the escortee of a quest - the same Escort goal that fills HideWhenQuestCompletedId is what
		// says so, so the flag is data-driven and needs no per-NPC list.
		//
		// An escortee is never idle in the ambient sense. Before its rescue it is held in a scripted pose - Casilda's
		// captive pose is a BehaviorControlCenterIIR, and an idle emote played over it simply replaces the pose. After
		// the rescue it is walking behind the player, and the standing gate already suppresses idles there.
		//
		// This is not the same thing as "has no ambient animations". Retail does animate her - reacting when an enemy
		// attacks the player and when that enemy dies - but those are reactions to combat events, a different system
		// from the self-timed idle rotation. The harvest cannot tell the two apart and files hers as idles (three rows
		// on template 9411); suppressing the rotation is correct either way, because none of those three is an idle.
		public bool IsQuestEscortee => HideWhenQuestCompletedId != null;

		// Which instance (copy) of MapId this entity lives in. Equals its Zone's PlayfieldToken - the retail
		// playfield-instance id. 0 until placed; WorldState.Place sets it from the resolved/created instance so every
		// later ZoneFor(entity) lands in the same copy. Two entities only see each other if MapId and InstanceId match.
		public uint InstanceId { get; set; }

		// Live transform + map. Mutable: updated by Move (players) and the AI tick (NPCs).
		public uint MapId { get; set; }

		// The NPC's type id (NpcTemplateId), or 0 for players/loot. The stable per-type key used to look up shops and
		// conversations without the shared-placeholder-name collision (many NPCs share an internal name, e.g. 25 share
		// "Halen van Dummy").
		public uint NpcTemplateId { get; init; }

		// The Khitai faction this creature belongs to (`NpcStatCore.FactionId`), or null - which is every base-game
		// NPC, since the faction ids are the expansion's. Read by the kill path to award reputation.
		public uint? FactionId { get; set; }

		// Ambient spawner: the NpcSpawnArea this NPC is a member of (its spawn-group pool), or null for a
		// capture-placed / named / summoned NPC. When set, the area pool maintains this creature's population (respawns
		// a replacement at a free area point when it dies) instead of the per-entity respawn - and its wander waypoints
		// are the area's points, so it roams the group's area.
		public uint? SpawnAreaId { get; set; }

		// Active buffs/effects on this entity, keyed by buff (spell) id - reapplying refreshes the entry. On a
		// server-initiated cancel the server pushes the buff-removal IIR (the ApplySpell rank-1 "end" twin with an
		// empty payload) so the client clears the icon live. On the client-initiated CancelBuff (0x201E right-click)
		// retail sends only a FormatFeedback text - the client removes its own icon - and a timed buff is expired
		// client-side from the ApplySpell duration. Entries with a duration expire on the tick, the rest live until
		// cancel/disconnect.
		public ConcurrentDictionary<uint, ActiveBuff> ActiveBuffs { get; } = new ConcurrentDictionary<uint, ActiveBuff>();

		/// <summary>
		///     Removes every buff that has expired at <paramref name="now" /> and returns them, so the caller can send
		///     each one's end-twin.
		/// </summary>
		/// <remarks>
		///     The <c>ToArray()</c> snapshot is load-bearing. A <see cref="ConcurrentDictionary{TKey,TValue}" />
		///     enumerator walks the live buckets, so removing the node it is standing on can skip the next node in the
		///     same bucket - with several buffs expiring together some would be left behind, and the client never
		///     self-expires an icon. Deciding and removing up front also means the caller's awaited sends can no longer
		///     invalidate the iteration, and <c>TryRemove</c>'s return value keeps the removal the single winner: a buff
		///     cancelled concurrently by the player is not reported here as well.
		/// </remarks>
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

		// The class/level base this entity's stats start from, by wire stat id, in the wire scale - the five
		// attributes, MaxHealth, and the casters' Non Combat Mana Regen base. Loaded once at world entry from
		// ClassAttributeBases, which covers all 12 classes at all 80 levels. Empty means "no row for this (class,
		// level)" - unknown, not "no base" - and a character there regenerates and mitigates below retail.
		public IReadOnlyDictionary<CharacterStatId, int> ClassBase { get; set; } = new Dictionary<CharacterStatId, int>();

		// The gear-derived stat bonuses this entity entered the world with, in display units by wire stat id
		// (MaxStatAggregator over its equipped items). Kept, rather than only folded into the rating fields below, so
		// the buff engine can recompute `gear + active buffs` from a fixed baseline instead of adding and subtracting
		// deltas as buffs come and go (BuffStatEffects.Sync). Empty for NPCs and for a player with no stat gear.
		public IReadOnlyDictionary<CharacterStatId, int> GearBonuses { get; set; } = new Dictionary<CharacterStatId, int>();

		/// <summary>
		///     This entity's out-of-combat mana regen base, in display units per second - the flat term
		///     <c>RegenRates.ManaPerSecond</c> adds on top of the Int+Wis sum. 0 for a class that has none, which is
		///     every rogue and soldier.
		/// </summary>
		/// <remarks>
		///     Unlike health's and stamina's, mana's flat term is class data rather than a universal seed, and it
		///     comes from two client sources that add: <see cref="ClassBase" />'s stat 280 and the auto-trained
		///     <c>Mage</c> / <c>Priest</c> feat. Both are raw wire (the stat's display scale is 100), which is why
		///     this divides and the attribute path does not - and no item in the catalog carries 280, so the bonus
		///     dictionary's entry can only have come from that feat.
		///     <para>
		///         It reproduces all three captured observations: 2200 for a level-80 Necromancer or Demonologist
		///         (a 200 class base plus the 2000 feat), 2000 for a level-24 Herald of Xotli (whose class templates
		///         carry no 280 row at all) and nothing for a level-23 Assassin, whose class trains neither feat.
		///     </para>
		/// </remarks>
		public double NonCombatManaRegenBase =>
			(ClassBase.GetValueOrDefault(CharacterStatId.NonCombatManaRegen)
			 + GearBonuses.GetValueOrDefault(CharacterStatId.NonCombatManaRegen))
			/ (double)CharacterStatId.NonCombatManaRegen.GetScale();

		/// <summary>
		///     The per-spell boosts this entity's trained feats grant, as <c>spellId -> percent x 100</c> - a
		///     Necromancer with `Decomposition` rank 5 carries +3500 on every rank of Flesh to Worms. The feat names a
		///     spell line; the fan-out to its ranks is resolved once at world entry (beside the gear bonuses) so the
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
		///     What this entity's trained feats do to their combos, as <c>comboId -> bonus</c> - the opening
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
		///     The procs this player's trained feats hang off its combos and spell lines, resolved once at world
		///     entry like <see cref="ComboBonuses" />. ProcService reads them on a combo landing and a cast completing.
		///     Empty for NPCs and for a character with no proc feats.
		/// </summary>
		public IReadOnlyList<FeatProc> Procs { get; set; } = [];

		/// <summary>
		///     The per-spell scaling-attribute overrides among this entity's known spells, as
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

		// This entity's score for one of the five attributes, in display units: the class/level base plus the buffed value
		// once BuffStatEffects has synced (else gear alone, its baseline). Used by the attribute-driven formulas: regen
		// (RegenRates) and armour / protection mitigation (CombatResolver, which wants the x10 wire scale - multiply
		// there).
		//
		// The base is decoded data rather than wire data: retail's snapshot stat sheet carries a flat 10 for every
		// attribute because the client computes the rest locally off its own class-feat templates, which the
		// class-attributes importer module reads out of the RDB.
		public uint AttributeScore(CharacterStatId attribute)
		{
			var geared = BuffedStats.TryGetValue(attribute, out var buffed)
				? (int)buffed
				: GearBonuses.GetValueOrDefault(attribute);

			// The two sources are in different scales and the base has to be converted before it meets the bonus.
			// ClassAttributeBase.Amount is the raw wire value (Assas23's Constitution base is 170) while
			// MaxStatAggregator has already divided gear by the stat's scale (their gear Constitution is 39) - and
			// their sheet reads 56, which is 170/10 + 39. Added raw, every attribute-driven formula (regen, armour and
			// protection mitigation) would read about ten times high.
			//
			// The base is empty for a (class, level) that is not decoded, in which case this is gear + buffs only and
			// reads low - the honest shortfall, not a silent zero base.
			return (uint)Math.Max(0, ClassBase.GetValueOrDefault(attribute) / (int)attribute.GetScale() + geared);
		}

		// The same score at the x10 wire scale the client's stat store holds: `raw = display x 10 + 10`. The +10 is the
		// baseline the client seeds every attribute to before applying anything (observed on the ClientInterface channel:
		// `(804 10 old:0)` at world entry, then `(804 710 old:10)` for a display 70) - which is exactly why the client's
		// formulas read `(raw - 10) / 10`: that converts back to display units. Verified against the armour trace, whose
		// AttributeBonus 140 = (710 - 10) / 10 x 2.0 for Necr80's display Strength of 70.
		public uint AttributeWireScore(CharacterStatId attribute)
		{
			return AttributeScore(attribute) * 10 + 10;
		}

		// UTC time this player last dealt or took damage. HP/mana regen is out-of-combat only in retail (the client's
		// OOCHealth/ManaRegeneration stats), so while within WorldSimulation.CombatRegenPause of this the HP+mana regen
		// is halted; stamina (the action resource) keeps regenerating. MinValue = never in combat (fresh player regens).
		public DateTimeOffset LastCombatAt { get; set; } = DateTimeOffset.MinValue;

		// Whether this entity has been in combat within the last `seconds`. On a player this gates regen; on an NPC it
		// is the combat memory that keeps it engaged with whoever hit it and stops the anti-whittle full-heal from
		// erasing a fight it is still in.
		public bool InCombatWithin(double seconds)
		{
			return (DateTimeOffset.UtcNow - LastCombatAt).TotalSeconds < seconds;
		}

		// Live vitals (NPCs are seeded at load; null for entities the combat/world layer does not track, such as loot).
		// The world is the authoritative owner - combat applies damage to this, and the tick respawns off it.
		public EntityVitals Vitals { get; set; } = null!;

		// For a player: the kill chain - how many kills this player has strung together inside ChainKillWindowSeconds
		// of each other, and when the chain lapses. Both are session state; retail's `ChainKillIIR` is a live
		// announcement, not a persisted score (the DailyPvE/PvPKills counters are the persisted ones and, measured
		// over the corpus, they never move with it).
		public uint KillChain { get; set; }

		public DateTimeOffset KillChainExpiresAt { get; set; } = DateTimeOffset.MinValue;

		// How long a chain survives without another kill, measured off the corpus (`--report chain-kill`): 32 captured
		// resets, median 8.97s, spread 7.90-9.22 - a 9-second timer with capture jitter. Escalations (2 -> 3) all
		// arrive inside it, at 1.36-7.83s.
		public const double ChainKillWindowSeconds = 9.0;

		// A chain is only announced from the second kill: retail sends counts 2 (x32) and 3 (x3) and never 1, so the
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

		// True when an announced chain has just lapsed, and clears it - the caller then sends the closing count of 0
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
		// you carry Casilda's kill quest. Data-driven from the quest goal definitions (kill-target NPCs that are not
		// quest givers), so it generalises to any number of quests/NPCs. See IsVisibleTo.
		public IReadOnlyList<uint> GateQuestIds { get; set; } = null!;

		// Spells this caster NPC can cast at its target, loaded from NpcSpell rows (by name) at zone load. Empty for
		// non-caster NPCs and players; IsCaster gates the sim's cast branch.
		public List<NpcSpellCast> Spells { get; } = [];

		// Item template ids carried by a loot bag, rolled when it drops; granted to the looter's backpack via
		// DispatchAddItem on TryLootAll and removed as they are taken, so each item is granted exactly once.
		public List<uint> LootItems { get; } = [];

		// The loot container this creature drops on death (from its NpcTemplate.LootContainerTemplateId): a plain Bag
		// by default, a bigger chest for bosses/elites. Read by the kill-drop path (OnNpcKilled -> SpawnLootBagAt) to
		// pick the LootContainerTemplate variant. Stamped at spawn from the type; unused on players.
		public LootContainerTemplateId LootContainer { get; set; } = LootContainerTemplateId.Bag;

		public Quaternion Rotation { get; set; }

		// The from point of the segment the broadcaster actually sends (MoveFrom -> Position), captured when a move
		// effect is queued. Distinct from MoveOrigin (which re-anchors to Position at that moment to start the next
		// segment); the broadcaster reads MoveFrom so its segment is not degenerate. Also the facing source for the
		// StopIIR.
		public Vector3 MoveFrom { get; set; }

		// The accumulation anchor for the move-broadcast throttle - the start of the segment currently being built up.
		// The model steps Position every tick, but a MoveTask is only broadcast once Position has travelled a
		// retail-sized distance (MoveBroadcastSegment) from this anchor, at which point the anchor re-advances to
		// Position. So the client interpolates ~6u segments instead of a per-tick snap.
		public Vector3 MoveOrigin { get; set; }

		public Vector3 Position { get; set; }

		// A player's currently-drawn weapon set (Primary = equip slots 6/8, Secondary = 20/21). Toggled by the
		// SwitchWeaponsButton action and seeded from Character.ActiveWeaponSet on world entry; the swap is relayed to
		// viewers via the BCC behavior channel. Unused for NPCs (they carry a fixed loadout).
		public WeaponSet ActiveWeaponSet { get; set; }

		public bool Following => FollowTargetHandle != null;

		// GM toggles on a player entity (.godmode / .invisible). GodMode: the player takes no damage from NPC swings.
		// Invisible: hostile NPCs do not aggro the player (skipped in target selection). Default off; cleared on relog
		// since the entity is recreated at world entry.
		public bool GodMode { get; set; }

		// Whether this NPC is hostile to the player at all - the captured `cCategory` (only 1 = hostile). It drives
		// the name colour and it gates fighting back; friendly/neutral NPCs (Citizens, vendors, wildlife) do neither.
		// Set at zone load. Players are never hostile here. It does not drive aggro: retail colours the Town Crier
		// hostile (62 unanimous votes) and he never attacks - see `Aggressive`.
		public bool Hostile { get; set; }

		/// <summary>
		///     Whether this NPC initiates - engages a player who simply walks past. Distinct from
		///     <see cref="Hostile" />, which is the name colour: retail colours civilians hostile without them ever
		///     fighting, the Town Crier being the clearest case.
		/// </summary>
		/// <remarks>
		///     The rule is <see cref="NpcWeapon.Initiates" /> - an armed humanoid, or anything that is not humanoid -
		///     and its derivation and validation are documented there.
		///     <para />
		///     Initiation only. Retaliation stays on <see cref="Hostile" />, so a passive NPC still fights back when
		///     attacked; this can make something unprovoked, never inert. Defaults true so a mint site that does not
		///     think about it keeps an aggressive NPC rather than silently pacifying it.
		/// </remarks>
		public bool Aggressive { get; set; } = true;

		// Whether this NPC shoots rather than swings - its main hand holds a bow/crossbow/thrown weapon
		// (`NpcWeapon.IsRanged`). Stamped from the spawn's equipment at load, because that is where the rows are
		// already Included; combat then only needs the flag. False for a player, an animal and anything unarmed.
		public bool RangedAttacker { get; set; }

		// How far this creature attacks from, in world units - its main-hand weapon's `Attack Range`
		// (NpcWeapon.AttackRange), defaulting to the melee constant. Stamped beside RangedAttacker, from the same
		// equipment rows, and read by the chase: a bow carrier stops here instead of walking into contact range.
		public float AttackRange { get; set; } = WorldState.NpcMeleeRange;

		// The `combo_npc_generic_*` catalog id this NPC's melee swings carry, 0 when it has no melee weapon (unarmed,
		// an animal, an archer). Picked from the weapon (NpcWeapon.MeleeComboName) and stamped at load beside
		// RangedAttacker, for the same reason: that is where the equipment rows are already Included. Whether a given
		// swing actually carries it is a per-swing roll - see NpcWeapon.MeleeComboShare.
		public uint MeleeComboId { get; set; }

		// The creature's own attack ability, as a spell base id (`NpcSpells.SpellId >> 8`), 0 when it has none - and
		// most do not: 35 of 1,227 templates carry a row at all. It is what a health `StatDamageIIR` has to name, and
		// the reason the field exists: retail's physical hits on the player name exactly these (`Ripper Volley`,
		// `Rend`, `Butcher`), and a record naming anything the client's spell catalog lacks - 0, or a combo template -
		// is voided by it, taking the health with it. Not the same thing as MeleeComboId, which is a combo template
		// the client does not hold as a spell.
		public uint AttackSpellBaseId { get; set; }

		// The class-family rows this NPC may swing instead of its generic one, already level-gated at load. Empty for
		// every NPC whose archetype the weapon does not answer, which is most of them - see
		// `NpcWeapon.ClassComboFamily`. A swing that carries a combo at all then picks between this and MeleeComboId
		// at `NpcWeapon.ClassComboShare`.
		public uint[] ClassComboIds { get; set; } = [];

		// True while this NPC is engaged in combat (a target in aggro range, not leashing home). Drives the combat-mode
		// stat broadcast that makes the client draw the NPC's weapon - without it the NPC swings empty-handed. The
		// tick flips this and emits an NpcEnterCombat/NpcExitCombat effect on the transition; reset on death/respawn.
		public bool InCombat { get; set; }

		// Directional combat: this defender's blue shield rose - three guards spread over the left/mid/right arcs,
		// decimal-packed the way CombatShieldIIR carries it. A swing into a covered arc is mitigated by the client's
		// own PvE/PvP shield-bonus table. 0 = no rose (never engaged, or a non-combatant).
		//
		// The two sides own it differently. An NPC's rose is the server's: rolled when it engages and shifted by one
		// shield every MinShiftSeconds..MaxShiftSeconds while it stays engaged. A player's rose is the player's: the
		// client owns stat 0xA5, moves a guard on the shield keybinds and uploads the new spread as a c2s
		// CombatShieldIIR, which HandlePlayerShieldRose stores here. So the tick never rolls or shifts a player's.
		public uint ShieldDistribution { get; set; }

		public bool Invisible { get; set; }

		public bool IsCaster => Spells.Count > 0;

		// Dead = has vitals and they hit zero. A dead entity is excluded from combat and counts down to respawn.
		public bool IsDead => Vitals?.IsDead == true;

		// A transient loot bag (expires + despawns) rather than a player/NPC.
		public bool IsLoot { get; private init; }

		// True when this entity can be looted: a standalone loot bag, or a dead NPC still carrying rolled loot (items
		// or cash). Loot is cleared when the NPC respawns (TickRespawn).
		public bool IsLootable => IsLoot || (IsDead && (LootItems.Count > 0 || CashTin > 0));

		// True while this player is riding a mount. Pressing the SummonMount action toggles it: mount when false (ride
		// buff + mount StatUpd), dismount when true (end-twin + StatUpd 0xBA=0). Without this the toggle re-mounts and
		// the client gets stuck at the end of the dismount channel.
		public bool IsMounted { get; set; }

		// The sneak/hide stance. A toggle: the same `PerformSpecialAction` selector both enters and leaves it, so the
		// server has to remember which way the next press goes - exactly like the mount toggle above.
		public bool IsHidden { get; set; }

		// When the `Resting` channel ends. Rest is a 20.5 s self-cast during which the player regains vitals, so it is
		// a window rather than a flag - and the window's length is the spell row's own cast time, not a number chosen
		// here. Null = not resting.
		public DateTimeOffset? RestingUntil { get; set; }

		public bool IsResting => RestingUntil > DateTimeOffset.UtcNow;

		// The shadow coverage the client last reported for this entity (SetShadowCoverageIIR). Retail puts this number
		// in the hide-enter behaviour's tail, so it has to be remembered between the client's report and the press.
		// 100 in every observation, which is what the client sends when it has not reported anything else yet - not a
		// scale that has been established (docs/subsystems/player-actions.md).
		public uint ShadowCoverage { get; set; } = 100;

		// A per-entity monotonic counter for the behaviour records the server initiates on this entity's behalf: the
		// `CommandMessage` phase (f3) and the command id retail echoes in `CommandqueueClientId`. Retail's is
		// per-session and monotonic, and the client does not key anything off the exact value - what it must not do is
		// repeat. Starts at 3 because that is the first value both captured sessions used for a stance.
		public uint NextBehaviorPhase() => ++_behaviorPhase;

		// The command id retail echoes in `CommandqueueClientId`. A separate counter from the phase, because the two
		// disagree in the captures: one hide-enter carries phase 5 with command id 3, and the Herald's pair runs
		// phase 3/4 against command id 3/4. Only the stance records that carry the field advance it.
		public uint NextBehaviorCommandId() => ++_behaviorCommandId;

		private uint _behaviorCommandId = 2;

		private uint _behaviorPhase = 2;

		// The mount's ride stat - the value the Mount stat carries on the wire, not the DB id - so a viewer arriving
		// after the mount can be told: the client renders the horse from the Mount/MountSpeed stats, so a late viewer
		// gets that stat block right after the spawn record.
		public uint MountRideStat { get; set; }

		// Damage this rider has soaked since mounting. Retail throws a rider off under fire, so the damage a mounted
		// player takes accumulates here and the mount comes off once it passes MountCatalog.ThrowOffDamage; reset on
		// every mount and every dismount. The ceiling is a placeholder - see MountCatalog - but the meter itself is
		// not: nothing else on the entity would let a hit reach the mount.
		public int MountDamageTaken { get; set; }

		public bool IsPlayer { get; }

		// True while this entity is mid-move (chasing / returning home / following a player). Drives the segment
		// throttle (the anchor is seeded when a move starts) and the one-shot stop (so an arrival emits a single StopIIR).
		// Cleared on arrival, death and respawn.
		public bool Moving { get; set; }

		// Takes the movement this entity has walked but not yet had broadcast, ending the move. Returns false when
		// there is nothing pending, so the caller sends no packet.
		//
		// It exists for death, which is the one way a move ends without going through the tick's own `EndMove`. The
		// tick only emits a segment once `MoveOrigin -> Position` reaches `MoveBroadcastSegment` (6u), so a creature
		// killed mid-chase carries up to 6u the client was never told about - and the death pose states the true
		// position, which would make the corpse jump that distance toward its killer right before the death animation.
		// Retail keeps the two in agreement: over the whole corpus, 250 of 267 death poses sit at distance 0.00 from
		// the end of that handle's preceding MoveTask. So the pose is right and the client is brought up to date.
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

		// When a timed summon should disappear, or null for anything that lives until killed/dismissed. The Frozen
		// Hatred fatality pet is the case it exists for: it is granted for 60s, each further fatality pushes the moment
		// out by another 60s, and the total is capped 5 minutes ahead. Checked by the world tick (TimedDespawn), so one
		// field carries both the lifetime and its extension - there is no separate timer to keep in sync.
		public DateTimeOffset? DespawnAt { get; set; }

		// /falldamage on|off. Retail's help states the default explicitly - "it is default on all players" - and it is
		// a per-character toggle there, not a server switch, so it lives on the entity and resets with the world entry
		// like any other live state.
		public bool FallDamageEnabled { get; set; } = true;

		// The prepared FA015807 spawn record (the bytes after the RPC header) for an NPC or a loot bag, built once at
		// map load (NpcSpawnBuilder.TryBuildLive from the typed NpcSpawn columns; loot via WorldObjectCodec) and
		// re-framed per viewer (current position + live playfield token). A runtime, in-memory cache - never persisted.
		//
		// Not used for players: a player's record is built per send from live state
		// (WorldBroadcaster.BuildPlayerSpawnBody), because a cached one would describe them as they were at login and
		// miss a mount, an equipment change or a level change taken since.
		public byte[] SpawnBody { get; set; } = null!;

		// For a loot bag dropped by a kill: the name of the NPC whose corpse it is (e.g. "Scavenger Tuut"). The bag is
		// a decoupled entity whose own Name is the generic "Bag of loot", so the source is recorded separately - it is
		// the match key for a LootFromNpc quest goal, since the quest item itself is RDB content the server does not
		// materialise. Null for a standalone bag (no source NPC).
		public string LootSourceName { get; set; } = null!;

		// The same corpse's NPC template id, carried beside the name so a LootFromNpc goal can match on the stable type
		// rather than a display string - `Saddur` is two different templates. 0 for a standalone bag, or one dropped by
		// something with no template behind it, which reads as "type unknown" and falls back to the name.
		public uint LootSourceTemplateId { get; set; }

		public string Name { get; }

		// Dev nameplate override: the full labelled overhead name ("Crocodile [Ape2 | id-prop x8]") to push via a
		// SetNameIIR right after this NPC's spawn, when the broadcaster's area-label debug toggle is on. The client
		// resolves an NPC's floating nameplate from its template_id LDB name and ignores the free spawn-body `Name`
		// string, so the label can only be shown by a SetNameIIR - the same per-entity name-commit the create flow
		// uses. Null = no label.
		public string OverheadNameOverride { get; set; } = null!;

		// For a loot bag: the player handle allowed to loot it (the killer, or the spawning player). Sent as the
		// NewAllowedLooterIIR's looter at spawn - retail's loot-permission grant, so a drop belongs to whoever earned it
		// rather than everyone in range. 0 = unowned (anyone). The take is not gated on it; Tortage is
		// single-player-instanced, so nobody else can reach the bag. Open work tracked in Gitea #101.
		public uint AllowedLooterHandle { get; set; }

		// The player's aggregated physical armor rating from worn gear, summed once at world entry and used to
		// mitigate incoming physical damage (CombatResolver.ApplyPhysicalMitigation). 0 for NPCs and unarmored players.
		public uint ArmorRating { get; set; }

		// Per-NPC swing counter, incremented each auto-attack to feed the attack-animation ack's per-actor counter
		// field (mirrors the small incrementing value the capture carries; not a persistence key).
		public uint AttackSwingCounter { get; set; }

		// Coins carried by a loot bag (tin, the wallet wire unit), rolled when the bag drops; zeroed once looted
		// (TryLootCash is granted exactly once per bag).
		public uint CashTin { get; set; }

		// The player's aggregated Combat Rating from worn gear (generic 0x36B), added as a provisional flat melee
		// damage bonus (its real hit-rate role needs a hit/miss model). 0 for NPCs / no gear.
		public uint CombatRating { get; set; }

		public uint CriticalDamageRating { get; set; }

		// Aggregated crit stats from worn gear: Critical Rating drives the melee crit chance, Critical Damage Rating
		// the crit magnitude (CombatResolver). 0 for NPCs / no gear.
		public uint CriticalRating { get; set; }

		// The retail overhead-name disposition toward the player: 0 = friendly (green), 1 = hostile (red),
		// 2 = neutral (yellow). Sourced from NpcStatCore.Disposition (captured cCategory) at zone load. Drives both
		// the RequestAggroStatus name-colour reply and Hostile (Hostile == Disposition == 1). NULL in the DB ->
		// DispositionFriendly here (matching the client's green default for an NPC it was never told about).
		public uint Disposition { get; set; } = DispositionFriendly;

		// The two template words the alternate-advancement pay-out multiplier reads - `NpcStatCore.NpcType` and
		// `NpcStatCore.NpcFlags`, stamped at spawn beside Disposition and FactionId. Null on a player, on a loot bag
		// and on any creature spawned from a pool rather than from a template row, which is the ordinary x1 case.
		public uint? NpcType { get; set; }
		public uint? NpcFlags { get; set; }

		// The wire handle other entities address this one by (the player's client instance / an NPC's spawn handle).
		public uint Handle { get; }

		// The player's aggregated Hit Rating from worn gear. Hit Rating governs hit chance (miss avoidance), which is
		// not modelled, so it is currently unused. 0 for NPCs / no gear.
		public uint HitRating { get; set; }

		// The entity's level (NPCs: from their NpcSpawn row; drives the kill-XP award). 0 if unknown.
		public uint Level { get; set; }

		// The player's aggregated Magic Damage rating from worn gear, summed at world entry; added flat to the damage
		// of spells they cast (CombatResolver.ResolveSpellDamage). 0 for NPCs and players with no such gear.
		public uint MagicDamage { get; set; }

		// The player's aggregated Natural Mana/Stamina Regen from worn gear (display units/sec), summed at world entry
		// and added onto the base regen tick (WorldSimulation). 0 for NPCs and players with no such gear.
		public uint NaturalManaRegen { get; set; }

		public uint NaturalStaminaRegen { get; set; }

		// The player's aggregated magical Protection rating from worn gear; mitigates incoming magic damage
		// (CombatResolver.ApplyMagicMitigation). 0 for NPCs and players with no Protection gear.
		public uint Protection { get; set; }

		// Companion follow: the handle of the leader this NPC follows (null = not following). Set when the player
		// "releases" the NPC (e.g. Casilda after her rescue stage); the world tick steps the NPC toward the leader and
		// broadcasts a MoveTask each step - the same server-driven incremental-step follow retail uses (the Hoxxling
		// gameplay capture: a stream of MoveTask from->to ~6-7u steps tracking the player). Cleared when the leader
		// leaves the zone / logs out. The shared Moving flag tracks whether she stepped so a single Stop closes the
		// move when she catches up.
		public uint? FollowTargetHandle { get; set; }

		// For a player: the handle of the NPC that last damaged them - used as the killer in the death's
		// SimpleCharKilledBy record. The resurrect window goes wave iff the killer resolves to the local player; a real
		// mob killer makes it instant like retail. Set by ApplyNpcAttack/ApplyNpcCast when a hit lands. For an NPC:
		// whoever hit it last (the aggro memory and the corpse's allowed looter).
		public uint? LastAttackerHandle { get; set; }

		// If this entity is a pet, the handle of the owner (the summoner's client instance); null = not a pet. Set
		// when the pet is bound via PetIIR owner-assign; QueuePetCommand validates the commander owns it.
		public uint? OwnerHandle { get; set; }
		// The three component shapes: where an NPC lives and walks, what it shows, what a player knows.
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
			// A player stands with the retail default guard - one shield per arc - from the moment they enter the
			// world, and moves it themselves from there (the client owns stat 0xA5 and uploads each change). An NPC
			// starts with no rose at all and rolls one when it engages.
			ShieldDistribution = isPlayer ? CombatShieldCodec.DefaultDistribution : 0;
		}

		// Resets the attack cooldown (e.g. when no target is in range), so re-aggro does not swing instantly.
		public void ArmAttackCooldown(double interval)
		{
			_attackCooldownRemaining = interval;
		}

		// Whether `viewer` (a player) may see this entity in interest streaming. Ungated entities are visible to all;
		// a gated NPC requires the viewer to hold one of its gating quests active.
		public bool IsVisibleTo(WorldEntity viewer)
		{
			// Hidden once the viewer has completed the gating quest (e.g. Casilda after her escort is done).
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
			ShieldDistribution = 0; // the shield rose belongs to the fight; the next engage rolls a fresh one
			Moving = false; // a mid-chase death stops moving (the corpse does not stream a trailing move)
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

		// Rolls this defender's opening shield rose and arms the first shift. Called on the engage transition, so
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

		// Drives the corpse timeline (drop -> fade -> quit, see CorpseFadeSeconds) and returns the one stage that came
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

			// The bag is dropped on the first tick after death rather than waiting out any delay - retail spawns it in
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
		// No-op (false) for anything that is not a loot bag.
		public bool TickExpiry(double dt)
		{
			if (!IsLoot)
				return false;

			_expiryRemaining -= dt;
			return _expiryRemaining <= 0;
		}

		// Advances the respawn countdown for a dead entity by dt; when it elapses, revives the entity (vitals back to
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
			// Reset the move anchor too: a chase/return leaves MoveOrigin at wherever the NPC last stepped (the spot it
			// died), so without this the first post-respawn MoveTask streams origin=death -> spawn and the client
			// renders the revived NPC back at its death location before walking home. Anchor it at spawn.
			MoveOrigin = Route.SpawnPosition;
			MoveFrom = Route.SpawnPosition;
			Moving = false; // a fresh NPC starts stationary; the next chase/return re-seeds the move anchor
			// Drop any unlooted corpse-bag contents - the entity is a live NPC again, not a bag.
			CashTin = 0;
			LootItems.Clear();
			_corpseAge = 0;
			_corpseStage = CorpseStage.None;
			InCombat = false; // the fresh NPC starts idle (sheathed); re-aggro re-draws via the combat-mode broadcast
			ShieldDistribution = 0; // ...and unrolls its shield rose until it engages again
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

	// A spell a caster NPC casts at its target: the full wire spell id + the captured ApplySpell effect payload
	// (interim verbatim). Sourced from the NpcSpell table by NPC name.
	public readonly record struct NpcSpellCast(uint SpellId, MagicBuffData Payload);

	// A pending ReachArea quest goal, solved when the player enters it on map `MapId`. Two shapes, and `Region`
	// decides which:
	//
	//   - `Region` set: the client's own trigger volume, resolved from the goal's typed LDB reference
	//     (`TrailingCategory` 51200 -> region name -> that playfield's RDB 1000053 record). Containment is the
	//     polygon prism, exactly as for a hazard.
	//   - `Region` null: the fallback - within `RadiusSq` (horizontal) of the goal's map marker (X, Z).
	//
	// The marker is a map pin, not the trigger, so the fallback is an approximation and the region is not. Goal
	// 1448's marker (416, 701) sits inside `Trappers Haven`, a 96 x 82 region, while the fallback tests a 40-unit
	// circle centred on the pin - overlapping, but neither contains the other. Only 3 goals carry a region.
	// QuestId is kept so a solved goal names its quest in logs.
	public readonly record struct ReachGoalTarget(uint QuestId, uint GoalId, uint MapId, float X, float Z, float RadiusSq, MapRegion? Region = null);
}
