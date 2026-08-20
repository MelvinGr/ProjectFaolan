using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderRemoved (0x20C5): "you are out of the finder", with a localized REASON. The client's own
	// argument list is `(reason, silent)`, and the captured body is exactly that:
	//
	//   [u16 len][reason - a <remoteformat> token, Latin1][u8 silent]
	//
	// It is the most-sent group-finder message in the corpus (140 frames over 133 captures) and it carries two
	// different tokens, both category 100:
	//
	//   * `GroupFinderCharacterLeftPlayfield` - pushed on the CsPlayerAgent Authenticate handshake at every login,
	//     and again on leaving an activity's instance. 141 bytes.
	//   * `GroupFinderLeaderDequeued` - the answer to c2s `LeaveGroupFinder`, ~0.3 s later, 1:1 in all three captured
	//     withdrawals (Gitea #882's two captures). 130 bytes. THIS IS NEW: the flow shipped with "no captured s2c
	//     answer" recorded against it, on the strength of a corpus that had no dequeue in it.
	//
	// The `key` attribute is the LDB entry's DETERMINISTIC content hash, not a nonce (see QuestTextTokens) - identical
	// across characters, sessions and captures - so the captured spelling of each token is harvested data and is what
	// goes back out, entity escapes and all.
	//
	// `silent` is 1 on the first login push and 0 on every other captured frame, including all three dequeues. What
	// makes the login one different is not recoverable from three values. [V0: DispatchGroupFinderRemoved's silent flag]
	public static class GroupFinderRemovedCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderRemoved;

		/// <summary>The category every captured reason carries.</summary>
		public const uint ReasonCategory = 100;

		/// <summary>The login / instance-exit push, key exactly as retail spells it.</summary>
		public const string CharacterLeftPlayfield = "GroupFinderCharacterLeftPlayfield";

		/// <summary>The answer to <c>LeaveGroupFinder</c>.</summary>
		public const string LeaderDequeued = "GroupFinderLeaderDequeued";

		internal const string CharacterLeftPlayfieldKey = "nKY&amp;+hL^I7&#36;M@PO`z&#39;Y0";
		internal const string LeaderDequeuedKey = "8g0uI&quot;(1.i*&#92;0#7aTqx3";

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GroupFinderRemoved value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteString(value.Reason).WriteByte(value.Silent ? (byte)1 : (byte)0);

			return s.ToArray();
		}

		public static GroupFinderRemoved Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderRemovedCodec));
			var reason = r.ReadStringU16Length();
			var silent = r.ReadU8() != 0;
			r.EnsureAtEnd();

			return new GroupFinderRemoved(reason, silent);
		}
	}

	/// <summary>Why the finder dropped this character, as the client's own argument names have it.</summary>
	public sealed record GroupFinderRemoved(string Reason, bool Silent);

	/// <summary>
	///     The two reasons the corpus carries, spelled the way retail spells them. Value factories beside the codec
	///     rather than <c>Build</c> overloads: both take the same (nothing much) argument list, so choosing between
	///     them by signature is exactly the ambiguity the one-canonical-Build rule removes.
	/// </summary>
	public static class GroupFinderRemovalReasons
	{
		/// <summary>The answer to <c>LeaveGroupFinder</c> - byte-exact against all three captured withdrawals.</summary>
		public static GroupFinderRemoved LeaderDequeued()
		{
			return new GroupFinderRemoved(
				QuestTextTokens.RemoteFormatToken(GroupFinderRemovedCodec.LeaderDequeued, GroupFinderRemovedCodec.ReasonCategory, GroupFinderRemovedCodec.LeaderDequeuedKey),
				false);
		}

		/// <summary>The login / instance-exit push. `silent` is 1 on the login one, which is where retail sends it.</summary>
		public static GroupFinderRemoved CharacterLeftPlayfield(bool silent)
		{
			return new GroupFinderRemoved(
				QuestTextTokens.RemoteFormatToken(GroupFinderRemovedCodec.CharacterLeftPlayfield, GroupFinderRemovedCodec.ReasonCategory,
					GroupFinderRemovedCodec.CharacterLeftPlayfieldKey), silent);
		}
	}
}
