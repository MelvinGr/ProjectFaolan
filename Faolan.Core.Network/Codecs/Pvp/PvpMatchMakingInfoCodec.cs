using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// s2c DispatchPvPMatchMakingInfoRequestReturn (0x2092): the minigame window's queue board - how many players are
	// signed up for each minigame right now. The answer to c2s PvPMatchMakingRequestInfo(int i_QueueType), which the
	// client polls every ~5 s while that window is open and once at login.
	//
	// The client's own recovered field list is `(i_cList, i_nNumberOfAnyApplies)`
	// [V1: docs/re/aoc-client-rpc-methods.json]. `i_cList` is a THREE-LEVEL count-framed map, each level a
	// `[u32 key]` before its element the way DispatchMassivePvpInfoRequestReturn's list is (MassivePvpInfoCodec):
	//
	//   [u32 (types + 1) * 0x3F1]
	//     types × [u32 minigameType] [u32 (maps + 1) * 0x3F1]
	//       maps × [u32 mapId] [u32 (queues + 1) * 0x3F1]
	//         queues × [u32 queueType] [u32 signups]
	//   [u32 i_nNumberOfAnyApplies]
	//
	// ⭐ THE BOARD IS LIVE STATE, AND THE CAPTURE PROVES IT BY MOVING. Until Gitea #882's capture this message was
	// on record as "captured empty" - `[0x3F1][0]` in every login dump - and the server answered with that constant.
	// It is not a constant: across 43 bodies in one session every field tracks the player's own signups, second by
	// second. Each `PvPMatchMakingPutIntoMatchMaking` raises exactly one `signups` by one and lowers the previous
	// one, because a character holds ONE signup at a time (which is also why `PvPMatchMakingRemoveFromMatchMaking`
	// needs no argument); each removal lowers it again. A map appears the first time anyone queues for it and STAYS
	// at 0 afterwards, so the board is a per-server registry seeded by activity, not a catalog - and an empty one is
	// what a server nobody has queued on sends, which is exactly the four login captures.
	//
	// ⚠️ `0x7E2` here is the ONE-ELEMENT list frame, not a constant: the innermost frame is `0x7E2` in all 43 bodies
	// because every captured signup carries `i_QueueType` 0, so each map has exactly one queue. Reading it as a
	// constant would have hard-coded the single-queue case (see the `0x3F1`/`0x7E2` trap in CLAUDE.md).
	//
	// ⚠️ The innermost KEY is 0 in every captured body, so its identity is inference rather than measurement: it is
	// read as `queueType` because that is the one field `PvPMatchMakingRequestInfo` and
	// `PvPMatchMakingPutIntoMatchMaking` both carry and both hold at 0 throughout.
	// [V0: the innermost map key of the matchmaking board]
	//
	// ⭐ `i_nNumberOfAnyApplies` is the "join first available" tally, and the capture's own annotations name it: it
	// goes 0 -> 1 on the `(map 0, type 0)` signup the player annotated "selected join first avaialble instead", and
	// back to 0 on the removal annotated "disabled join first avaialbl". Those signups are counted HERE and are
	// deliberately not listed as a type-0 group.
	public static class PvpMatchMakingInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchPvPMatchMakingInfoRequestReturn;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PvpMatchMakingInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Types.Count));
			foreach (var type in value.Types)
			{
				s.WriteUInt32(type.MinigameType).WriteUInt32(ListFrame.ForCount(type.Maps.Count));
				foreach (var map in type.Maps)
				{
					s.WriteUInt32(map.MapId).WriteUInt32(ListFrame.ForCount(map.Queues.Count));
					foreach (var queue in map.Queues)
						s.WriteUInt32(queue.QueueType).WriteUInt32(queue.Signups);
				}
			}

			s.WriteUInt32(value.NumberOfAnyApplies);

			return s.ToArray();
		}

		public static PvpMatchMakingInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PvpMatchMakingInfoCodec));

			var types = new List<PvpMatchMakingTypeBoard>();
			for (var typeIndex = r.ReadListCount(); typeIndex > 0; typeIndex--)
			{
				var minigameType = r.ReadU32Be();
				var maps = new List<PvpMatchMakingMapBoard>();
				for (var mapIndex = r.ReadListCount(); mapIndex > 0; mapIndex--)
				{
					var mapId = r.ReadU32Be();
					var queues = new List<PvpMatchMakingQueue>();
					for (var queueIndex = r.ReadListCount(); queueIndex > 0; queueIndex--)
						queues.Add(new PvpMatchMakingQueue(r.ReadU32Be(), r.ReadU32Be()));

					maps.Add(new PvpMatchMakingMapBoard(mapId, queues));
				}

				types.Add(new PvpMatchMakingTypeBoard(minigameType, maps));
			}

			var anyApplies = r.ReadU32Be();
			r.EnsureAtEnd();

			return new PvpMatchMakingInfo(types, anyApplies);
		}
	}

	/// <summary>The whole minigame queue board.</summary>
	public sealed record PvpMatchMakingInfo(IReadOnlyList<PvpMatchMakingTypeBoard> Types, uint NumberOfAnyApplies)
	{
		/// <summary>What a server nobody has queued on sends - the four login captures, byte for byte.</summary>
		public static readonly PvpMatchMakingInfo Empty = new([], 0);
	}

	/// <summary>One minigame objective (the LDB 802 id) and the maps that run it.</summary>
	public sealed record PvpMatchMakingTypeBoard(uint MinigameType, IReadOnlyList<PvpMatchMakingMapBoard> Maps);

	/// <summary>One minigame playfield and its per-queue-type signup counts.</summary>
	public sealed record PvpMatchMakingMapBoard(uint MapId, IReadOnlyList<PvpMatchMakingQueue> Queues);

	/// <summary>How many characters are signed up for one (type, map, queueType) triple right now.</summary>
	public sealed record PvpMatchMakingQueue(uint QueueType, uint Signups);

	/// <summary>
	///     Rolls a flat set of live signups up into the board's three-level shape, keyed and ordered the way retail
	///     sends it (ascending type, then map, then queue type - which is what every captured body is in).
	/// </summary>
	public static class PvpMatchMakingBoard
	{
		/// <param name="knownQueues">
		///     Every (type, map, queueType) the server has ever seen a signup for. Retail keeps listing a queue at 0
		///     once it has appeared, so the caller carries the registry and this only counts into it.
		/// </param>
		/// <param name="signups">The signups held right now; each contributes 1 to its own triple.</param>
		/// <param name="numberOfAnyApplies">How many characters picked "join first available" instead of a minigame.</param>
		public static PvpMatchMakingInfo From(IEnumerable<(uint MinigameType, uint MapId, uint QueueType)> knownQueues,
			IEnumerable<(uint MinigameType, uint MapId, uint QueueType)> signups, uint numberOfAnyApplies)
		{
			ArgumentNullException.ThrowIfNull(knownQueues);
			ArgumentNullException.ThrowIfNull(signups);

			var counts = signups.CountBy(s => s).ToImmutableDictionary();

			var types = knownQueues
				.Distinct()
				.GroupBy(q => q.MinigameType)
				.OrderBy(g => g.Key)
				.Select(g => new PvpMatchMakingTypeBoard(g.Key, g
					.GroupBy(q => q.MapId)
					.OrderBy(m => m.Key)
					.Select(m => new PvpMatchMakingMapBoard(m.Key, m
						.OrderBy(q => q.QueueType)
						.Select(q => new PvpMatchMakingQueue(q.QueueType, (uint)counts.GetValueOrDefault(q)))
						.ToList()))
					.ToList()))
				.ToList();

			return new PvpMatchMakingInfo(types, numberOfAnyApplies);
		}
	}
}
