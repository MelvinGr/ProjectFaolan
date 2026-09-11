using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One entry of the spawn scaffold's feat/ability id list: the 5-byte [u8 rank][u32 id] groups that follow the
	///     entry table on the wire. Per-character progression data; fresh characters carry around 20 ids, geared ones
	///     40 or more.
	/// </summary>
	/// <remarks>
	///     The wire grammar is documented in docs/re/player-spawn-generation.md.
	/// </remarks>
	[PrimaryKey(nameof(CharacterId), nameof(SkillId))]
	public class CharacterSkill
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		/// <summary>The feat/ability id. Wire order is ascending <see cref="SkillId" />.</summary>
		public uint SkillId { get; set; }

		/// <summary>
		///     The per-id rank/slot byte: 0 on fresh characters, while geared captures carry values like 0x08 and
		///     0x41-0x4B.
		/// </summary>
		public byte Rank { get; set; }
	}
}
