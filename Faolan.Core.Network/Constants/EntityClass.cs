namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     In-world entity object-class ids: the <c>type</c> half of an entity id <c>{type, instance}</c>
	///     (<c>ProtoID32</c>, or the type field of a <c>ProtoEndpoint</c>). Every IIR record that targets an entity
	///     starts <c>[u32 type][u32 handle]</c>, where <c>type</c> is one of these and <c>handle</c> is the live
	///     instance.
	/// </summary>
	/// <remarks>
	///     This is a separate id namespace from the RPC <i>conversation</i> class ids in <see cref="Endpoints" />,
	///     which are BKDR-31 hashes (0xA9ECB95D = hash("GameCharAgent")). These object-class ids are a small
	///     enumerated registry based at 50000.
	///     <para>
	///         Each name below is the pairing <c>--report entity-classes</c> shows: it prints the class word each
	///         dynel-family IIR actually carries in the capture corpus.
	///     </para>
	///     <para>
	///         <see cref="Character" /> (50000) is by far the most common - players, NPCs and creatures are all
	///         "Characters". Confirmed in the client: <c>MoveTaskIIR_c::PollStatus</c> checks
	///         <c>id.type != 50000</c> and logs "id is not of a character!". The world-object classes cluster just
	///         above it (51016, 51320-51325). See docs/entity-class-ids.md.
	///     </para>
	/// </remarks>
	public static class EntityClass
	{
		public const uint Character = 0x0000C350; // 50000 - players / NPCs / creatures (the n3Char/creature class)
		public const uint ClimbingDynel = 0x0000C87D; // 51325 - climbing dynels (1FD6412B)
		public const uint Destructible = 0x0000C879; // 51321 - destructible objects (6BD56736)
		public const uint Guild = 0x0000DEB7; // 57015 - player guilds (the ID32 a guild is addressed by)
		public const uint LootBag = 0x0000C87A; // 51322 - loot bags / containers (F096251E)
		public const uint Shop = 0x0000C75B; // 51035 - vendor shop containers (the ID32 type a shop's content is addressed by)
		public const uint SimpleDynel = 0x0000C878; // 51320 - static decor / props (66AEDD50)
		public const uint Team = 0x0000DEA9; // 57001 - player teams/groups (the ID32 a team is addressed by)
		public const uint TombStone = 0x0000C87B; // 51323 - a dead player's corpse marker (0D80E7C9)
		public const uint ZoneLabel = 0x0000C748; // 51016 - zone/city label objects (33A56FB0)

		// Not entity classes, despite sitting numerically nearby: 0xC79C/0xC79E/0xC7A0 are the resurrection-spot and
		// playfield-proxy type words, which are per-packet rather than per-entity (the same spawn point appears under
		// two of them in different frames - see ResurrectionSpotsBody), and 0x9C50 = 40016 is below the 50000 base
		// entirely. All three stay local to their codec until they are decoded.
	}
}
