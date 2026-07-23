using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Interfaces
{
	public interface IMessageFramer
	{
		// Feeds one recv's bytes to the framer and returns whatever COMPLETE frames they finished. A window (span), not
		// an array + count: the receive loop hands over a slice of its reusable buffer, so nothing has to be copied to
		// be framed, and a framer can't read past what the recv actually delivered. The result is a materialized list,
		// never a lazy sequence over the window - the caller awaits between frames, by which time it is gone.
		IEnumerable<byte[]> PushBytes(ReadOnlySpan<byte> bytes);
	}
}
