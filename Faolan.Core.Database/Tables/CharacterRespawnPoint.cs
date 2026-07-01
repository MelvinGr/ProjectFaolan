using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A respawn point a character has DISCOVERED (one row per character+point). Always-known points need no row.
	public class CharacterRespawnPoint : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		[ForeignKey(nameof(RespawnPointId))]
		public virtual RespawnPoint RespawnPoint { get; set; }

		public uint CharacterId { get; set; }

		public uint RespawnPointId { get; set; }
	}
}
