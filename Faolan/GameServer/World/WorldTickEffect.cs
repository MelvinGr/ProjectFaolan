namespace Faolan.GameServer.World
{
	/// <summary>
	///     A wire-visible result of a world tick, returned by the tick for the caller to broadcast.
	/// </summary>
	/// <remarks>
	///     It keeps the model network-free: the tick mutates state and reports what happened, and the caller turns each
	///     effect into the right packets.
	/// </remarks>
	public enum WorldTickEffectType
	{
		NpcRespawned, // a dead NPC's respawn timer elapsed; it is alive again at its spawn point (re-render it)
		NpcCorpseLootDropped, // an NPC just died; drop its loot bag as a separate dynel beside the body, which is what retail does in the death's own burst
		NpcCorpseFaded, // the body has lain there for CorpseFadeSeconds; fade it out for viewers (n3ToClientFade)
		NpcCorpseQuit, // ...and CorpseQuitDelaySeconds later, drop the corpse handle for viewers (n3ToClientQuit)
		NpcCorpseRemoved, // a NoRespawn corpse finished the sequence above; take the entity out of the zone entirely
		LootExpired, // a loot bag's lifetime elapsed (despawn it for nearby players, then remove it from the zone)
		NpcAttack, // an aggroed NPC (Entity) swings at a player (Target); the listener applies damage + pushes vitals
		NpcMove, // an aggroed NPC (Entity) stepped toward its target player (Target); broadcast its MoveTask transform
		NpcEnterCombat, // an NPC (Entity) just engaged a Target; broadcast the combat-mode stat (draw weapon) and the aggro (HateList/ThreatLevel)
		NpcExitCombat, // an NPC (Entity) disengaged (leashed / lost its target); broadcast the combat-mode stat so it sheathes
		NpcStopped, // an NPC (Entity) arrived home and stopped; broadcast its StopIIR (final position and facing)
		NpcCast, // a caster NPC (Entity) casts a spell at a player (Target); the caller sends ApplySpell and applies damage
		NpcPatrol, // a patrolling NPC (Entity) should re-issue its patrol route so it keeps walking its loop
		NpcFollow, // a companion NPC (Entity) stepped toward the player it follows (Target); broadcast its MoveTask, ungated because this is not combat
		NpcAmbientAnimation, // the NPC (Entity) performs one of its creature's ambient idle animations (Entity.Presentation.AmbientAnimationPicked): a BehaviorId.Emote record
		TimedDespawn, // a summon with a lifetime (Entity) ran out of time; fade it for nearby players and remove it (the Frozen Hatred fatality pet)
		EncounterBark, // a scripted-encounter lure (Entity) noticed an approaching player (Target) at its bark radius - cry for help once
		EncounterTrigger, // a player (Target) closed to the lure's (Entity) trigger radius - swap the lure for its boss
		EncounterReset, // a scripted-encounter boss (Entity) is gone; put its lure back where it stood
		NpcShieldRose, // the NPC's (Entity) directional-combat shield rose changed - it engaged, or the shift timer moved a guard to another arc; broadcast the CombatShieldIIR
		NpcChatter, // an ambient bystander bark: the NPC (Entity) local-says a random RenownBystanderLovers line to nearby players (chat server), purely cosmetic
		NpcGreeting // a detection bark: the NPC (Entity) noticed a player (Target) arrive and greets them by name - a `sendtell`, so it goes to that player alone
	}

	/// <summary>
	///     One tick effect. <c>Target</c> is the attacked, chased or aggroed player for NpcAttack, NpcMove and
	///     NpcEnterCombat, and null otherwise.
	/// </summary>
	public readonly record struct WorldTickEffect(WorldEntity Entity, WorldTickEffectType Type, WorldEntity? Target = null);
}
