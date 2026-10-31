using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The wire side of the world simulation: the operations the tick needs to push to clients.
	/// </summary>
	/// <remarks>
	///     The simulation depends on this narrow port rather than on the listener, so it can own world state and timing
	///     without owning networking.
	/// </remarks>
	public interface IWorldOutbound
	{
		/// <summary>
		///     Gracefully fade an entity out (n3ToClientFade) for the players in range - used for expired loot.
		/// </summary>
		Task BroadcastEntityFade(WorldEntity entity);

		/// <summary>
		///     Broadcast an NPC's aggro on a target (HateListUpdate and ThreatLevel) to nearby viewers, so the threat
		///     is visible on the tick it engages.
		/// </summary>
		Task BroadcastNpcAggro(WorldEntity npc, WorldEntity target);

		/// <summary>
		///     Broadcast an NPC's combat-mode change - engaged draws the weapon, disengaged sheathes it - to the players
		///     who can see it, as the captured stat-20 update. Without it the NPC swings empty-handed.
		/// </summary>
		Task BroadcastNpcCombatMode(WorldEntity npc, bool inCombat);

		/// <summary>
		///     Broadcast the ambient idle animation the tick picked for this NPC (<c>BehaviorId.Emote</c>) to the
		///     players who have it confirmed: a still-loading viewer has no dynel to play it on.
		/// </summary>
		Task BroadcastNpcAmbientAnimation(WorldEntity npc);

		/// <summary>
		///     Broadcast a defender's directional-combat shield rose (<c>CombatShieldIIR</c>) to the players who can see
		///     it, so the client draws the guard arcs and the attacker can aim at the weakest one.
		/// </summary>
		/// <remarks>
		///     Serves both sides: the tick's NPC rose, and a player's own rose relayed to the opponents who see them.
		/// </remarks>
		Task BroadcastShieldRose(WorldEntity entity);

		/// <summary>
		///     The wire side of a scripted encounter: replace a lure with its boss on the same spot, and put the lure
		///     back once the boss is gone. The tick decides when, by proximity, in <see cref="WorldState" />.
		/// </summary>
		Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure);

		/// <inheritdoc cref="SwapEncounterLureForBoss" />
		Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss);

		/// <summary>
		///     Broadcast an NPC's tick step - its MoveTask transform - to the players who can see it, whether it is
		///     chasing a target or returning home.
		/// </summary>
		Task BroadcastNpcMove(WorldEntity npc);

		/// <summary>
		///     The companion-follow step: retail's mode-04 list bound to the leader, not a bare path segment.
		/// </summary>
		Task BroadcastNpcFollow(WorldEntity npc, WorldEntity leader);

		/// <summary>
		///     Re-issue a patrolling NPC's captured route to the players who can see it, so it keeps walking its loop.
		///     Retail likewise re-paths idle patrols.
		/// </summary>
		Task BroadcastNpcPatrol(WorldEntity npc);

		/// <summary>Re-render a respawned NPC for the players in range, from its stored FA015807 spawn body.</summary>
		Task BroadcastNpcRespawn(WorldEntity npc);

		/// <summary>
		///     Broadcast an NPC's StopIIR - final position and facing - when it arrives home and stops moving, closing
		///     the move it was streaming.
		/// </summary>
		Task BroadcastNpcStop(WorldEntity npc);

		/// <summary>
		///     Roll a freshly killed NPC's drop and spawn its loot bag as a separate dynel beside the body.
		/// </summary>
		/// <remarks>
		///     The corpse is left standing: retail's bag and body coexist, and the body is torn down by
		///     <see cref="BroadcastCorpseFade" /> and <see cref="BroadcastEntityDespawn" />.
		/// </remarks>
		Task DropCorpseLoot(WorldEntity npc);

		/// <summary>
		///     Fade a dead NPC's body out for the players in range (n3ToClientFade, the 2.0 s Character dissolve),
		///     about 30.8 s after the kill. Its handle is dropped a couple of seconds later via
		///     <see cref="BroadcastEntityDespawn" />.
		/// </summary>
		Task BroadcastCorpseFade(WorldEntity npc);

		/// <summary>
		///     Drop an entity's handle for the players in range (n3ToClientQuit): the second half of the corpse
		///     teardown, and the plain despawn used wherever an entity should simply stop existing for a viewer.
		/// </summary>
		Task BroadcastEntityDespawn(WorldEntity entity);

		/// <summary>
		///     An entity was relocated rather than walked - the mode-02 snap, for the viewers who keep it in sight.
		/// </summary>
		Task BroadcastEntitySnap(WorldEntity entity);

		/// <summary>
		///     Re-run one player's interest pass with nothing having changed on their side, so entities that moved
		///     themselves are streamed in and out for a player standing still. See <c>WorldBroadcaster.ResolveInterest</c>.
		/// </summary>
		Task ResolveInterest(INetworkClient client);

		/// <summary>
		///     Top up the ambient spawn-group pools of every loaded zone to their <c>MaxCount</c>, respawning killed
		///     members. Called on a slow cadence from the simulation tick; a no-op unless the ambient spawner is enabled.
		/// </summary>
		Task MaintainAmbientPools(Zone zone);

		/// <summary>
		///     Run the tradeskills whose bar has finished: the craft's item grant, and the gem going into its socket.
		/// </summary>
		/// <remarks>
		///     Retail paces both server-side, so the transaction is staged by the inbound handler and completed here.
		///     The tick owns the cadence; <c>TradeskillService</c> owns the deadline and the work.
		/// </remarks>
		Task CompleteDueTradeskills(Zone zone);

		/// <summary>
		///     Play one due step of a harvested world object's staged visual sequence - mesh swap, collision drop,
		///     regrowth - to everyone who can see it. The tick owns the timing; this is the wire side.
		/// </summary>
		Task PlayDynelVisualStep(DueDynelVisualStep step);

		/// <summary>
		///     A harvest node's gathering-pin window ran out: put its own map-pin kind back (WaypointType, 0xAC).
		/// </summary>
		Task RestoreNodeWaypointType(Zone zone, LiveWorldObject node);

		/// <summary>Push a 0x96B8DC59 vitals update for the player - the regen tick, or NPC-attack damage.</summary>
		Task PushVitals(INetworkClient client, EntityVitals vitals);

		/// <summary>Re-derive the player's buff-modified stats and push whatever changed.</summary>
		/// <remarks>
		///     The tick calls it wherever it takes buffs off a player - expiry, death - so a stat a buff raised comes
		///     back down.
		/// </remarks>
		Task SyncBuffStats(INetworkClient client);

		/// <summary>
		///     The sub-area name banner, fired by the tick when a player crosses into a <c>MapArea</c>.
		/// </summary>
		Task SendAreaNameBanner(INetworkClient client, uint areaNameId);

		/// <summary>
		///     Remove an active buff icon: the end-twin of the apply, the same ApplySpell carrier with the spell id's
		///     rank byte set to 1 and an empty payload.
		/// </summary>
		/// <remarks>
		///     Retail expires timed buffs by sending this rather than letting the client self-expire, so the tick fires
		///     it when a tracked buff's duration elapses and the icon disappears at 0 instead of sticking.
		///     <paramref name="applyTo" /> and <paramref name="applyFrom" /> mirror the apply: equal for a self-buff,
		///     player and NPC respectively for an NPC-cast debuff.
		/// </remarks>
		Task SendBuffRemoval(INetworkClient client, uint applyTo, uint applyFrom, uint appliedSpellId);

		/// <summary>
		///     Apply a spell to the player as a self-buff (ApplySpell, caster equal to subject), taking the wire id
		///     <c>(base &lt;&lt; 8) | rank</c>. The tick needs it for the death penalty, applied at the moment of death.
		/// </summary>
		Task ApplySelfBuff(INetworkClient client, uint wireSpellId);

		/// <summary>
		///     The floating combat-damage number over the player when an NPC hits them: <c>DamageResultIIR</c> with the
		///     player as target and the NPC as attacker. The player-attacks-NPC direction rides <c>CombatService</c>.
		/// </summary>
		Task SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage);

		/// <summary>
		///     Unseat a mounted player: the ride buff's end-twin plus a Mount=0 stat update, the two records every
		///     retail dismount ends with.
		/// </summary>
		/// <remarks>
		///     The tick needs it for the two dismounts it owns rather than the client: a hit that throws the rider, and
		///     the respawn after a death.
		/// </remarks>
		Task SendDismount(INetworkClient client);

		/// <summary>
		///     The in-session death action (<c>BccIIR</c>, behaviorId 41877) that transitions the live SimpleChar into
		///     the dead state (bcc_state 29) so the release window shows - the in-session analogue of the login spawn's
		///     bcc_state 29. Sent at the end of the death sequence.
		/// </summary>
		Task SendDeathBcc(INetworkClient client, uint dyingHandle);

		/// <summary>
		///     The death-pose MoveTask (movement type 0x06) retail forces onto the dying player just before the kill
		///     record, putting the avatar into the incapacitated state at the death position.
		/// </summary>
		Task SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition);

		/// <summary>
		///     The explicit death trigger (<c>SimpleCharKilledByUpdateIIR</c>) the client needs to enter the
		///     release-screen state; a health=0 stat update alone does not. Sent before the resurrection-spots list.
		/// </summary>
		Task SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint);

		/// <summary>
		///     Send the NPC's attack-animation ack to the player it hit - a <c>BehaviorControlCenterIIR</c> with the NPC
		///     handle as actor - so the client plays the NPC's swing and the player's hit-react.
		/// </summary>
		/// <remarks>
		///     Visual only: the health move rides <see cref="PushVitals" />.
		/// </remarks>
		/// <param name="ranged">
		///     Picks the carrier: a bow-armed NPC's swing is <c>Combat:Ranged</c>, two records rather than one, where a
		///     melee one is <c>Combat:Melee</c>. It is the entity's <c>RangedAttacker</c>, stamped at load.
		/// </param>
		Task SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter, SwingDirection direction, uint comboId, bool ranged);

		/// <summary>Send a caster NPC's spell cast at the player: the ApplySpell from the NPC handle.</summary>
		/// <remarks>
		///     The damage or effect is the captured payload; the visual and the health move ride the caller's
		///     <see cref="PushVitals" />, as the swing does.
		/// </remarks>
		Task SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell);

		/// <summary>
		///     Retail's per-tick vitals regen delta (<c>RegenIIR</c>): health, mana and stamina deltas in the x100 wire
		///     scale, added to the client's current bars so they regen smoothly rather than jumping a full stat block.
		/// </summary>
		Task SendRegen(INetworkClient client, uint handle, int healthDelta, int manaDelta, int staminaDelta);

		/// <summary>
		///     The "resurrection point discovered" banner: retail's toast, sent in the same frame as the spots re-send.
		/// </summary>
		/// <param name="nameKey">
		///     The per-point key of the point's category-270 name string; see
		///     <c>FormatFeedbackCodec.BuildRespawnPointDiscoveredXml</c>.
		/// </param>
		Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey);

		/// <summary>The kill-chain announcement, <c>ChainKillIIR</c> on the player's own handle.</summary>
		/// <remarks>
		///     Sent to the killer only - all 67 captured records name the capturing player's own handle.
		/// </remarks>
		/// <param name="count">The running chain (2 or 3 in the corpus, never 1), or 0 to close a lapsed one.</param>
		Task SendKillChain(INetworkClient client, uint handle, uint count);

		/// <summary>
		///     The player's resurrection spots: <c>DispatchValidResurrectionSpots</c> with a reference position and
		///     every spot they may resurrect at.
		/// </summary>
		/// <remarks>
		///     Named for the list it carries rather than for the release screen, because it serves both of retail's
		///     uses:
		///     <list type="bullet">
		///         <item>
		///             On death, with the death position - the client draws the release screen and replies
		///             <c>RespawnMe(spotId)</c>. It is <c>SimpleCharKilledByUpdateIIR</c> that puts the client in the
		///             dead state, not this.
		///         </item>
		///         <item>
		///             On discovery, with the live position - re-sent with the newly found spot in it so the release
		///             screen has it next death. This is not the notification: retail pairs it with a
		///             <c>FormatFeedbackIIR</c> banner in the same frame
		///             (<see cref="SendRespawnPointDiscoveredBanner" />) and that is what the player sees. Sent alone it
		///             shows nothing.
		///         </item>
		///     </list>
		///     In Hoxxling_mage-heraldofxotli_login_gameplay frame 138 carries one spot and frame 7862 carries two, the
		///     second arriving while the player was alive and mid-walk, so this alone never opens the release screen.
		/// </remarks>
		Task SendResurrectionSpots(INetworkClient client, Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots);

		/// <summary>
		///     The revive action retail sends on <c>RespawnMe</c> to transition the player out of the dead substate
		///     (bcc_state 29 to 1). It closes the release window and revives in place; it is not a world re-entry.
		/// </summary>
		Task SendRevive(INetworkClient client, uint handle, uint respawnPointId);

		/// <summary>
		///     The actual position move to the resurrection point: the revive n3Teleport carrying the destination.
		/// </summary>
		/// <remarks>
		///     <see cref="SendReviveTeleport" /> only confirms the playfield and moves nothing; retail follows it with
		///     this to relocate the player.
		/// </remarks>
		Task SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, Quaternion rotation);

		/// <summary>
		///     The same-map revive teleport (<c>IntendedTeleport</c>) relocating the revived player to the chosen
		///     resurrection point with no loading screen.
		/// </summary>
		/// <param name="playfieldToken">
		///     Must be the client's live token (<see cref="Zone.PlayfieldToken" />), so the client recognises the same
		///     playfield and simply repositions.
		/// </param>
		Task SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken);

		/// <summary>
		///     A <c>StatDamageIIR</c>: a pool move on the target - a damage-over-time tick, an NPC's swing, a hostile
		///     cast. Paired with <see cref="PushVitals" /> for the bar move.
		/// </summary>
		/// <param name="displayHealth">
		///     The resulting <c>HealthCurrent</c> in display units, scaled to the wire by the sender. This is the field
		///     that moves the bar.
		/// </param>
		/// <param name="delta">The damage number, unscaled.</param>
		/// <param name="type">The damage-school flag.</param>
		/// <param name="spellBaseId">The wire spell id shifted right by 8.</param>
		Task SendStatDamage(INetworkClient client, uint targetHandle, uint displayHealth, int delta, uint type, uint casterHandle, uint spellBaseId);

		/// <summary>
		///     Apply a spell's area - <paramref name="effectSpellId" /> to up to <paramref name="maxTargets" /> entities
		///     within <paramref name="radius" /> metres of <paramref name="sourceHandle" /> - and answer which entities
		///     it reached.
		/// </summary>
		/// <remarks>
		///     The tick's half of an aura: a buff whose spell states a periodic area re-applies it through here every
		///     period, for as long as the buff is up. <paramref name="sourceHandle" /> is what the area radiates from,
		///     which is not always the player - see <c>WorldSimulation.ApplyAreaEffect</c>.
		/// </remarks>
		/// <returns>
		///     The victim handles rather than a count: the caller has to put the effect itself on each victim, so it
		///     needs to know who they were.
		/// </returns>
		Task<IReadOnlyList<uint>> ApplySpellArea(INetworkClient client, uint sourceHandle, uint effectSpellId, float radius, uint maxTargets);

		/// <summary>
		///     The ApplySpell (<c>MagicBuffIIR</c>) carrier for a buff landing on someone other than the caster - the
		///     effect a player's area puts on the mobs around it.
		/// </summary>
		/// <remarks>
		///     <paramref name="applyTo" /> differs from <paramref name="applyFrom" />, so the send and relay register
		///     nothing: the world model owns a non-player entity's buff registry, and the tick counts it down.
		/// </remarks>
		Task SendSpellApplication(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId, uint durationCentiseconds);

		/// <summary>
		///     One damage-over-time tick against a non-player entity: the health move plus its floating damage number.
		/// </summary>
		/// <remarks>
		///     Runs through the same kill lifecycle a spell hit uses - experience, corpse, quest credit - so a bleed a
		///     player started can land the killing blow.
		/// </remarks>
		/// <param name="delta">The display-scale per-tick change; negative for harm.</param>
		Task ApplyNpcDotTick(INetworkClient client, uint npcHandle, uint casterHandle, int delta, uint spellId);
	}
}
