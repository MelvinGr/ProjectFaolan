using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>Locates the playfield-instance token inside a 0x737A6DF9 world-entry snapshot.</summary>
	/// <remarks>
	///     Not a codec: it reads a field out of another message's body rather than owning a message of its own, which is
	///     why it is a sibling of <see cref="N3ToClientQuitCodec" /> - its one consumer, since a despawn is scoped to the
	///     playfield instance the client learned here - rather than a method on it.
	/// </remarks>
	public static class PlayfieldTokenScan
	{
		// The markers that bracket the token inside the snapshot tail: ...000000BF <token> 0000040B - identical in the
		// seeded beach burst (token 0x004FE428) and the hoxxling gameplay capture (0x01EA985C).
		private static readonly byte[] TokenPrefixMarker = [0x00, 0x00, 0x00, 0xBF];
		private static readonly byte[] TokenSuffixMarker = [0x00, 0x00, 0x04, 0x0B];

		/// <summary>
		///     Finds the 4 bytes framed by 000000BF ... 0000040B: the prefix immediately precedes the token and the suffix
		///     immediately follows it.
		/// </summary>
		/// <returns>
		///     False when the framing is absent. The token is then unknown, and a despawn must not be sent with a guessed
		///     value.
		/// </returns>
		public static bool TryExtract(ReadOnlySpan<byte> snapshotBody, out uint token)
		{
			token = 0;
			var i = snapshotBody.IndexOf(TokenPrefixMarker);
			while (i >= 0 && i + 12 <= snapshotBody.Length)
			{
				var start = i + 4;
				if (snapshotBody.Slice(start + 4, 4).SequenceEqual(TokenSuffixMarker))
				{
					token = snapshotBody.ReadU32Be(start);
					return true;
				}

				var next = snapshotBody.Slice(i + 1).IndexOf(TokenPrefixMarker);
				i = next < 0
					? -1
					: i + 1 + next;
			}

			return false;
		}
	}
}
