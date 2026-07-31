using System;
using Faolan.Core.Network.Codecs.Combat;

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

		// This DoT's measured tick interval in ms (Spell.DotIntervalMs); 0 = unmeasured, and the tick uses its default.
		public uint DotIntervalMs { get; init; }

		// The bit needed to reconstruct this buff's ApplySpell payload on relog (#274): the per-application effect
		// Nonce (last u32 of the payload). Set at apply from the constructed payload; persisted to CharacterBuff for a
		// self-buff. Default 0 for the NPC-cast debuffs (which are never persisted).
		public uint Nonce { get; init; }

		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is { } d && (now - AppliedAt).TotalSeconds >= d;
		}

		// The buff duration carried by an ApplySpell (0x6B87E316) effect payload, in MILLISECONDS. The payload's own
		// field is in CENTISECONDS (hundredths of a second - the same ×10ms unit as the RDB cast-time field), so this
		// scales by 10 and callers stay in ms. Calibrated in-client: Rend 700 -> 7s, Butcher 400 -> 4s,
		// Poacher-Marksman 600 -> 6s, Exhausted 10000 -> 100s. (#274)
		public static uint DurationMsFromPayload(byte[] payload)
		{
			return Payload(payload)?.Duration * 10 ?? 0;
		}

		// The per-application effect nonce an ApplySpell payload carries. It is the LOW half of the record's u64
		// expiry-stamp slot, which is where our sender puts it - the `effectNonce` spelling is a misnomer the codec
		// documents and this file keeps because it is also a persisted column name. (#274)
		public static uint NonceFromPayload(byte[] payload)
		{
			return (uint)(Payload(payload)?.ExpiryStamp ?? 0);
		}

		// The APPLY payload decoded, or null when there is none to decode: the removal twin carries an EMPTY payload,
		// and the only form in traffic is the 54-byte apply (MagicBuffDataCodec). Reading it through the codec is what
		// removed the two fixed offsets (+8 and end-4) this file used to index by.
		private static MagicBuffData? Payload(byte[] payload)
		{
			return payload is { Length: MagicBuffDataCodec.Length }
				? MagicBuffDataCodec.Parse(payload)
				: null;
		}
	}
}
