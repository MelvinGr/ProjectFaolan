using System;
using System.Buffers.Binary;

namespace Faolan.GameServer.World
{
	// One active buff/effect on a world entity (see WorldEntity.ActiveBuffs). CasterHandle is who applied it (the
	// entity itself for a self-buff, the NPC for an NPC-cast debuff); the tick uses it as the removal twin's
	// applyFrom. DurationSeconds null = lives until cancelled or the session ends, a value = the tick expires it
	// (and sends the retail end-twin removal). Durations come from the ApplySpell payload (offset +8, ms) or the
	// spell's RDB DurationMs (#274).
	public sealed record ActiveBuff(uint BuffId, uint CasterHandle, DateTimeOffset AppliedAt, double? DurationSeconds)
	{
		public DateTimeOffset LastDotTick { get; set; }

		// Damage-over-time per tick on the target's HealthCurrent (from Spell.DotDelta, captured StatDamageIIR):
		// negative = a bleed/poison debuff, positive = a heal-over-time, 0 = no DoT. DotType is the +45 damage-school
		// flag carried into the rebuilt packet. LastDotTick is mutable tick bookkeeping (the world tick stamps it). #274
		public int DotDelta { get; init; }
		public uint DotType { get; init; }

		// The bits needed to reconstruct this buff's ApplySpell payload on relog (#274): the per-application effect
		// Nonce (last u32 of the payload) and whether it used the 54-byte LONG form (spellweave) vs the 50-byte short
		// form. Set at apply from the constructed payload; persisted to CharacterBuff for a self-buff. Default 0/false
		// for the NPC-cast debuffs (which are never persisted).
		public uint Nonce { get; init; }
		public bool IsLongForm { get; init; }

		// The absolute instant this buff expires, so its remaining time can be persisted across a relog. Null = it has
		// no server duration (lives until cancelled).
		public DateTimeOffset? ExpiresAt => DurationSeconds is double d ? AppliedAt.AddSeconds(d) : null;

		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is double d && (now - AppliedAt).TotalSeconds >= d;
		}

		// The per-application effect nonce carried at the END of an ApplySpell payload (the last big-endian u32 -
		// offset 46 in the 50-byte short form, 50 in the 54-byte long form). Returns 0 for a payload too short to
		// carry one (e.g. the empty removal twin). (#274)
		public static uint NonceFromPayload(byte[] payload)
		{
			return payload is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(payload.Length - 4, 4)) : 0;
		}

		// True for the 54-byte LONG buff-data form (spellweave/mount/shield), false for the 50-byte short form
		// (ordinary self-buffs) - the client rejects the wrong shape, so it must survive a relog. (#274)
		public static bool IsLongFormPayload(byte[] payload)
		{
			return payload is { Length: >= 54 };
		}

		// The buff duration encoded in an ApplySpell (0x6B87E316) effect payload: a u32 big-endian at offset +8, in
		// CENTISECONDS (hundredths of a second - the same ×10ms unit as the RDB cast-time field). Calibrated in-client:
		// Rend 700 -> 7s, Butcher 400 -> 4s, Poacher-Marksman 600 -> 6s, Exhausted 10000 -> 100s. Returned here in
		// MILLISECONDS (×10) so callers stay in ms. Returns 0 when the payload is too short / carries no duration. (#274)
		public static uint DurationMsFromPayload(byte[] payload)
		{
			return payload is { Length: >= 12 }
				? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)) * 10
				: 0;
		}
	}
}
