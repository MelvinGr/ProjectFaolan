namespace Faolan.Core.Network.Constants
{
	// In-world ENTITY object-class ids (the `type` half of an entity id `{type, instance}` = `ProtoID32` / the
	// type field of a `ProtoEndpoint`). Every IIR record that targets an entity starts `[u32 type][u32 handle]`,
	// where `type` is one of these and `handle` is the live instance. This is a SEPARATE id namespace from the RPC
	// *conversation* class ids in `Faolan.Core.Network.Constants.Endpoints` (those are BKDR-31 hashes like
	// 0xA9ECB95D = hash("GameCharAgent")); these object-class ids are a small ENUMERATED registry based at 50000.
	//
	// CHARACTER (0xC350 = 50000) is by far the most common - players, NPCs and creatures are all "Characters", which
	// is why 0xC350 is all over the wire. Confirmed by Ghidra (MoveTaskIIR_c::PollStatus checks `id.type != 50000`
	// and logs "id is not of a character!"). The world-object classes cluster just above it (51016, 51320-51325).
	// See docs/entity-class-ids.md.
	public static class EntityClass
	{
		public const uint Character = 0x0000C350; // 50000 - players / NPCs / creatures (the n3Char/creature class)
		public const uint ClimbingDynel = 0x0000C87D; // 51325 - climbing dynels (1FD6412B)
		public const uint Destructible = 0x0000C879; // 51321 - destructible objects (6BD56736)
		public const uint Guild = 0x0000DEB7; // 57015 - player guilds (the ID32 a guild is addressed by)
		public const uint LootBag = 0x0000C87A; // 51322 - loot bags / containers (F096251E)
		public const uint Shop = 0x0000C75B; // 50011 - vendor shop containers (the ID32 type a shop's content is addressed by)
		public const uint SimpleDynel = 0x0000C878; // 51320 - static decor / props (66AEDD50)
		public const uint Team = 0x0000DEA9; // 57001 - player teams/groups (the ID32 a team is addressed by)
		public const uint ZoneLabel = 0x0000C748; // 51016 - zone/city label objects (33A56FB0)

		// NOT entity classes, despite sitting numerically nearby - listed so the next reader does not add them:
		// 0xC79C/0xC79E/0xC7A0 are the resurrection-spot + playfield-proxy type words, which are per-PACKET rather
		// than per-entity (the same spawn point appears under two of them in different frames - see
		// ResurrectionSpotsBody), and 0x9C50 = 40016 is below the 50000 base entirely. All three stay local to their
		// codec until they are decoded.
	}
}
