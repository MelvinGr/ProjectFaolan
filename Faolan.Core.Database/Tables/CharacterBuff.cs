using System;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A self-buff a character is carrying, persisted so it survives a relog WITH its remaining time (#274). One row
	// per character+buff. This is SAVEGAME state (like Character.Position/MountId), not catalog data: the importer
	// never touches it - PlayerPersistenceService snapshots the live WorldEntity.ActiveBuffs on logout/autosave and
	// RestoreBuffs re-applies them at world entry.
	//
	// Only SELF-buffs are persisted (CasterHandle == the player): NPC-cast debuffs carry a session-volatile NPC
	// handle and a DoT that shouldn't resume across a relog. Buffs owned by a dedicated re-applier (Shield of the
	// Risen, the mount ride buff) are excluded at save time so they aren't double-applied on entry.
	//
	// The wire packet is CONSTRUCTED on restore rather than replaying a stored blob: the base id (BuffId >> 8) plus
	// ExpiresAt are enough to rebuild the ApplySpell payload, with the duration field AND the payload's absolute expiry
	// stamp both recomputed from ExpiresAt so the client counts down the REMAINING time (#480).
	[PrimaryKey(nameof(CharacterId), nameof(BuffId))]
	public class CharacterBuff
	{
		// The applied wire buff id ((base << 8) | rank) - the same key WorldEntity.ActiveBuffs uses. The spell BASE id
		// the payload carries is BuffId >> 8.
		public uint BuffId { get; set; } // TODO: Add fk?
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// Absolute UTC expiry, so the REMAINING time survives the relog (remaining = ExpiresAt - now at restore).
		// Null = a buff with no server duration (lives until cancelled) - restored with a 0 duration field.
		public DateTimeOffset? ExpiresAt { get; set; }
	}
}
