using System;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A self-buff a character is carrying, persisted so it survives a relog with its remaining time. One row
	// per character+buff. This is savegame state (like Character.Position/MountId), not catalog data: the importer
	// never touches it - PlayerPersistenceService snapshots the live WorldEntity.ActiveBuffs on logout/autosave and
	// RestoreBuffs re-applies them at world entry.
	//
	// Only self-buffs are persisted (CasterHandle == the player): NPC-cast debuffs carry a session-volatile NPC
	// handle and a DoT that shouldn't resume across a relog. Buffs owned by a dedicated re-applier (Shield of the
	// Risen, the mount ride buff) are excluded at save time so they aren't double-applied on entry.
	//
	// The wire packet is constructed on restore rather than replaying a stored blob: the base id (BuffId >> 8) plus
	// ExpiresAt are enough to rebuild the ApplySpell payload, with the duration field and the payload's absolute expiry
	// stamp both recomputed from ExpiresAt so the client counts down the remaining time.
	[PrimaryKey(nameof(CharacterId), nameof(BuffId))]
	public class CharacterBuff
	{
		// The applied wire buff id ((base << 8) | rank) - the same key WorldEntity.ActiveBuffs uses. The spell base id
		// the payload carries is BuffId >> 8.
		public uint BuffId { get; set; }
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// Absolute UTC expiry, so the remaining time survives the relog (remaining = ExpiresAt - now at restore).
		// Null = a buff with no server duration (lives until cancelled) - restored with a 0 duration field.
		public DateTimeOffset? ExpiresAt { get; set; }
	}
}
