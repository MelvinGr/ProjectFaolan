using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Interfaces
{
	public interface IMessageFramer
	{
		/// <summary>
		///     Feeds one recv's bytes to the framer and returns the complete frames they finished.
		/// </summary>
		/// <param name="bytes">
		///     A window over the receive loop's reusable buffer, so nothing is copied to be framed and a framer cannot
		///     read past what the recv delivered.
		/// </param>
		/// <returns>
		///     A materialized list, which is a correctness requirement: the caller awaits between frames, by which time
		///     the window is gone, so nothing may be enumerated lazily out of it.
		/// </returns>
		IReadOnlyList<byte[]> PushBytes(ReadOnlySpan<byte> bytes);
	}
}
