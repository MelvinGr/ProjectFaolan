using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     c2s <c>PvPMatchMakingPutIntoMatchMaking(ID32::InstanceType i_nMap, int i_nType, bool i_bBringTeam, int
	///     i_QueueType)</c> - the player presses "sign up" in the minigame window.
	/// </summary>
	/// <remarks>
	///     The captured body is exactly the parameter list [V1: docs/re/aoc-client-rpc-methods.json]: 13 bytes,
	///     <c>[u32 i_nMap][u32 i_nType][u8 i_bBringTeam][u32 i_QueueType]</c>. <c>ID32::InstanceType</c> is the
	///     instance word alone rather than the two-word ID32 pair, which is why the map is one u32.
	///     <para>
	///         Both leading words resolve outside this message. <c>i_nMap</c> is a playfield id: the eight captured
	///         signups carry 5006 / 5201 / 5012 / 5005, which are The Call of Jhebbal Sag, Blood Ravine, Lost Temple
	///         and Hallowed Vaults in the Maps table. <c>i_nType</c> is the minigame objective id from LDB 802: the
	///         capture's annotations name the signups ("signed up for conquest pvp", "signed up for capture the
	///         skull"), and type 4 is that LDB's Conquest while type 5 is its Capture The Skull.
	///     </para>
	///     <para>
	///         <c>(0, 0)</c> is not a map at all: it is the window's "join first available" checkbox. Retail counts
	///         those into the board's <c>NumberOfAnyApplies</c> rather than listing them under a type; see
	///         <see cref="PvpMatchMakingInfoCodec" />. <c>i_bBringTeam</c> is 0 in every captured signup, which was
	///         solo.
	///     </para>
	/// </remarks>
	public static class PvpMatchMakingSignupCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.PvPMatchMakingPutIntoMatchMaking;

		/// <summary>The map/type pair the "join first available" checkbox sends instead of a chosen minigame.</summary>
		public const uint AnyAvailable = 0;

		public static byte[] Build(PvpMatchMakingSignup value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(value.MapId)
				.WriteUInt32(value.MinigameType)
				.WriteByte(value.BringTeam ? (byte)1 : (byte)0)
				.WriteUInt32(value.QueueType);

			return s.ToArray();
		}

		public static PvpMatchMakingSignup Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PvpMatchMakingSignupCodec));
			var mapId = r.ReadU32Be();
			var minigameType = r.ReadU32Be();
			var bringTeam = r.ReadU8() != 0;
			var queueType = r.ReadU32Be();
			r.EnsureAtEnd();

			return new PvpMatchMakingSignup(mapId, minigameType, bringTeam, queueType);
		}
	}

	/// <summary>One minigame signup, as the client's own parameter list names it.</summary>
	/// <param name="MapId">The minigame's playfield, or 0 for "join first available".</param>
	/// <param name="MinigameType">The LDB 802 objective id (4 = Conquest, 5 = Capture The Skull), or 0 for "any".</param>
	/// <param name="BringTeam">Queue the whole team rather than just this character.</param>
	/// <param name="QueueType">0 in every captured signup; the same value <c>PvPMatchMakingRequestInfo</c> asks with.</param>
	public sealed record PvpMatchMakingSignup(uint MapId, uint MinigameType, bool BringTeam, uint QueueType)
	{
		/// <summary>True when this is the "join first available" checkbox rather than a chosen minigame.</summary>
		public bool IsAnyAvailable => MapId == PvpMatchMakingSignupCodec.AnyAvailable && MinigameType == PvpMatchMakingSignupCodec.AnyAvailable;
	}
}
