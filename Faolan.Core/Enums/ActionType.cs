namespace Faolan.Core.Enums
{
	public enum ActionType : uint
	{
		CastSpell = 0x4,
		Emote = 0x5,
		NormalAttack = 0xf, // melee swing / auto-attack behavior (BehaviorControlCenter); the dominant combat action
		MoveEquippedToFromInventory = 0x9,
		SwitchWeaponsButton = 0x2e,
		RightClickItem = 0x0e,
		SummonMount = 0xa,
		TalkToNpc = 0x16 // interacting with / talking to an NPC (opens its conversation)
	}
}
