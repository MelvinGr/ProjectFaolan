namespace Faolan.Core.Enums
{
	// The `command` word of a QueuePetCommand (0x205B) packet - the pet command bar (#307, docs/summon-pet-decode.md).
	// Confirmed from the necro-pet capture (chat-annotated): Defend (0x1F6) and Dismiss (0x1F8). The other stance
	// buttons (attack / follow / stay / aggressive / defensive / passive) are the neighbouring 0x1Fx codes - add
	// them as captured.
	public enum PetCommand : uint
	{
		Defend = 0x1F6, // "defend" - the pet guards the owner
		Dismiss = 0x1F8 // dismiss the pet (server despawns it + sends PetIIR mode-1)
	}
}
