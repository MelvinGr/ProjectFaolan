using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     c2s <c>ScryMessageBoxAnswer</c> - the answer to the scry confirmation box
	///     <see cref="ScryShowMessageBoxCodec" /> raises. A pure echo plus the chosen option:
	///     <c>[u32 answer][u32 scriptThreadCtxId][u64 ctxCookie]</c>.
	/// </summary>
	/// <remarks>
	///     The ctxId and cookie are the correlation handle - whatever a sender puts in comes straight back, which is what
	///     lets a pending action be resumed without server-side script threads.
	///     <para>
	///         <c>Answer</c> is the option index, not a boolean. The chat-annotated corpus carries a 2, and it is not a
	///         decline: the same box (<c>localized id="1004"</c>, "Do you want to travel to the Spider Caverns?") is
	///         answered 1 on the run the player annotates "energed normal instance" and 2 thirty-four seconds later on
	///         "enered epic instance", and both are followed by the redirect and <c>Authenticate2</c> that enters the
	///         zone. A dungeon's difficulty mode is therefore which button of the travel box the player presses, and the
	///         server's answer to that choice is the <c>PlayfieldProxy</c> it sends back, which carries the mode in its
	///         class word (<c>PlayfieldInstanceOwnerTests</c>).
	///         [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json, 19:56:41 and 19:57:15]
	///     </para>
	/// </remarks>
	public static class ScryMessageBoxAnswerCodec
	{
		// The first option - "yes" on a plain travel box, and normal mode on a box that offers difficulties.
		public const uint Confirm = 1;

		// The second option: epic mode on a difficulty box. Named rather than inlined because the only thing separating it
		// from `Confirm` is which button the player pressed, not whether they confirmed.
		public const uint ConfirmSecondOption = 2;

		// The decline, and it is a value rather than "anything that is not 1". No capture holds one - the player always
		// went - so this is the one part of the field that is inferred, and it is inferred the safe way round: an unknown
		// option travels, since boxes built here offer exactly one destination, and only an explicit 0 cancels. Treating
		// "not 1" as a decline would drop retail's own epic-mode answer.
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
