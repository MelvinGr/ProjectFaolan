using System.Collections.Generic;
using Faolan.Core.Network.Codecs;

namespace Faolan.GameServer.World
{
	// Maps a SUMMON-PET spell id (the id the client casts via ActionType.CastSpell) to the pet it produces — the
	// pet counterpart of MountCatalog (#307). When a cast's spell id is in here, the server summons the pet
	// (spawn the creature + PetIIR owner-assign + follow) exactly like the retail necro-pet flow
	// (docs/summon-pet-decode.md). Spell ids + the (combat/social) kind are decoded from the chat-annotated capture.
	// The PetTemplate is a creature TYPE name (Npc.Name) - spawned from the TYPE (Npc.Data lifted stat core, #244),
	// NOT a world placement. The pet-creature types must be imported (Npc row + Data) for the summon to render.
	public sealed record PetSummon(uint SummonSpellId, string PetTemplate, bool Combat)
	{
		public (uint F1, uint F2, uint F3) Kind =>
			Combat
				? PetIirCodec.CombatKind
				: PetIirCodec.SocialKind;
	}

	public static class PetSummonCatalog
	{
		// The distinct pet creature TYPES to seed (by template name) - used by the importer's pets module.
		public static IEnumerable<PetSummon> All => BySpell.Values;

		// Confirmed from the necro captures (spell id = the CastSpell trailer; pet template = the summoned FA015807
		// creature's decoded name). The full necro pet roster is in NecroL80_..._mount-horse_all-necro-summons_dismiss-all
		// (all 12 summoned + mass-dismissed). "Pleasurable Company" is the generic SOCIAL companion (non-combat, not
		// targetable); everything else is a combat pet.
		private static readonly Dictionary<uint, PetSummon> BySpell = new Dictionary<uint, PetSummon>
		{
			[0x004105E9] = new PetSummon(0x004105E9, "Pleasurable Company", false),
			[0x0005E2F5] = new PetSummon(0x0005E2F5, "Arcanist", true),
			[0x0005E2F0] = new PetSummon(0x0005E2F0, "Magus", true),
			[0x0005E2F6] = new PetSummon(0x0005E2F6, "Reaper", true),
			[0x0005E321] = new PetSummon(0x0005E321, "Archmagus", true),
			[0x0005E322] = new PetSummon(0x0005E322, "Blood Arcanist", true),
			[0x0004F813] = new PetSummon(0x0004F813, "Corruptor", true),
			[0x0004F814] = new PetSummon(0x0004F814, "Blighted One", true),
			[0x0004F815] = new PetSummon(0x0004F815, "Necrotic Bomb", true),
			[0x0004F816] = new PetSummon(0x0004F816, "Harvester", true),
			[0x0004F817] = new PetSummon(0x0004F817, "Life-stealer", true),
			[0x00127EDD] = new PetSummon(0x00127EDD, "Mutilator", true)
		};

		public static PetSummon ForSpell(uint spellId)
		{
			return BySpell.GetValueOrDefault(spellId);
		}

		public static bool IsPetSummon(uint spellId)
		{
			return BySpell.ContainsKey(spellId);
		}
	}
}
