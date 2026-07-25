using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// One live entity in the world simulation - a player or an NPC - identified by its session-scoped wire handle.
	// This is the authoritative, in-memory state the server owns (position/rotation, map), as opposed to the
	// per-session captured state the replay path used. The roadmap (Gitea #404) promotes the
	// scattered per-session maps into this shared model. Vitals/AI/buffs will hang off this as those systems land.
	public sealed class WorldEntity
	{
		// How long a fresh corpse stays as the dying creature (so the client plays its death animation off the
		// health=0 vitals it just received) before the corpse is converted to a lootable bag in place. Must be well
		// under RespawnDelaySeconds so the lootable window opens before the NPC revives.
		public const double CorpseLingerSeconds = 3.0;

		// Default time a killed NPC stays dead before the tick respawns it.
		public const double DefaultRespawnDelaySeconds = 15.0;

		public const uint DispositionFriendly = 0;
		public const uint DispositionHostile = 1;
		public const uint DispositionNeutral = 2;

		// The monotonic per-entity counter carried in field 3 of the continuous idle heartbeat (short-form
		// BehaviorControlCenterIIR, #225). Incremented once per emitted pulse so the client sees a steadily rising tick,
		// as retail's stream does.
		public uint HeartbeatTick;

		// Set once the corpse has been turned into a lootable bag this death, so the conversion fires exactly once.
		private bool _corpseConverted;

		// Time (seconds) until this NPC may swing again; counts down while it has a target in range.
		private double _attackCooldownRemaining;

		// Remaining time (seconds) the dying corpse is still the creature model (death animation) before it becomes a
		// loot bag; counts down while dead-and-not-yet-converted.
		private double _corpseLingerRemaining;

		// Remaining time (seconds) until a loot bag expires.
		private double _expiryRemaining;

		// Seconds until the patrol route is re-issued (retail re-paths a one-shot patrol so it keeps looping). Set to
		// the cycle length (path traversal time + dwell) when the route is issued; the tick counts it down.
		private double _patrolReissueRemaining;

		// Remaining time (seconds) until respawn while dead; meaningless when alive.
		private double _respawnRemaining;

		// The MapArea.AreaNameId the player is currently standing in, or 0 for none (#348). Live world state, never
		// persisted: it exists so the tick can tell a CROSSING (banner) from merely still being inside the area.
		public uint CurrentAreaNameId { get; set; }

		// The creature's death-animation behaviorId (the death BccIIR field-1, #362), from its NpcTemplate. Defaults to
		// the humanoid biped death (WorldDeath.HumanoidDeathBehaviorId); overridden per-type where harvested (demon /
		// quadruped). Read by CombatService.OnNpcKilled when it sends the death BccIIR.
		public uint DeathBehaviorId { get; set; } = WorldDeath.HumanoidDeathBehaviorId;

		// For an NPC: a quest that, once the VIEWER has COMPLETED it, HIDES this NPC (they don't spawn / are despawned).
		// The complement of GateQuestIds ("show while active") - e.g. Casilda vanishes from the beach once her escort
		// quest is completed. Data-driven from the Escort goal (escortee template -> escort quest id). null = never hidden.
		public uint? HideWhenQuestCompletedId { get; set; }

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

		// UTC time this player last dealt or took damage. HP/mana regen is OUT-OF-COMBAT only in retail (the client's
		// OOCHealth/ManaRegeneration stats), so while within WorldSimulation.CombatRegenPause of this the HP+mana regen
		// is halted; stamina (the action resource) keeps regenerating. MinValue = never in combat (fresh player regens).
		public DateTimeOffset LastCombatAt { get; set; } = DateTimeOffset.MinValue;

		// Live vitals (NPCs are seeded at load; null for entities the combat/world layer doesn't track here yet).
		// The world is the authoritative owner - combat applies damage to this, and the tick respawns off it.
		public EntityVitals Vitals { get; set; } = null!;

		// For a PLAYER: the quest ids the player currently has ACTIVE. Drives quest-gated NPC visibility (#261).
		// Refreshed at world entry and whenever the player's quest state changes (accept / advance / complete).
		public HashSet<uint> ActiveQuestIds { get; } = [];

		// For a PLAYER: the quest ids the player has COMPLETED. Drives "hide once done" NPC gating (#262) - e.g. Casilda
		// is gone from the beach once her escort quest is completed. Loaded alongside ActiveQuestIds.
		public HashSet<uint> CompletedQuestIds { get; } = [];

		// For a player: the handles the client has actually CONFIRMED constructing - i.e. polled via 0x2020 (the poll's
		// requestId is the entity handle; the server answers with a 0x2008 SpawnCheck that renders it). Known means "spawn
		// sent"; Confirmed means "client built it". A despawn (n3ToClientQuit) for a Known-but-not-Confirmed handle hits a
		// pcDynel-NULL loader crash on a still-loading client (#245), so interest only despawns Confirmed handles.
		public HashSet<uint> Confirmed { get; } = [];

		// For a player: the handles this player currently sees (has been sent a spawn for). Interest management diffs
		// against this set to decide what to spawn/despawn as entities move in and out of range. Unused for NPCs.
		public HashSet<uint> Known { get; } = [];

		// The STATIC world objects (props/doors/vines/destructibles) this player currently has rendered - the interest
		// known-set for Zone.WorldObjects, keyed by the object's handle within this playfield (#424).
		//
		// Deliberately separate from `Known` above, because the rule is the OPPOSITE. `Known` is sticky: an NPC sent at
		// entry is never culled mid-load (#245), and Known means "was ever sent". World objects are culled - retail
		// despawns them on range exit and re-sends the full spawn record verbatim on re-entry (measured across the login
		// captures: 565 of 648 repeated sends are byte-identical, the rest differ only in the optional kdtree field, and
		// 212 n3ToClientQuit records carry world-object handles with their own class). Sharing one set would force one
		// of the two rules onto both.
		//
		// Handles are per-PLAYFIELD, not global - the same small handle names a different object on another map - so
		// this set is cleared on every map change along with the rest of the entity's per-zone state.
		public HashSet<uint> KnownObjects { get; } = [];

		// The owning connection for a player (so the broadcaster can send packets to it); null for NPCs.
		public INetworkClient Client { get; }

		// #266 patrol: the captured waypoint route (absolute world coords) this NPC walks + its walk speed; empty for a
		// stationary NPC. Sent as a patrol MoveTask after the NPC spawns - retail drives idle patrol client-side (the
		// client walks the route), so the server just issues the captured route once per spawn/respawn.
		public IReadOnlyList<Vector3> PatrolWaypoints { get; set; } = [];

		// For an NPC: the quest ids that gate this NPC's visibility (null/empty = ungated, seen by everyone). A gated
		// NPC is streamed only to a viewer holding one of these quests active - e.g. Scavenger Tuut appears only while
		// you carry Casilda's kill quest. Data-driven from the quest goal definitions (kill-target NPCs that aren't
		// quest givers), so it generalises to any number of quests/NPCs. See IsVisibleTo.
		public IReadOnlyList<uint> GateQuestIds { get; set; } = null!;

		// Spells this CASTER NPC can cast at its target (#232), loaded from NpcSpell rows (by name) at zone load.
		// Empty for non-caster NPCs and players; IsCaster gates the sim's cast branch.
		public List<NpcSpellCast> Spells { get; } = [];

		// For a PLAYER: the pending ReachArea quest goals (#262) - a location objective is solved when the player moves
		// within range of its point. Loaded once at world entry (null = not yet loaded) so the per-move check is a pure
		// in-memory distance test, no DB hit; entries are removed as they're reached. Empty list = nothing to watch.
		public List<ReachGoalTarget> PendingReachGoals { get; set; } = null!;

		// Item template ids carried by a loot bag, rolled when it drops; granted to the looter's backpack via
		// DispatchAddItem (0x2020) on TryLootAll and removed as they're taken, so each item is granted exactly once.
		public List<uint> LootItems { get; } = [];

		// The loot CONTAINER this creature drops on death (from its NpcTemplate.LootContainerTemplateId): a plain Bag
		// by default, a bigger chest for bosses/elites. Read by the kill-drop path (OnNpcKilled -> SpawnLootBagAt) to
		// pick the LootContainerTemplate variant. Stamped at spawn from the type; harmless (unused) on players.
		public LootContainerTemplateId LootContainer { get; set; } = LootContainerTemplateId.Bag;
		public Quaternion Rotation { get; set; }
		public Quaternion SpawnRotation { get; set; }

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

		// Where the entity respawns (its original spawn point) - set for NPCs by ForNpc.
		public Vector3 SpawnPosition { get; set; }

		// #400: a PLAYER's currently-drawn weapon set (Primary = equip slots 6/8, Secondary = 20/21). Toggled by the
		// SwitchWeaponsButton action and seeded from Character.ActiveWeaponSet on world entry; the swap is relayed to
		// viewers via the BCC behavior channel. Unused for NPCs (they carry a fixed loadout).
		public WeaponSet ActiveWeaponSet { get; set; }
		public bool Following => FollowTargetHandle != null;

		// GM toggles on a PLAYER entity (.godmode / .invisible). GodMode: the player takes no damage from NPC swings.
		// Invisible: hostile NPCs don't aggro the player (skipped in target selection). Default off; cleared on relog
		// since the entity is recreated at world entry.
		public bool GodMode { get; set; }

		// Whether this NPC attacks players on sight. Driven by Disposition (only cCategory 1 = hostile aggros);
		// friendly/neutral NPCs (Citizens, vendors, guards, wildlife) never aggro. Set at zone load. Players are
		// never hostile here.
		public bool Hostile { get; set; }

		// True while this NPC is engaged in combat (a target in aggro range, not leashing home). Drives the combat-mode
		// stat broadcast that makes the client DRAW the NPC's weapon - without it the NPC swings empty-handed (#218).
		// The tick flips this and emits an NpcEnterCombat/NpcExitCombat effect on the transition; reset on death/respawn.
		public bool InCombat { get; set; }
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

		public bool IsPlayer { get; }

		// #272: true while this entity is mid-move (chasing / returning home / following a player). Drives the segment
		// throttle (the anchor is seeded when a move starts) and the one-shot stop (so an arrival emits a single StopIIR).
		// Cleared on arrival, death and respawn.
		public bool Moving { get; set; }

		// A summoned/test NPC that should NOT revive when killed: once dead it lingers as a lootable
		// corpse for RespawnDelaySeconds, then the tick fades + removes it instead of respawning it.
		public bool NoRespawn { get; set; }

		// #266 retail wander: a beach NPC with captured anchor points drifts between them - capture-measured as a SINGLE
		// short 2-point MoveTask to a nearby anchor every ~25-60s (NOT a continuous route, NOT client-driven). The server
		// owns the position (every client sees the same spot; a chase begins from where it actually is, not its spawn).
		public bool Patrols => PatrolWaypoints.Count > 0;

		// True while a leashed NPC is walking back to its spawn point - it ignores re-aggro until home (so a player
		// camping the leash edge can't ping-pong it).
		public bool Returning { get; set; }

		// The captured MoveTask record re-addressed to THIS entity (kept as the wander-anchor source only; no longer
		// replayed verbatim - retail does NOT walk a continuous route, see Patrols/NextWanderPoint).
		public byte[] PatrolBody { get; set; } = null!;

		// The FA015807 spawn BODY (the bytes after the RPC header) that renders this entity on a client. Constructed
		// FROM SCRATCH - never a verbatim capture: NPCs via NpcSpawnBuilder.TryBuildLive from the typed NpcSpawn columns
		// (the old captured NpcSpawn.Data blob is retired), pets via TryBuildFromType, loot via WorldObjectBuilder, and
		// players via PlayerSpawnBuilder.BuildFromScratch (stashed by CaptureOwnSpawnBody at entry). This is a RUNTIME,
		// in-memory cache of that built body - never persisted, never touched by the importer - which the broadcaster
		// re-frames per recipient (current position + live playfield token) in SendWorldSpawn, so it isn't rebuilt for
		// every viewer. Null until built / for entities with no spawn.
		public byte[] SpawnBody { get; set; } = null!;
		public double PatrolCycleSeconds { get; set; }

		// How long this entity stays dead before the tick respawns it.
		public double RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

		// For a LOOT BAG dropped by a kill: the NAME of the NPC whose corpse it is (e.g. "Scavenger Tuut"). The bag is a
		// decoupled entity (#246) whose own Name is the generic "Bag of loot", so the source is recorded separately - it
		// is the match key for a LootFromNpc quest goal (#261), since the quest item itself is RDB content we don't
		// materialise. Null for a standalone bag (no source NPC).
		public string LootSourceName { get; set; } = null!;
		public string Name { get; }

		// #363 dev nameplate override: the FULL labelled overhead name ("Crocodile [Ape2 | id-prop x8]") to push via a
		// SetNameIIR right after this NPC's spawn, when DebugAreaLabels is on. The client resolves an NPC's floating
		// nameplate from its template_id LDB name and IGNORES the free spawn-body `Name` string, so the label can only
		// be shown by a SetNameIIR (0xADCE0CDA) - the same per-entity name-commit the create flow uses. Null = no label.
		public string OverheadNameOverride { get; set; } = null!;

		// Ambient bark lines (verbatim <remoteformat> XML) this NPC type periodically local-says, from NpcChatter at
		// zone load. Null/empty = this NPC has no captured chatter group and stays silent.
		public string[] ChatterLines { get; set; } = null!;

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

		public WorldEntity(uint handle, uint mapId, bool isPlayer, string name, INetworkClient client)
		{
			Handle = handle;
			MapId = mapId;
			IsPlayer = isPlayer;
			Name = name;
			Client = client;
		}

		// Resets the attack cooldown (e.g. when no target is in range), so re-aggro doesn't swing instantly.
		public void ArmAttackCooldown(double interval)
		{
			_attackCooldownRemaining = interval;
		}

		// Arms the next wander move for a randomized ~25-60s (capture-measured retail cadence: a beach NPC drifts every
		// 30-80s, the spread keeps a pack from moving in lockstep).
		public void ArmPatrolCycle()
		{
			_patrolReissueRemaining = 25.0 + Random.Shared.NextDouble() * 35.0;
		}

		// Whether `viewer` (a player) may see this entity in interest streaming. Ungated entities are visible to all;
		// a gated NPC requires the viewer to hold one of its gating quests active.
		public bool IsVisibleTo(WorldEntity viewer)
		{
			// #262: hidden once the viewer has COMPLETED the gating quest (e.g. Casilda after her escort is done).
			if (HideWhenQuestCompletedId is { } hideQuest && viewer != null && viewer.CompletedQuestIds.Contains(hideQuest))
				return false;

			if (GateQuestIds == null || GateQuestIds.Count == 0)
				return true;
			if (viewer == null)
				return false;

			foreach (var questId in GateQuestIds)
			{
				if (viewer.ActiveQuestIds.Contains(questId))
					return true;
			}

			return false;
		}

		// Marks the entity dead (call once its vitals reach 0) and starts the corpse-linger + respawn countdowns. The
		// corpse stays as the dying creature for CorpseLingerSeconds (death animation), then the tick converts it to a
		// lootable bag; the respawn timer revives it later. Idempotent per death.
		public void MarkDead()
		{
			_respawnRemaining = RespawnDelaySeconds;
			_corpseLingerRemaining = CorpseLingerSeconds;
			_corpseConverted = false;
			InCombat = false; // combat ends on death; a respawn re-engages from a clean (sheathed) state
			Moving = false; // a mid-chase death stops moving (the corpse doesn't stream a trailing move, #272)
		}

		// The next wander destination: an anchor point other than where we already are (so the move isn't a no-op).
		// `isOccupied` (#363 ambient pools): when supplied, PREFER an anchor no other entity is parked on, so pool
		// members spread across their area's points instead of stacking; falls back to any non-current anchor when
		// every free one is taken. Null (captured patrols) keeps the plain random-non-current pick.
		public Vector3 NextWanderPoint(Func<Vector3, bool>? isOccupied = null)
		{
			if (PatrolWaypoints.Count == 1)
				return PatrolWaypoints[0];

			// Scan the anchors from a random offset (keeps the pick random) and take the first that isn't where we
			// already stand; among those, a FREE one wins, else remember the first taken anchor as the fallback.
			var start = Random.Shared.Next(PatrolWaypoints.Count);
			var fallback = Position;
			var haveFallback = false;
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var p = PatrolWaypoints[(start + i) % PatrolWaypoints.Count];
				if (Vector3.DistanceSquared(p, Position) < 1f)
					continue; // a no-op move to where we already are

				if (isOccupied == null || !isOccupied(p))
					return p;

				if (!haveFallback)
				{
					fallback = p;
					haveFallback = true;
				}
			}

			return fallback;
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

		// Counts down a fresh corpse's death-animation linger by dt; returns true the single tick it elapses, so the
		// caller converts the corpse to a lootable bag in place. Fires exactly once per death and only while dead.
		public bool TickCorpseLinger(double dt)
		{
			if (_corpseConverted || !IsDead)
				return false;

			_corpseLingerRemaining -= dt;
			if (_corpseLingerRemaining > 0)
				return false;

			_corpseConverted = true;
			return true;
		}

		// Counts down a loot bag's lifetime by dt; returns true the tick it expires (caller despawns + removes it).
		// No-op (false) for anything that isn't a loot bag.
		public bool TickExpiry(double dt)
		{
			if (!IsLoot)
				return false;

			_expiryRemaining -= dt;
			return _expiryRemaining <= 0;
		}

		// Counts down the wander timer; true when it's time for the next 2-point wander move (then re-arm via ArmPatrolCycle).
		public bool TickPatrolCycle(double dt)
		{
			if (!Patrols)
				return false;

			_patrolReissueRemaining -= dt;
			return _patrolReissueRemaining <= 0;
		}

		// A summoned NoRespawn NPC: once dead, run its respawn timer DOWN as a despawn timer instead of reviving -
		// returns true the tick it elapses so the caller fades + removes the corpse. Never revives the entity.
		public bool TickPostDeathDespawn(double dt)
		{
			if (!IsDead)
				return false;

			_respawnRemaining -= dt;
			return _respawnRemaining <= 0;
		}

		// Advances the respawn countdown for a DEAD entity by dt; when it elapses, revives the entity (vitals back to
		// full, transform back to its spawn point) and returns true so the caller can re-spawn it for nearby players.
		// No-op (false) for a living entity or one with no vitals.
		public bool TickRespawn(double dt)
		{
			if (!IsDead)
				return false;

			_respawnRemaining -= dt;
			if (_respawnRemaining > 0)
				return false;

			Vitals.Health = Vitals.MaxHealth;
			Position = SpawnPosition;
			Rotation = SpawnRotation;
			// Reset the move anchor too (#246): a chase/return leaves MoveOrigin at wherever the NPC last stepped
			// (the spot it died), so without this the first post-respawn MoveTask streams origin=death -> spawn and
			// the client renders the revived NPC back at its death location before walking home. Anchor it at spawn.
			MoveOrigin = SpawnPosition;
			MoveFrom = SpawnPosition;
			Moving = false; // a fresh NPC starts stationary (#272); the next chase/return re-seeds the move anchor
			// Drop any unlooted corpse-bag contents - the entity is a live NPC again, not a bag.
			CashTin = 0;
			LootItems.Clear();
			_corpseConverted = false;
			InCombat = false; // the fresh NPC starts idle (sheathed); re-aggro re-draws via the combat-mode broadcast
			Returning = false;
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
				SpawnPosition = position,
				SpawnRotation = rotation,
				Vitals = vitals!,
				NpcTemplateId = templateId
			};
		}
	}

	// A spell a caster NPC casts at its target (#232): the full wire spell id + the captured ApplySpell effect
	// payload (interim verbatim). Sourced from the NpcSpell table by NPC name.
	public readonly record struct NpcSpellCast(uint SpellId, byte[] Payload);

	// A pending ReachArea quest goal (#262): the goal is solved when the player is within `RadiusSq` (horizontal, on
	// map `MapId`) of the world point (X, Z) - the goal's map marker, which IS the objective's world location
	// (verified: Saddur's kill-goal marker == his spawn). QuestId is kept so a solved goal names its quest in logs.
	public readonly record struct ReachGoalTarget(uint QuestId, uint GoalId, uint MapId, float X, float Z, float RadiusSq);
}
