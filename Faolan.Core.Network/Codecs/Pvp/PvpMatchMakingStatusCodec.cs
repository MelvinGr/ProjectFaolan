using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     s2c <c>DispatchPvPMatchMakingYouAreInMatchMaking</c> - the acknowledgement of a
	///     <c>PvPMatchMakingPutIntoMatchMaking</c>: "you are queued for this one now".
	/// </summary>
	/// <remarks>
	///     The client's recovered field list is <c>(i_nMap, i_nType, i_ShowFifo, i_AsTeamMember, i_queueType)</c>
	///     [V1: docs/re/aoc-client-rpc-methods.json], and the captured body is 14 bytes - 4 + 4 + 1 + 1 + 4 and nothing
	///     else.
	///     <para>
	///         The three u32 fields echo the signup that caused it: all eight captured pairs match their request word
	///         for word, including the <c>(0, 0)</c> "join first available" one. What the server adds is the two bytes.
	///         <c>i_ShowFifo</c> is 1 in all eight and is the "show it in the FIFO" flag the matchmaking messages carry
	///         throughout - the client's on-screen feedback line. <c>i_AsTeamMember</c> is 0 in all eight, which is
	///         what a solo signup means: the capture never set <c>i_bBringTeam</c>, so the corpus shows only this side
	///         of the flag. [V0: i_AsTeamMember with a team]
	///     </para>
	/// </remarks>
	public static class PvpMatchMakingStatusCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouAreInMatchMaking;

		public static byte[] Build(PvpMatchMakingStatus value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(value.MapId)
				.WriteUInt32(value.MinigameType)
				.WriteByte(value.ShowFifo ? (byte)1 : (byte)0)
				.WriteByte(value.AsTeamMember ? (byte)1 : (byte)0)
				.WriteUInt32(value.QueueType);

			return s.ToArray();
		}

		/// <summary>The acknowledgement retail sends for a signup: the request echoed back with the two flags.</summary>
		public static byte[] Build(PvpMatchMakingSignup signup, bool asTeamMember)
		{
			ArgumentNullException.ThrowIfNull(signup);

			return Build(new PvpMatchMakingStatus(signup.MapId, signup.MinigameType, true, asTeamMember, signup.QueueType));
		}

		public static PvpMatchMakingStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PvpMatchMakingStatusCodec));
			var mapId = r.ReadU32Be();
			var minigameType = r.ReadU32Be();
			var showFifo = r.ReadU8() != 0;
			var asTeamMember = r.ReadU8() != 0;
			var queueType = r.ReadU32Be();
			r.EnsureAtEnd();

			return new PvpMatchMakingStatus(mapId, minigameType, showFifo, asTeamMember, queueType);
		}
	}

	/// <summary>"You are in matchmaking for this one", as the client's own field list names it.</summary>
	public sealed record PvpMatchMakingStatus(uint MapId, uint MinigameType, bool ShowFifo, bool AsTeamMember, uint QueueType);
}
