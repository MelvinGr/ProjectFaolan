using System.Threading;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The <c>field</c> word of a <see cref="GameActionData" /> element. Not a codec but the live-value policy for one
	///     field, in the same family as RemoteFormatSession and <see cref="GameClock" />: a codec writes whatever it is
	///     handed, and this is where the value comes from.
	/// </summary>
	/// <remarks>
	///     It is the server's own unique-instance counter, and the client names it: GameActionData's typeId constructor
	///     (FUN_00898780) fills the object's +0x10 from its parameter and +0x14 from the generator FUN_00898ba0, whose
	///     whole body is <c>++s_nInstance</c> guarded by an assert citing GameActionData.cpp:75 - "The unique id counter in
	///     GameActionData has wrapped and might now collide with existing ids." So the field is
	///     GameActionData::s_nInstance, a per-process allocation counter: not content, not a per-trip value, and not
	///     derivable from anything the action is about.
	///     <para>
	///         The corpus agrees: across all 120 captures the 187 elements carrying a non-zero field form 40 series, one per
	///         (capture, server port), and are monotonically increasing in every one, with no violations - including two
	///         trips in one capture that read as out of order until the source port is taken into account, because two
	///         different game servers issued them.
	///     </para>
	///     <para>
	///         Zero is not an instance id: the default constructor (FUN_008985c0) leaves +0x14 at 0, so a template-loaded
	///         action carries 0 and every server-constructed element carries a counter value. The corpus splits exactly that
	///         way, and a second field shows the same split - all 1,558 elements with <c>field == 0</c> carry
	///         <c>0x2f5 == 9</c>, and all 187 with a non-zero field carry <c>0x2f5 == 0</c>.
	///     </para>
	/// </remarks>
	public static class GameActionInstanceId
	{
		private static int _next;

		/// <summary>
		///     The next unique id, mirroring the client's <c>++s_nInstance</c>. It skips 0 on wrap for the same reason retail
		///     asserts on it: 0 means not assigned, so handing it out would make a constructed action read as a
		///     template-loaded one.
		/// </summary>
		public static uint Next()
		{
			var value = (uint)Interlocked.Increment(ref _next);
			return value == 0
				? (uint)Interlocked.Increment(ref _next)
				: value;
		}
	}
}
