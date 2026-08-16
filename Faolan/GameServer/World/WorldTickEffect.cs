namespace Faolan.GameServer.World
{
	// A wire-visible result of a world tick (Gitea #404), returned by WorldState.Tick for the
	// listener to broadcast. Keeps the model network-free: the tick mutates state and reports what happened; the
	// listener turns each effect into the right packet(s).
	public enum WorldTickEffectType
	{
		NpcRespawned, // a dead NPC's respawn timer elapsed; it is alive again at its spawn point (re-render it)
		NpcCorpseLootDropped, // an NPC just died; drop its loot bag as a SEPARATE dynel beside the body (retail does this in the death's own burst)
		NpcCorpseFaded, // the body has lain there for CorpseFadeSeconds; fade it out for viewers (n3ToClientFade)
		NpcCorpseQuit, // ...and CorpseQuitDelaySeconds later, drop the corpse handle for viewers (n3ToClientQuit)
		NpcCorpseRemoved, // a NoRespawn corpse finished the sequence above; take the entity out of the zone entirely
		LootExpired, // a loot bag's lifetime elapsed (despawn it for nearby players, then remove it from the zone)
		NpcAttack, // an aggroed NPC (Entity) swings at a player (Target); the listener applies damage + pushes vitals
		NpcMove, // an aggroed NPC (Entity) stepped toward its target player (Target); broadcast its MoveTask transform
		NpcEnterCombat, // an NPC (Entity) just engaged a Target; broadcast the combat-mode stat (draw weapon, #218) + the aggro (HateList/ThreatLevel, #216)
		NpcExitCombat, // an NPC (Entity) disengaged (leashed / lost its target); broadcast the combat-mode stat so it sheathes
		NpcStopped, // an NPC (Entity) arrived home and stopped; broadcast its StopIIR (final position + facing, #190)
		NpcCast, // a CASTER NPC (Entity) casts a spell at a player (Target); the listener sends ApplySpell + applies damage (#232)
		NpcPatrol, // a patrolling NPC (Entity) should (re-)issue its patrol route so it keeps walking its loop (#266)
		NpcFollow, // a companion NPC (Entity) stepped toward the PLAYER it follows (Target); broadcast its MoveTask, ungated (not combat) (#262)
		NpcAmbientAnimation, // the NPC (Entity) performs one of its creature's ambient idle animations (Entity.AmbientAnimationPicked): a BehaviorId.Emote record (#766)
		TimedDespawn, // a summon with a lifetime (Entity) ran out of time; fade it for nearby players + remove it (the Frozen Hatred fatality pet, #140)
		EncounterBark, // a scripted-encounter LURE (Entity) noticed an approaching player (Target) at its bark radius - cry for help once (#502)
		EncounterTrigger, // a player (Target) closed to the LURE's (Entity) trigger radius - swap the lure for its boss (#502)
		EncounterReset, // a scripted-encounter BOSS (Entity) is gone; put its lure back where it stood (#502)
		NpcShieldRose, // the NPC's (Entity) directional-combat shield rose changed - it engaged, or the shift timer moved a guard to another arc; broadcast the CombatShieldIIR (#20)
		NpcChatter, // an ambient bystander bark: the NPC (Entity) local-says a random RenownBystanderLovers line to nearby players (chat server), purely cosmetic
		NpcGreeting // a DETECTION bark (#694): the NPC (Entity) noticed a player (Target) ARRIVE and greets them by name - a `sendtell`, so it goes to that player alone
	}

	// `Target` is the attacked/chased/aggroed player for NpcAttack/NpcMove/NpcEnterCombat; null otherwise.
	public readonly record struct WorldTickEffect(WorldEntity Entity, WorldTickEffectType Type, WorldEntity? Target = null);
}
