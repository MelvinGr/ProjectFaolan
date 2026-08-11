using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// c2s ScryMessageBoxAnswer (0x203B) - the answer to the scry confirmation box ScryShowMessageBoxCodec raises.
	// A pure ECHO plus the chosen option: `[u32 answer][u32 scriptThreadCtxId][u64 ctxCookie]`. The ctxId + cookie
	// are the correlation handle - whatever a sender puts in comes straight back, which is what lets a pending
	// action be resumed without server-side script threads.
	//
	// ⚠️ `Answer` is the OPTION INDEX, not a boolean, and reading it as one was wrong (#511). The chat-annotated
	// corpus carries a `2`, and it is not a decline: the same box (`localized id="1004"`, *"Do you want to travel
	// to the Spider Caverns?"*) is answered `1` on the run the player annotates *"energed normal instance"* and
	// `2` 34 seconds later on *"enered epic instance"*, and BOTH are followed by the redirect + `Authenticate2`
	// that enters the zone. So a dungeon's difficulty MODE is which button of the travel box you press - and the
	// server's answer to that choice is already decoded: the `PlayfieldProxy` it sends back carries the mode in its
	// class word (`0xC79C` normal against `0xCABC` on that same epic entry, `PlayfieldInstanceOwnerTests`). Option
	// index in, class word back.
	// [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json, 19:56:41 and 19:57:15]
	public static class ScryMessageBoxAnswerCodec
	{
		// The first option - "yes" on a plain travel box, and NORMAL mode on a box that offers difficulties.
		public const uint Confirm = 1;

		// The second option: EPIC on a difficulty box. Named rather than inlined because the only thing that
		// separates it from `Confirm` is which button the player pressed, not whether they confirmed.
		public const uint ConfirmSecondOption = 2;

		// ⚠️ The DECLINE, and it is a value rather than "anything that is not 1". No capture holds one - the
		// player always went - so this is the one part of the field that is inferred, and it is inferred the safe
		// way round: an unknown option travels (our boxes offer exactly one destination), only an explicit 0
		// cancels. Treating "not 1" as a decline would have dropped retail's own epic-mode answer.
		public const uint Decline = 0;
		public const uint Method = (uint)GameCharAgentMethods.ScryMessageBoxAnswer;

		public static byte[] Build(ScryMessageBoxAnswer value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.Answer).WriteUInt32(value.ThreadCtxId).WriteUInt64(value.Cookie);

			return s.ToArray();
		}

		public static ScryMessageBoxAnswer Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ScryMessageBoxAnswerCodec));
			var value = new ScryMessageBoxAnswer(r.ReadU32Be(), r.ReadU32Be(), r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record ScryMessageBoxAnswer(uint Answer, uint ThreadCtxId, ulong Cookie);
}
