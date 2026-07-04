using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One-to-one spawn-scaffold state that is not row-shaped: the raw pre-IIR frame (the near-constant skeleton
	// with per-character value islands between the spawn transform and the CharacterIIR; 228/239/339-byte
	// variants) and the per-character u32 that follows the entry-table count. Captured characters get these
	// extracted from their spawn; fresh characters get the fresh-character defaults
	// (PlayerSpawnScaffoldRows.FreshPreFrame). See docs/archive/player-spawn-generation-plan.md "VALIDATED GRAMMAR".
	[PrimaryKey(nameof(CharacterId))]
	public class CharacterSpawnFrame
	{
		public virtual Character Character { get; set; }

		// The raw pre-IIR frame bytes.
		public byte[] PreFrame { get; set; }
		public uint CharacterId { get; set; }

		// The u32 after the entry-table count (Strie 0xB7, necrol80 0x2B6, editor preview 0x3F1).
		public uint EntryTableX { get; set; }
	}
}
