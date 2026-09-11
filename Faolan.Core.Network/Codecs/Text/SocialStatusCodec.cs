using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// c2s SetSocialStatus - the player telling the chat tier what to show beside their name on everyone's friend
	// list. Body is one u32: `[u32 SocialStatus]`.
	//
	// Three captured bodies, all in `NecroL80_..._annotated_9`: `00000002`, `00000001`, `00000002`, each ~50
	// microseconds after the matching `SetLookingForTeamStatus` on the CS tier - see SocialStatus for why that
	// pairing, the client's friend-list icon table and its own `PresenceCategory` switch together name the values.
	//
	// The c2s form is an enum on the stream while FriendStateCodec's byte is the same enum packed into a record, so
	// the differing widths are the client's choice rather than a mistake. Both are gated.
	public static class SocialStatusCodec
	{
		public const ushort Opcode = (ushort)ChatServerOpcodes.SetSocialStatus;

		public static byte[] Build(SocialStatus value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value);

			return s.ToArray();
		}

		/// <summary>Reads the one-word body.</summary>
		/// <remarks>
		///     Strict on the width, tolerant on the value: an unknown status is returned as-is rather than thrown on,
		///     because the client's own renderer treats everything outside 1..3 as offline instead of rejecting it. A
		///     status we have never seen is a value we do not know, not a body we failed to read.
		/// </remarks>
		public static SocialStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SocialStatusCodec));
			var status = (SocialStatus)r.ReadU32Be();
			r.EnsureAtEnd();

			return status;
		}
	}
}
