using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Interfaces
{
	public interface IMessageFramer
	{
		// Feeds one recv's bytes to the framer and returns whatever COMPLETE frames they finished. A window (span), not
		// an array + count: the receive loop hands over a slice of its reusable buffer, so nothing has to be copied to
		// be framed, and a framer can't read past what the recv actually delivered.
		//
		// The result is a materialized LIST rather than an IEnumerable, and that is a correctness requirement, not a
		// preference: the caller awaits between frames, by which time the window is gone, so nothing may be enumerated
		// lazily out of it. (It also keeps a recv that completes no frame - every recv of a multi-recv burst packet -
		// free of a list and an enumerator.)
		IReadOnlyList<byte[]> PushBytes(ReadOnlySpan<byte> bytes);
	}
}
