using System;

namespace Faolan.GameServer.World
{
	// One active buff/effect on a world entity (see WorldEntity.ActiveBuffs). DurationSeconds is null until the
	// RDB spell definitions are imported (no capture or table carries durations today); null = lives until
	// cancelled or the session ends, a value = the tick expires it.
	public sealed record ActiveBuff(uint BuffId, uint CasterHandle, DateTimeOffset AppliedAt, double? DurationSeconds)
	{
		public bool Expired(DateTimeOffset now)
		{
			return DurationSeconds is double d && (now - AppliedAt).TotalSeconds >= d;
		}
	}
}
