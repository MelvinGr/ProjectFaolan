using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// c2s SetSocialStatus (0x002A) - the player telling the CHAT tier what to show beside their name on everyone's
	// friend list. Body is one u32: `[u32 SocialStatus]` (#892).
	//
	// Three captured bodies, all in `NecroL80_..._annotated_9`: `00000002`, `00000001`, `00000002`, each ~50
	// microseconds after the matching `SetLookingForTeamStatus` on the CS tier - see SocialStatus for why that
	// pairing, the client's friend-list icon table and its own `PresenceCategory` switch together name the values.
	//
	// A u32 for a value the friend record carries in ONE byte is the client's choice, not a mistake: the c2s form is
	// an enum on the stream, and FriendStateCodec's byte is the same enum packed into a record. Both are gated.
	public static class SocialStatusCodec
	{
		public const ushort Opcode = (ushort)ChatServerOpcodes.SetSocialStatus;

		public static byte[] Build(SocialStatus value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value);

			return s.ToArray();
		}

		// ⚠️ Strict on the WIDTH, tolerant on the VALUE: an unknown status is returned as-is rather than thrown on,
		// because the client's own renderer treats everything outside 1..3 as offline instead of rejecting it. A
		// status we have never seen is a value we do not know, not a body we failed to read.
		public static SocialStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SocialStatusCodec));
			var status = (SocialStatus)r.ReadU32Be();
			r.EnsureAtEnd();

			return status;
		}
	}
}
