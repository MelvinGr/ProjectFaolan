using System;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     One active buff or effect on a world entity - see <see cref="WorldEntity.ActiveBuffs" />.
	/// </summary>
	/// <param name="CasterHandle">
	///     Who applied it: the entity itself for a self-buff, the NPC for an NPC-cast debuff. The tick uses it as the
	///     removal twin's applyFrom.
	/// </param>
	/// <param name="DurationSeconds">
	///     Null means the buff lives until cancelled or the session ends; a value means the tick expires it and sends
	///     retail's end-twin removal. Durations come from the ApplySpell payload or the spell's RDB DurationMs.
	/// </param>
	public sealed record ActiveBuff(uint BuffId, uint CasterHandle, DateTimeOffset AppliedAt, float? DurationSeconds)
	{
		public DateTimeOffset LastDotTick { get; set; }

		/// <summary>
		///     The absolute instant this buff expires, so its remaining time can be persisted across a relog. Null when
		///     it has no server duration and lives until cancelled.
		/// </summary>
		public DateTimeOffset? ExpiresAt => DurationSeconds is { } d
			? AppliedAt.AddSeconds(d)
			: null;

		/// <summary>
		///     Damage-over-time per tick on the target's HealthCurrent, from <c>Spell.DotDelta</c> and the captured
		///     StatDamageIIR: negative is a bleed or poison debuff, positive a heal-over-time, 0 no DoT.
		///     <see cref="DotType" /> is the +45 damage-school flag carried into the rebuilt packet, and
		///     <see cref="LastDotTick" /> is tick bookkeeping the world tick stamps.
		/// </summary>
		public int DotDelta { get; init; }
		public uint DotType { get; init; }

		/// <summary>
		///     This DoT's measured tick interval in milliseconds (<c>Spell.DotIntervalMs</c>); 0 is unmeasured, and the
		///     tick uses its own default.
		/// </summary>
		public uint DotIntervalMs { get; init; }

		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is { } d && (now - AppliedAt).TotalSeconds >= d;
		}

		/// <summary>
		///     The buff duration carried by an ApplySpell (0x6B87E316) effect payload, in milliseconds.
		/// </summary>
		/// <param name="payload">
		///     The decoded value rather than bytes, so nothing but the codec holds the encoded form. Null is the
		///     removal twin, which carries no data region at all.
		/// </param>
		/// <remarks>
		///     The payload's own field is in centiseconds - the same x10 ms unit as the RDB cast-time field - so this
		///     scales by 10 and callers stay in milliseconds. Calibrated in-client: Rend 700 to 7 s, Butcher 400 to
		///     4 s, Poacher-Marksman 600 to 6 s, Exhausted 10000 to 100 s.
		/// </remarks>
		public static uint DurationMsFromPayload(MagicBuffData? payload)
		{
			return payload?.Duration * 10 ?? 0;
		}
	}
}
