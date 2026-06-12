namespace Faolan.GameServer.World
{
	// A wire-visible result of a world tick (docs/archive/server-architecture-roadmap.md), returned by WorldState.Tick for the
	// listener to broadcast. Keeps the model network-free: the tick mutates state and reports what happened; the
	// listener turns each effect into the right packet(s).
	public enum WorldTickEffectType
	{
		NpcRespawned, // a dead NPC's respawn timer elapsed; it is alive again at its spawn point (re-render it)
		LootExpired, // a loot bag's lifetime elapsed (despawn it for nearby players, then remove it from the zone)
		NpcAttack // an aggroed NPC (Entity) swings at a player (Target); the listener applies damage + pushes vitals
	}

	// `Target` is the attacked player for NpcAttack; null otherwise.
	public readonly record struct WorldTickEffect(WorldEntity Entity, WorldTickEffectType Type, WorldEntity Target = null);
}
