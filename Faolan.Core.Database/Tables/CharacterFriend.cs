using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One entry in a character's friend list. Keyed by the pair - a friendship is that pair and nothing else, so there
	// is no surrogate id and re-adding the same friend cannot produce a second row.
	//
	// One-sided, deliberately: the friend list has no accept handshake at all (docs/subsystems/teams-trade-friends.md,
	// stated outright in the capture's own chat - *"both friends added, friends are one sided, no accept needed"*), so
	// adding someone writes exactly this row and nothing on their side. That is also why there is no state column: there
	// is no pending/accepted/blocked to model.
	//
	// Unlike a team - which is live-only state and has no table - a friend list survives logout, so it is persisted.
	[PrimaryKey(nameof(CharacterId), nameof(FriendCharacterId))]
	public class CharacterFriend
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The befriended character. A real FK so a deleted character cannot leave a dangling entry in someone's list.
		public uint FriendCharacterId { get; set; }

		[ForeignKey(nameof(FriendCharacterId))]
		public virtual Character Friend { get; set; } = null!;
	}
}
