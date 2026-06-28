using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Extractors
{
	// One map's login world build: the captured player instance + the ordered server download packets from a
	// MapChange up to the client's first spawn poll (0x2020). Replaying these (re-addressed to the live character)
	// is how a map renders - exactly what the embedded beach blob does today, generalized per map from captures.
	public sealed record MapWorldBurstEntry(uint MapId, uint CapturedInstance, List<byte[]> Packets);

	public static class MapWorldBurstExtractor
	{
		private const int MaxEntityQuitsInWorldBuild = 10;
		private const uint MapChangeSubOpcode = 0x5D85BFC7;

		// N3ToClientQuit (entity leaving). A clean world-build never contains one - entities are spawned, not removed -
		// so its presence above a small tolerance marks a window captured MID-SESSION (the player active in a populated
		// area, NPCs entering/leaving range) rather than a clean world entry. Such a window is gameplay churn, not a
		// world build, so it is rejected. Measured across all captures: clean maps have 0 quits (one out-of-range
		// capture has 5); the only polluted window, the city map 11040, has 72 - a 14x margin - alongside 2375 packets
		// (next-largest clean window: 282) and 1194 NPC StatIir ticks (next: 15). The threshold isolates it cleanly.
		private const uint N3ToClientQuitSubOpcode = 0x04B82B2D;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2020 = 0x2020; // client spawn poll - ends the pre-poll spawn burst
		private const uint Opcode2076 = 0x2076; // spawn manifest (entity handle -> slot table), sent after the poll flood

		// The LoginWorld receiver conversation id "0D 47 C1 67 6C 10 <varint>" - its trailing varint is the client
		// instance the packet was sent to (the local player), unambiguous even when other players are spawned.
		private static readonly byte[] ReceiverMarker = [0x47, 0xC1, 0x67, 0x6C, 0x10];

		// Per capture: each MapChange begins a world-build window. The FULL window is the pre-poll spawn burst (every
		// 0x2000 download up to the client's first 0x2020 poll) PLUS the single 0x2076 spawn manifest the server sends
		// after the poll flood - the entity handle->slot table the client needs to instantiate the spawned entities.
		// (Retail order: MapChange -> 0x2000 spawn burst -> client 0x2020 poll flood -> server 0x2008 SpawnCheck +
		// 0x2076 manifest -> 0x2024 ReadyForPlayScreen.) Everything else in the post-poll region is server-generated
		// (0x2008 SpawnCheck, StatIir, ReadyForPlayScreen - all rebuilt live on world entry) or gameplay, so only the
		// 0x2076 is taken. The window ends at the next MapChange (or end of capture). Returns the largest window per
		// map. This is what the committed beach (4010) seed carries by hand; the extractor now derives it for any map.
		public static List<MapWorldBurstEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var bursts = new List<MapWorldBurstEntry>();
			List<byte[]> current = null;
			uint currentMap = 0;
			uint capturedInstance = 0;
			var sawPoll = false; // per-window: has the client's first 0x2020 poll passed? (ends the pre-poll burst)

			foreach (var p in packets)
			{
				var body = GetBody(p.Bytes);
				var subop = body.Length >= 8
					? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4))
					: 0;

				if (p.Opcode == Opcode2000 && subop == MapChangeSubOpcode && p.IsDownload)
				{
					Flush();
					currentMap = body.Length >= 38
						? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(34, 4))
						: 0;
					current = [p.Bytes];

					// The receiver conversation id of the world-build downloads is the local player's instance -
					// read it from the first packet's header (unambiguous even when other players are spawned).
					BackfillInstance(p.Bytes);
					continue;
				}

				if (current == null)
					continue;

				// The first 0x2020 poll ends the pre-poll spawn burst but NOT the window - the 0x2076 manifest is
				// still to come. Keep the window open and switch to manifest-only collection.
				if (p.Opcode == Opcode2020)
				{
					sawPoll = true;
					continue;
				}

				if (!sawPoll)
				{
					// Pre-poll spawn burst: every 0x2000 download (world objects, entity spawns, the player spawn).
					if (p.Opcode == Opcode2000 && p.IsDownload)
					{
						current.Add(p.Bytes);
						BackfillInstance(p.Bytes); // backfill if the MapChange header lacked the instance
					}
				}
				else if (p.Opcode == Opcode2076 && p.IsDownload)
				{
					// Post-poll: the one structural record worth keeping - the spawn manifest. Everything else here is
					// server-generated or gameplay and is deliberately skipped.
					current.Add(p.Bytes);
					BackfillInstance(p.Bytes);
				}
			}

			Flush();
			return bursts;

			void Flush()
			{
				// Reject a window captured mid-session (gameplay churn, not a clean world build) - see the
				// N3ToClientQuit remark. A rejected map with no clean window elsewhere falls back to the beach burst.
				if (current is { Count: > 0 } && currentMap != 0 && CountSubOpcode(current, N3ToClientQuitSubOpcode) <= MaxEntityQuitsInWorldBuild)
					bursts.Add(new MapWorldBurstEntry(currentMap, capturedInstance, current));
				current = null;
				currentMap = 0;
				capturedInstance = 0;
				sawPoll = false;
			}

			void BackfillInstance(byte[] full)
			{
				if (capturedInstance != 0)
					return;

				var inst = ExtractReceiverInstance(full);
				if (inst != 0)
					capturedInstance = inst;
			}
		}

		// One burst per map: the longest (most complete) that also has a known captured instance.
		public static List<MapWorldBurstEntry> Merge(IEnumerable<MapWorldBurstEntry> all)
		{
			var best = new Dictionary<uint, MapWorldBurstEntry>();
			foreach (var e in all)
			{
				// Drop only bursts with an unknown captured instance - those can't be re-addressed. Out-of-range
				// instances (maps 1000/11040 captured e.g. 0x110099BD, a 5-byte varint) ARE kept now: the runtime
				// RewriteBurstInstance pads the live id's varint to the captured length, so the substitution stays
				// length-preserving.
				if (e.CapturedInstance == 0)
					continue;

				if (!best.TryGetValue(e.MapId, out var cur) || e.Packets.Count > cur.Packets.Count)
					best[e.MapId] = e;
			}

			return best.Values.ToList();
		}

		// Counts window packets whose IIR sub-opcode (body[4:8]) equals `subOpcode` - used by the mid-session
		// hygiene gate to tally entity-quit churn.
		private static int CountSubOpcode(IEnumerable<byte[]> window, uint subOpcode)
		{
			var n = 0;
			foreach (var full in window)
			{
				var body = GetBody(full);
				if (body.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) == subOpcode)
					n++;
			}

			return n;
		}

		// Reads the local player's client instance from the receiver conversation id "...47 C1 67 6C 10 <varint>"
		// embedded in the packet header. Returns 0 if the marker isn't present.
		private static uint ExtractReceiverInstance(byte[] full)
		{
			var idx = IndexOf(full, ReceiverMarker);
			if (idx < 0)
				return 0;

			var pos = idx + ReceiverMarker.Length;
			return ReadVarint(full, pos);
		}

		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length
				? full[bodyStart..]
				: [];
		}

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return -1;

			for (var i = 0; i <= haystack.Length - needle.Length; i++)
			{
				var match = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						match = false;
						break;
					}
				}

				if (match)
					return i;
			}

			return -1;
		}

		// Little-endian base-128 varint (the receiver-tail encoding). Stops at the buffer end / a non-continuation
		// byte; the instance ids of interest fit in 32 bits.
		private static uint ReadVarint(byte[] buf, int pos)
		{
			uint value = 0;
			var shift = 0;
			while (pos < buf.Length && shift < 32)
			{
				var b = buf[pos++];
				value |= (uint)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;

				shift += 7;
			}

			return value;
		}
	}
}
