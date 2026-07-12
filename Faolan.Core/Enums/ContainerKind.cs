namespace Faolan.Core.Enums
{
	public enum ContainerKind : uint
	{
		Equipped = 101,
		Bag = 104,
		Bank = 105, // personal bank storage - delivered on-demand via BankIIR (protocol_bank_storage), same codec
		Resource = 112,
		Loot = 115 // dropped loot-bag inventory kind on the wire (LootBagTemplate); ops key on the bag ENTITY handle, not this id

	}
}
