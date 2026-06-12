namespace Faolan.Core.Enums
{
	public enum ActionType : uint
	{
		CastSpell = 0x4,
		Emote = 0x5,
		NormalAttack = 0xf, // melee swing / auto-attack behavior (BehaviorControlCenter); the dominant combat action
		UnarmedAttack = 0x11, // unarmed swing (empty main hand); ability id rides the tail, ack = plain behavior echo (combat capture 2026-06-12)
		MoveEquippedToFromInventory = 0x9,
		SwitchWeaponsButton = 0x2e,
		RightClickItem = 0x0e,
		SummonMount = 0xa,
		UseObject = 0x12, // startobjectaction(18): use a world object (e.g. open a loot bag / chest)
		TalkToNpc = 0x16 // interacting with / talking to an NPC (opens its conversation)
	}
}
