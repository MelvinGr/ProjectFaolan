using System;
using System.Buffers.Binary;

namespace Faolan.GameServer.World
{
	// One active buff/effect on a world entity (see WorldEntity.ActiveBuffs). CasterHandle is who applied it (the
	// entity itself for a self-buff, the NPC for an NPC-cast debuff); the tick uses it as the removal twin's
	// applyFrom. DurationSeconds null = lives until cancelled or the session ends, a value = the tick expires it
	// (and sends the retail end-twin removal). Durations come from the ApplySpell payload (offset +8, ms) or the
	// spell's RDB DurationMs (#274).
	public sealed record ActiveBuff(uint BuffId, uint CasterHandle, DateTimeOffset AppliedAt, float? DurationSeconds)
	{
		public DateTimeOffset LastDotTick { get; set; }

		// The absolute instant this buff expires, so its remaining time can be persisted across a relog. Null = it has
		// no server duration (lives until cancelled).
		public DateTimeOffset? ExpiresAt => DurationSeconds is { } d
			? AppliedAt.AddSeconds(d)
			: null;

		// Damage-over-time per tick on the target's HealthCurrent (from Spell.DotDelta, captured StatDamageIIR):
		// negative = a bleed/poison debuff, positive = a heal-over-time, 0 = no DoT. DotType is the +45 damage-school
		// flag carried into the rebuilt packet. LastDotTick is mutable tick bookkeeping (the world tick stamps it). #274
		public int DotDelta { get; init; }
		public uint DotType { get; init; }

		// The bit needed to reconstruct this buff's ApplySpell payload on relog (#274): the per-application effect
		// Nonce (last u32 of the payload). Set at apply from the constructed payload; persisted to CharacterBuff for a
		// self-buff. Default 0 for the NPC-cast debuffs (which are never persisted).
		public uint Nonce { get; init; }

		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is { } d && (now - AppliedAt).TotalSeconds >= d;
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

		// The per-application effect nonce carried at the END of an ApplySpell payload (the last big-endian u32 -
		// offset 50 in the 54-byte buff-data form). Returns 0 for a payload too short to carry one (e.g. the empty
		// removal twin). (#274)
		public static uint NonceFromPayload(byte[] payload)
		{
			return payload is { Length: >= 4 }
				? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(payload.Length - 4, 4))
				: 0;
		}
	}
}
