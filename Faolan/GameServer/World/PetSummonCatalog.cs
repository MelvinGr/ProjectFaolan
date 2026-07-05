using System.Collections.Generic;
using Faolan.Core.Network.Packets.Opcodes;

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
		public (uint F1, uint F2, uint F3) Kind => Combat ? PetIirCodec.CombatKind : PetIirCodec.SocialKind;
	}

	public static class PetSummonCatalog
	{
		// Confirmed from NecroL80_..._casting-with-chat-notes.pcapng (spell id = the CastSpell trailer; pet template =
		// the summoned FA015807 creature's decoded name). The necro "Deathless Acolyte" is the SPELL name; the two
		// pet CREATURES it summons are named "Arcanist" (0x5E2F5) and "Magus" (0x5E2F0). "Pleasurable Company" is the
		// generic social companion (0x4105E9). Add more as captured.
		private static readonly Dictionary<uint, PetSummon> BySpell = new()
		{
			[0x0005E2F5] = new PetSummon(0x0005E2F5, "Arcanist", Combat: true),          // Deathless Acolyte: Arcanist
			[0x0005E2F0] = new PetSummon(0x0005E2F0, "Magus", Combat: true),             // Deathless Acolyte: Magnus
			[0x004105E9] = new PetSummon(0x004105E9, "Pleasurable Company", Combat: false),
		};

		public static PetSummon ForSpell(uint spellId) => BySpell.GetValueOrDefault(spellId);

		public static bool IsPetSummon(uint spellId) => BySpell.ContainsKey(spellId);

		// The distinct pet creature TYPES to seed (by template name) - used by the importer's pets module.
		public static IEnumerable<PetSummon> All => BySpell.Values;
	}
}
