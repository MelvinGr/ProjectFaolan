using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	// The wire side of the world simulation: the operations the tick needs to push to clients. Implemented by the
	// GameServer listener (which owns the packet framing + send), so the simulation depends on this narrow port
	// rather than on the whole listener. Lets the simulation own world STATE + TIMING without owning networking.
	public interface IWorldOutbound
	{
		// Gracefully fade an entity out for the players in range (n3ToClientFade) - used for expired loot.
		Task BroadcastEntityFade(WorldEntity entity);

		// Broadcast an NPC's aggro on a target (HateListUpdate + ThreatLevel) to nearby viewers, so the threat/aggro is
		// visible the tick it engages (#216).
		Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target);

		// Broadcast an NPC's combat-mode change (engaged -> draw weapon, disengaged -> sheathe) to the players who can
		// see it, as the captured stat-20 update. Without it the NPC swings empty-handed (#218).
		Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat);

		// Broadcast an NPC's idle heartbeat (short-form BehaviorControlCenterIIR 0x642CD3D6) to the players who have it
		// CONFIRMED, so it keeps playing its idle animation instead of freezing (#225). Confirmed-gated (not Known) so it
		// is never sent to a still-loading client - the pcDynel-NULL hang the earlier heartbeat experiment hit.
		Task BroadcastNpcHeartbeat(WorldEntity npc);

		// Broadcast a defender's directional-combat shield rose (CombatShieldIIR 0xCFFD0519) to the players who can see
		// it, so the client draws the blue guard arcs over it and the attacker can aim at the weakest one (#20). Serves
		// both sides: the tick's NPC rose, and a player's own rose relayed to the opponents who can see them (#620).
		Task BroadcastShieldRose(WorldEntity entity);

		// #502 scripted encounters. The tick decides WHEN (proximity, in WorldState); these do the wire side: replace a
		// lure with its boss on the same spot, and put the lure back once the boss is gone.
		Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure);
		Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss);

		// Broadcast an NPC's tick step (its MoveTask transform) to the players who can see it - chasing a target or
		// returning home - so they watch it move.
		Task BroadcastNpcMove(WorldEntity npc);

		// (Re-)issue a patrolling NPC's captured route to the players who can see it, so it keeps walking its loop
		// (retail re-paths idle patrols; #266).
		Task BroadcastNpcPatrol(WorldEntity npc);

		// Re-render a respawned NPC for the players in range (its stored FA015807 spawn body).
		Task BroadcastNpcRespawn(WorldEntity npc);

		// Broadcast an NPC's StopIIR (final position + facing) when it arrives home and stops moving, closing the move
		// it was streaming (#190).
		Task BroadcastNpcStop(WorldEntity npc);

		// Convert a dead NPC's corpse to a lootable bag in place (after its death-animation linger), or despawn it if
		// no loot bag can be built for the map.
		Task ConvertCorpseToLoot(WorldEntity npc);

		// #363: top up the ambient spawn-group pools of every loaded zone to their MaxCount (respawn killed members).
		// Called on a slow cadence from the sim tick; no-op unless the ambient spawner is enabled.
		Task MaintainAmbientPools();

		// Push a 0x96B8DC59 vitals update for the player (regen tick, NPC-attack damage).
		Task PushVitals(INetworkClient client, EntityVitals vitals);

		// Re-derive the player's buff-modified stats and push whatever changed (#367). The tick calls it wherever it
		// takes buffs OFF a player - expiry, death - so a stat a buff raised comes back down.
		Task SyncBuffStats(INetworkClient client);

		// The revive action (BccIIR 0x642CD3D6) retail sends on RespawnMe to transition the player out of the Dead
		// substate (bcc_state 29→1) - closes the release window + revives IN PLACE, NOT a world re-entry (#214).
		// #348: the sub-area name banner, fired by the tick when a player crosses into a MapArea.
		Task SendAreaNameBanner(INetworkClient client, uint areaNameId);

		// Remove an active buff icon: the retail "end-twin" of the apply (the SAME ApplySpell 0x6B87E316 carrier with
		// the spell id's rank byte set to 1 and an EMPTY payload). Confirmed from the combat captures - retail expires
		// timed buffs by sending this, not by letting the client self-expire - so the tick fires it when a tracked
		// buff's duration elapses, making the icon disappear at 0 instead of sticking. applyTo/applyFrom mirror the
		// apply: equal for a self-buff, applyTo=player + applyFrom=NPC for an NPC-cast debuff (#274).
		Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId);

		// Apply a spell to the player as a self-buff (ApplySpell, caster == subject), taking the WIRE id
		// ((base << 8) | rank). The tick needs it for the death penalty, which is applied at the moment of death.
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId);

		// The floating combat-damage number over the player when an NPC hits them: DamageResultIIR (0xFEC4158A),
		// target=player + attacker=NPC (the player-attacks-NPC direction already rides CombatService). #274.
		Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage);

		// Unseat a mounted player: the ride buff's end-twin + StatUpd(Mount=0), the same two records every retail
		// dismount ends with. The tick needs it for the two dismounts it owns rather than the client - a hit that
		// throws the rider, and the respawn after a death (#142).
		Task SendDismount(INetworkClient client);

		// The in-session DEATH action (BccIIR 0x642CD3D6, behaviorId 41877) that transitions the live SimpleChar INTO
		// the dead state (bcc_state 29) so the release window shows - the in-session analogue of the login spawn's
		// bcc_state=29. Sent at the end of the death sequence (#214).
		Task SendDeathBcc(INetworkClient client, uint dyingHandle);

		// The death-pose MoveTask (0x96C46740 movement-type 0x06) retail forces onto the dying player just before the
		// kill record (#214); puts the avatar into the dead/incapacitated state at the death position.
		Task SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition);

		// The explicit death trigger (SimpleCharKilledByUpdateIIR 0xB066AFC2) the client needs to enter the dead
		// (release-screen) state - a health=0 StatUpdate alone doesn't (#214). Sent before the resurrection-spots list.
		Task SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint);

		// Send the NPC's attack-animation ack to the player it hit (the decoded "second BccAck" - BehaviorControlCenterIIR
		// with actor = NPC handle), so the client plays the NPC's swing + the player's hit-react. Visual only - the HP
		// move rides PushVitals.
		// `ranged` picks the CARRIER: a bow-armed NPC's swing is `Combat:Ranged`, two records rather than one, where a
		// melee one is `Combat:Melee` (#676). It is the entity's `RangedAttacker`, already stamped at load.
		Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId, bool ranged);

		// Send a caster NPC's spell cast at the player: the ApplySpell (0x6B87E316) from the NPC handle (#232). The
		// damage/effect is the captured payload; visual + the HP move rides the caller's PushVitals (like the swing).
		Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell);

		// Retail's per-tick vitals regen delta (RegenIIR): [+health][+mana][+stamina] in the ×100 wire scale, added to
		// the client's current bars so they regen smoothly (vs pushing a full stat block every tick).
		Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta);

		// The "resurrection point discovered" FIFO banner (#372) - retail's toast, sent in the same frame as the
		// spots re-send. `nameKey` is the per-point key of the point's category-270 name string; see
		// FormatFeedbackCodec.BuildRespawnPointDiscoveredXml.
		Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey);

		// #446: the kill-chain announcement, `ChainKillIIR` on the player's own handle. `count` is the running chain
		// (2 or 3 in the corpus, never 1) or 0 to close a lapsed one. Sent to the killer only - all 67 captured
		// records name the capturing player's own handle.
		Task SendKillChain(INetworkClient client, uint handle, uint count);

		// The player's resurrection spots: DispatchValidResurrectionSpots (0x2024) with a reference position + every
		// spot they may resurrect at. Serves BOTH of retail's uses, which is why it is named for the list it carries
		// and not for the release screen:
		//   - ON DEATH, with the death position - the client draws the release screen and replies RespawnMe(spotId)
		//     (handled inbound). It is SimpleCharKilledByUpdateIIR that puts the client in the dead state, not this.
		//   - ON DISCOVERY, with the LIVE position - re-sent with the newly-found spot in it so the release screen has
		//     it next death (#372). This is NOT the notification: retail pairs it with a FormatFeedbackIIR banner in
		//     the same frame (SendRespawnPointDiscoveredBanner) and that is what the player sees. Sending this alone
		//     puts byte-perfect bytes on the wire and shows the player nothing - measured, not assumed.
		// Proven in Hoxxling_mage-heraldofxotli_login_gameplay: frame 138 = one spot, frame 7862 = two, the second
		// arriving while the player was ALIVE and mid-walk - so this alone never opens the release screen either.
		Task SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots);

		Task SendRevive(INetworkClient client, uint handle, uint respawnPointId);

		// The actual position move to the res point: the revive n3Teleport (0x5A32F0D7) carrying the destination
		// position. The IntendedTeleport above only confirms the playfield (no move); retail follows it with THIS to
		// relocate the player (#214, decoded from the beach resurrect sequence IntendedTeleport -> n3Teleport).
		Task SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, float facingX, float facingZ);

		// The same-map revive teleport (IntendedTeleport 0x10D27BC0) relocating the revived player to the chosen res
		// point with NO loading screen. playfieldToken MUST be the client's LIVE token (Zone.PlayfieldToken) so the
		// client recognizes the same playfield and just repositions (#214).
		Task SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken);

		// A DoT tick's StatDamageIIR (0xE9F24184): the health effect on the player from an active bleed/HoT debuff -
		// statValue = the player's resulting HealthCurrent, delta the per-tick change, type the damage-school flag,
		// caster the NPC, spellBaseId the wire id >> 8. Paired with PushVitals for the bar move. #274.
		Task SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId);
	}
}
