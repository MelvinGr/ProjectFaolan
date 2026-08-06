using System.Threading;

namespace Faolan.Core.Network.Codecs.World
{
	// The `field` word of a GameActionData element (#636). NOT a codec - it is the live-value policy for one field, in
	// the same family as `RemoteFormatSession` and `GameClock`: a codec writes whatever it is handed, and this is where
	// the value comes from.
	//
	// It is the SERVER's own unique-instance counter, and the client says so in its own words. `GameActionData`'s
	// typeId ctor (FUN_00898780) fills the object's +0x10 from its parameter and +0x14 from a generator,
	// FUN_00898ba0, whose whole body is `++s_nInstance` guarded by:
	//
	//     "The unique id counter in GameActionData has wrapped and might now collide with existing ids. This can be
	//      very critical if it happens! This code should be fixed!"     s_nInstance!=0, GameActionData.cpp:75
	//
	// So the field is `GameActionData::s_nInstance` - a per-PROCESS allocation counter, not content, not a per-trip
	// value, and not derivable from anything the action is about. The corpus agrees exactly: across all 120 captures
	// the 187 elements carrying a non-zero field form 40 (capture, server port) series and are monotonically
	// increasing in EVERY one of them, with zero violations - including the two wagoner trips in one capture that read
	// as out of order until the source port is taken into account, because they were issued by two different game
	// servers. [V2: the harvest is reproduced by the per-port monotonicity scan written up on #636]
	//
	// ⚠️ ZERO is not an instance id. The default ctor (FUN_008985c0) leaves +0x14 at 0, so a template-loaded action
	// carries 0 and every element that the server CONSTRUCTED carries a counter value. The corpus splits exactly that
	// way, and the split is visible in a second field too: all 1,558 elements with `field == 0` carry `0x2f5 == 9`,
	// and all 187 with a non-zero field carry `0x2f5 == 0`. Never hand out 0 here.
	public static class GameActionInstanceId
	{
		private static int _next;

		// The next unique id, mirroring the client's `++s_nInstance`. Skips 0 on wrap for the same reason retail
		// asserts on it: 0 means "not assigned", so handing it out would make a constructed action read as a
		// template-loaded one.
		public static uint Next()
		{
			var value = (uint)Interlocked.Increment(ref _next);
			return value == 0
				? (uint)Interlocked.Increment(ref _next)
				: value;
		}
	}
}
