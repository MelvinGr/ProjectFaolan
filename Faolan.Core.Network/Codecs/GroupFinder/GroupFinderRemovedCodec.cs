using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	/// <summary>
	///     s2c <c>DispatchGroupFinderRemoved</c> - "you are out of the finder", with a localized reason. The client's
	///     argument list is <c>(reason, silent)</c>, and the captured body is exactly that:
	///     <c>[u16 len][reason - a remoteformat token, Latin1][u8 silent]</c>.
	/// </summary>
	/// <remarks>
	///     It is the most-sent group-finder message in the corpus (140 frames over 133 captures) and carries two
	///     different tokens, both category 100:
	///     <list type="bullet">
	///         <item>
	///             <c>GroupFinderCharacterLeftPlayfield</c>, pushed on the CsPlayerAgent Authenticate handshake at
	///             every login and again on leaving an activity's instance. 141 bytes.
	///         </item>
	///         <item>
	///             <c>GroupFinderLeaderDequeued</c>, the answer to c2s <c>LeaveGroupFinder</c> ~0.3 s later, 1:1 in all
	///             three captured withdrawals. 130 bytes.
	///         </item>
	///     </list>
	///     <para>
	///         The <c>key</c> attribute is the LDB entry's deterministic content hash rather than a nonce (see
	///         <c>QuestTextTokens</c>) - identical across characters, sessions and captures - so the captured spelling
	///         of each token is harvested data and is what goes back out, entity escapes and all.
	///     </para>
	///     <para>
	///         <c>silent</c> is 1 on the first login push and 0 on every other captured frame, including all three
	///         dequeues. What makes the login one different is not recoverable from three values.
	///         [V0: DispatchGroupFinderRemoved's silent flag]
	///     </para>
	/// </remarks>
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

		// Canonical: the only method here that writes bytes.
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
	///     rather than <c>Build</c> overloads: both take the same argument list, so choosing between them by signature
	///     would be ambiguous.
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

		/// <summary>The login / instance-exit push. <c>silent</c> is 1 on the login one, which is where retail sends it.</summary>
		public static GroupFinderRemoved CharacterLeftPlayfield(bool silent)
		{
			return new GroupFinderRemoved(
				QuestTextTokens.RemoteFormatToken(GroupFinderRemovedCodec.CharacterLeftPlayfield, GroupFinderRemovedCodec.ReasonCategory,
					GroupFinderRemovedCodec.CharacterLeftPlayfieldKey), silent);
		}
	}
}
