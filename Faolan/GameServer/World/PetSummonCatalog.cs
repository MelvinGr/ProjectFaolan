using System.Collections.Generic;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// Maps a SUMMON-PET spell id (the id the client casts via ActionType.CastSpell) to the pet it produces — the
	// pet counterpart of MountCatalog (#307). When a cast's spell id is in here, the server summons the pet
	// (spawn the creature + PetIIR owner-assign + follow) exactly like the retail necro-pet flow
	// (docs/summon-pet-decode.md). Spell ids + the (combat/social) kind are decoded from the chat-annotated capture;
	// the pet TEMPLATE name must resolve to a spawnable NpcSpawns row (the remaining data task — the pet-creature
	// templates are in the capture but not yet imported as summonable NPCs).
	public sealed record PetSummon(uint SummonSpellId, string PetTemplate, bool Combat)
	{
		public (uint F1, uint F2, uint F3) Kind => Combat ? PetIirCodec.CombatKind : PetIirCodec.SocialKind;
	}

	public static class PetSummonCatalog
	{
		// Confirmed from NecroL80_..._casting-with-chat-notes.pcapng: necro Deathless Acolyte (combat) 0x5E2F5,
		// generic companion "Pleasurable Company" (social) 0x4105E9. Add more as captured.
		private static readonly Dictionary<uint, PetSummon> BySpell = new()
		{
			[0x0005E2F5] = new PetSummon(0x0005E2F5, "Deathless Acolyte", Combat: true),
			[0x004105E9] = new PetSummon(0x004105E9, "Pleasurable Company", Combat: false),
		};

		public static PetSummon ForSpell(uint spellId) => BySpell.GetValueOrDefault(spellId);

		public static bool IsPetSummon(uint spellId) => BySpell.ContainsKey(spellId);
	}
}
