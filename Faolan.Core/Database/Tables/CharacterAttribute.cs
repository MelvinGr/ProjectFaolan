using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One of a character's attributes/derived stats - the values in the spawn's IIR attribute scaffold (the sparse
	// [statId][value] block before the ProtoCoreCharacterIIR). Modeled as structured rows so the scaffold can be
	// generated from the DB instead of copied from spawn-player-template.bin. See
	// docs/player-spawn-generation-plan.md / [[CharacterSkill]].
	public class CharacterAttribute : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint CharacterId { get; set; }

		// The attribute / stat id (the IIR stat key).
		public uint StatId { get; set; }

		// Its value (some are raw ints, some are float-encoded - kept as the raw 32-bit wire value).
		public uint Value { get; set; }
	}
}
