using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
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

		// Re-run the world-entry render path for the player (used to respawn them at a resurrection point).
		Task ReenterWorld(INetworkClient client);

		// Remove an active buff icon: the retail "end-twin" of the apply (the SAME ApplySpell 0x6B87E316 carrier with
		// the spell id's rank byte set to 1 and an EMPTY payload). Confirmed from the combat captures - retail expires
		// timed buffs by sending this, not by letting the client self-expire - so the tick fires it when a tracked
		// buff's duration elapses, making the icon disappear at 0 instead of sticking. applyTo/applyFrom mirror the
		// apply: equal for a self-buff, applyTo=player + applyFrom=NPC for an NPC-cast debuff (#274).
		Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId);

		// The floating combat-damage number over the player when an NPC hits them: DamageResultIIR (0xFEC4158A),
		// target=player + attacker=NPC (the player-attacks-NPC direction already rides CombatService). #274.
		Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage);

		// The in-session DEATH action (BccIIR 0x642CD3D6, behaviorId 41877) that transitions the live SimpleChar INTO
		// the dead state (bcc_state 29) so the release window shows - the in-session analogue of the login spawn's
		// bcc_state=29. Sent at the end of the death sequence (#214).
		Task SendDeathBcc(INetworkClient client, uint dyingHandle);

		// The self-applied death-state effect (ApplySpell 0x6B87E316, spell 0x0009F000) retail sends right after the
		// release screen (#214). Without it the SimpleCharKilledBy + 0x2024 still left the client alive in-client.
		Task SendDeathEffect(INetworkClient client, uint dyingHandle);

		// The death-pose MoveTask (0x96C46740 movement-type 0x06) retail forces onto the dying player just before the
		// kill record (#214); puts the avatar into the dead/incapacitated state at the death position.
		Task SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition);

		// The explicit death trigger (SimpleCharKilledByUpdateIIR 0xB066AFC2) the client needs to enter the dead
		// (release-screen) state - a health=0 StatUpdate alone doesn't (#214). Sent before the resurrection-spots list.
		Task SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint);

		// Send the NPC's attack-animation ack to the player it hit (the decoded "second BccAck" - BehaviorControlCenterIIR
		// with actor = NPC handle), so the client plays the NPC's swing + the player's hit-react. Visual only - the HP
		// move rides PushVitals.
		Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter);

		// Send a caster NPC's spell cast at the player: the ApplySpell (0x6B87E316) from the NPC handle (#232). The
		// damage/effect is the captured payload; visual + the HP move rides the caller's PushVitals (like the swing).
		Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell);

		// The player's resurrection spots: DispatchValidResurrectionSpots (0x2024) with a reference position + every
		// spot they may resurrect at. Serves BOTH of retail's uses, which is why it is named for the list it carries
		// and not for the release screen:
		//   - ON DEATH, with the death position - the client draws the release screen and replies RespawnMe(spotId)
		//     (handled inbound). It is SimpleCharKilledByUpdateIIR that puts the client in the dead state, not this.
		//   - ON DISCOVERY, with the LIVE position - re-sending the list with a newly-found spot in it is the whole
		//     discovery notification (#372): the client diffs it against what it held and toasts itself. There is no
		//     server-sent toast; the text is client-side, like NewLevelIIR's level banner.
		// Proven in Hoxxling_mage-heraldofxotli_login_gameplay: frame 138 = one spot, frame 7862 = two, the second
		// arriving while the player was ALIVE and mid-walk - so this alone never opens the release screen.
		Task SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots);

		// The revive action (BccIIR 0x642CD3D6) retail sends on RespawnMe to transition the player out of the Dead
		// substate (bcc_state 29→1) - closes the release window + revives IN PLACE, NOT a world re-entry (#214).
		// #348: the sub-area name banner, fired by the tick when a player crosses into a MapArea.
		Task SendAreaNameBanner(INetworkClient client, uint areaNameId);

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
