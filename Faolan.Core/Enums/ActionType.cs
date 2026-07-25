namespace Faolan.Core.Enums
{
	public enum ActionType : uint
	{
		CastSpell = 0x4,
		Emote = 0x5,

		// A self-ANIMATION the movement record cannot express, with the animation itself named by the args' f1
		// (bcc_action_id, the client's animation-action table). Seen for a MOUNTED jump (live, 2026-07-25: f1 249528),
		// a jump off a bridge (NecroL80 ...falldamage_bridge-toast fr1318: 249527) and 249555 elsewhere - all in one id
		// cluster. A plain unmounted jump does NOT use it: that is pure CharDCMove with MoveFlag.Airborne, which is why
		// it relays without this (xxx_2clients_jumping carries no c2s BCC at all). Retail relays it to viewers: the
		// same capture shows s2c form-4 type-7 records naming OTHER players' handles (0x131830B, f1 249527).
		PlayAnimation = 0x7,
		NormalAttack = 0xf, // melee swing / auto-attack behavior (BehaviorControlCenter); the dominant combat action
		UnarmedAttack = 0x11, // unarmed swing (empty main hand); ability id rides the tail, ack = plain behavior echo (combat capture 2026-06-12)
		MoveEquippedToFromInventory = 0x9,
		SwitchWeaponsButton = 0x2e,
		RightClickItem = 0x0e,
		SummonMount = 0xa,
		UseObject = 0x12, // startobjectaction(18): use a world object (e.g. open a loot bag / chest)
		// Interacting with a CHARACTER, and the target decides what it means: an NPC opens its conversation (or its shop
		// window), a PLAYER is a trade request (#101 - capture login_2-clients_group-trade-friend f1251 -> f1260, where
		// the same 0x16 at another player produces DispatchRequestTrade). Named TalkToNpc until the trade decode showed
		// the NPC case is only half of it.
		InteractWithCharacter = 0x16
	}
}
