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
		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is double d && (now - AppliedAt).TotalSeconds >= d;
		}

		// The buff duration encoded in an ApplySpell (0x6B87E316) effect payload: a u32 big-endian at offset +8, in
		// CENTISECONDS (hundredths of a second - the same ×10ms unit as the RDB cast-time field). Calibrated in-client:
		// Rend 700 -> 7s, Butcher 400 -> 4s, Poacher-Marksman 600 -> 6s, Exhausted 10000 -> 100s. Returned here in
		// MILLISECONDS (×10) so callers stay in ms. Returns 0 when the payload is too short / carries no duration. (#274)
		public static uint DurationMsFromPayload(byte[] payload)
		{
			return payload is { Length: >= 12 } ? BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)) * 10 : 0;
		}
	}
}
