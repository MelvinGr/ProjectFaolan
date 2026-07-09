using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A quest-state TAG set on a character (one row per character+tag). Tags are the boolean flags the RDB Lua
	// scripts read/write (`player:HasTag(n)` / `player:AddTag(n)`) - they ARE the quest/dialogue state machine
	// (protocol_conversation_routing). Persisted so quest/conversation progress survives relog.
	[PrimaryKey(nameof(CharacterId), nameof(TagId))]
	public class CharacterTag
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint TagId { get; set; }
	}
}
