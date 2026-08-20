using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// c2s PvPMatchMakingPutIntoMatchMaking (0x2052): the player presses "sign up" in the minigame window. The
	// client's own signature is
	//   PvPMatchMakingPutIntoMatchMaking(ID32::InstanceType i_nMap, int i_nType, bool i_bBringTeam, int i_QueueType)
	// [V1: docs/re/aoc-client-rpc-methods.json], and the captured body is exactly that - 13 bytes, 4 + 4 + 1 + 4:
	//
	//   [u32 i_nMap][u32 i_nType][u8 i_bBringTeam][u32 i_QueueType]
	//
	// `ID32::InstanceType` is the INSTANCE word alone, not the two-word ID32 pair, which is why the map is one u32.
	//
	// ⭐ Both leading words resolve outside this message. `i_nMap` is a PLAYFIELD id: the eight captured signups
	// carry 5006 / 5201 / 5012 / 5005, which are `The Call of Jhebbal Sag` / `Blood Ravine` / `Lost Temple` /
	// `Hallowed Vaults` in our own Maps table - harvested from a file that knows nothing about this message.
	// `i_nType` is the minigame OBJECTIVE id from LDB 802: the capture's annotations name the signups
	// ("signed up for conquest pvp", "signed up for capture the skull"), and type 4 is that LDB's `Conquest` while
	// type 5 is its `Capture The Skull`.
	//
	// ⚠️ `(0, 0)` is not a map at all - it is the window's "join first available" checkbox, annotated as
	// "selected join first avaialble instead". Retail counts those into the board's `NumberOfAnyApplies` rather
	// than listing them under a type. See PvpMatchMakingInfoCodec.
	//
	// `i_bBringTeam` is 0 in every captured signup (the capture was solo). Gitea #882.
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
