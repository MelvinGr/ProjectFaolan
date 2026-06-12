using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Importer
{
	// A captured on-wire packet with the metadata the extractors need, uniform across the .json dumps and the
	// .pcapng analyzer output.
	public readonly record struct CapturedPacket(long Ticks, bool IsDownload, uint Opcode, uint SubOpcode, byte[] Bytes);

	// One extracted conversation node: the player's TalkToNpc key + the captured NPC handle/instance + the ordered
	// server response packets to replay. NpcName is resolved from the same capture's FA015807 spawn for the handle
	// (null if that spawn wasn't captured) - the stable identity the runtime routes on instead of the volatile handle.
	public sealed record ConversationEntry(uint TypeId, uint Mode, uint Field3, uint CapturedNpcHandle, uint CapturedInstance, List<byte[]> Packets, string NpcName);

	// Ports Captures/extract_conversations.py: find each player TalkToNpc (Interact 642CD3D6, actionType 0x16),
	// read its args, and collect the server conversation packets that follow until the next talk. Caller merges
	// across captures and writes ConversationNode/ConversationPacket rows.
	public static class ConversationExtractor
	{
		private const uint InteractSubOpcode = 0x642CD3D6;
		private const uint Opcode2000 = 0x2000;
		private const uint Opcode2015 = 0x2015;

		private const uint SpawnSubOpcode = 0xFA015807;
		private const uint TalkToNpcActionType = 0x16;

		// The Knubot conversation-display response sub-opcodes (642CD3D6 echo excluded: it is a generic interact ack):
		// OpenChatWindow (045B0B0F), AppendText (4A3D899A), AnswerList (F164A4A5), CloseChatWindow (C4AF39EB). The
		// close is captured too so a replayed conversation ends by closing the window (and the stage segmenter has its
		// final terminator) instead of leaving it open.
		private static readonly HashSet<uint> ResponseSubOpcodes = [0x4A3D899A, 0xF164A4A5, 0x045B0B0F, 0xC4AF39EB];

		// `packets` must be one capture's GameServer packets in chronological order.
		public static List<ConversationEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var namesByHandle = BuildSpawnNameMap(packets);
			var talkIndices = new List<int>();
			for (var i = 0; i < packets.Count; i++)
			{
				var p = packets[i];
				if (p.IsDownload || p.Opcode != Opcode2000 || p.SubOpcode != InteractSubOpcode)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length >= 22 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4)) == TalkToNpcActionType)
					talkIndices.Add(i);
			}

			var talkSet = talkIndices.ToHashSet();
			var entries = new List<ConversationEntry>();

			foreach (var ti in talkIndices)
			{
				var body = GetBody(packets[ti].Bytes);
				if (body.Length < 26)
					continue;

				var instance = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				var argLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(22, 4));
				if (26 + argLen > body.Length)
					continue;

				var (typeId, mode, field3, npc) = ParseArgs(body.AsSpan(26, argLen));

				var resp = new List<byte[]>();
				for (var j = ti + 1; j < packets.Count && !talkSet.Contains(j); j++)
				{
					var r = packets[j];
					if (r.IsDownload && (r.Opcode == Opcode2015 || (r.Opcode == Opcode2000 && ResponseSubOpcodes.Contains(r.SubOpcode))))
						resp.Add(r.Bytes);
				}

				if (resp.Count > 0)
					entries.Add(new ConversationEntry(typeId, mode, field3, npc, instance, resp, namesByHandle.GetValueOrDefault(npc)));
			}

			return entries;
		}

		// Merges nodes from many captures, deduped by (TypeId, Mode, Field3). Prefers an entry whose captured player
		// instance is a 4-byte varint (in-range, so it re-addresses to any normal character), then the most packets.
		// Deduped by (CapturedNpcHandle, TypeId, Mode, Field3) - the NPC handle is part of the key because
		// (TypeId, Mode, Field3) is not NPC-unique (the same mode is a different conversation per NPC/quest-state).
		// Prefers an entry whose captured player instance is a 4-byte varint, then the most packets.
		public static List<ConversationEntry> Merge(IEnumerable<ConversationEntry> all)
		{
			var best = new Dictionary<(string, uint, uint, uint), (int score, ConversationEntry entry)>();
			foreach (var e in all)
			{
				// Key by NPC NAME (stable identity), not the session-volatile handle: the same NPC from different
				// captures has different handles but the same name, so this correctly merges them into one node.
				// Fall back to the handle string when a name couldn't be resolved so unrelated nodes don't collapse.
				var identity = string.IsNullOrEmpty(e.NpcName)
					? $"#{e.CapturedNpcHandle}"
					: e.NpcName;
				var key = (identity, e.TypeId, e.Mode, e.Field3);
				var score = (VarintLength(e.CapturedInstance) == 4
					? 1_000_000
					: 0) + e.Packets.Count;
				if (!best.TryGetValue(key, out var cur) || score > cur.score)
					best[key] = (score, e);
			}

			return best
				.Values
				.Select(v => v.entry)
				.ToList();
		}

		// Maps each FA015807 spawn's handle to its embedded name (0x0A <len> <utf8>), so a conversation's NPC handle
		// can be resolved to a stable name within the same capture.
		internal static Dictionary<uint, string> BuildSpawnNameMap(IReadOnlyList<CapturedPacket> packets)
		{
			var map = new Dictionary<uint, string>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.Opcode != Opcode2000 || p.SubOpcode != SpawnSubOpcode)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < 16)
					continue;

				var handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
				var name = ReadSpawnName(body);
				if (name != null)
					map[handle] = name;
			}

			return map;
		}

		// The 0x2000 body within the full on-wire packet: [len4][crc4][headerLen4][header][body].
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

		// Walks the InteractArgs protobuf -> (typeId f1, mode f2, field3 f3, npc handle = field10.SpellTarget.f2).
		private static (uint TypeId, uint Mode, uint Field3, uint Npc) ParseArgs(ReadOnlySpan<byte> a)
		{
			uint typeId = 0, mode = 0, field3 = 0, npc = 0;
			var i = 0;
			while (i < a.Length)
			{
				var tag = a[i++];
				int fn = tag >> 3, wt = tag & 7;
				if (wt == 0)
				{
					var v = ReadVarint(a, ref i);
					if (fn == 1)
						typeId = (uint)v;
					else if (fn == 2)
						mode = (uint)v;
					else if (fn == 3)
						field3 = (uint)v;
				}
				else if (wt == 2)
				{
					var len = (int)ReadVarint(a, ref i);
					var sub = a.Slice(i, len);
					i += len;
					if (fn == 10) // SpellTarget { f1, f2 = npc handle }
					{
						var j = 0;
						while (j < sub.Length)
						{
							var t2 = sub[j++];
							int f2 = t2 >> 3, w2 = t2 & 7;
							if (w2 == 0)
							{
								var v2 = ReadVarint(sub, ref j);
								if (f2 == 2)
									npc = (uint)v2;
							}
							else
								break;
						}
					}
				}
				else
					break;
			}

			return (typeId, mode, field3, npc);
		}

		// The entity name is the first `0x0A <len 1..63> <printable-ascii>` run in the spawn body.
		private static string ReadSpawnName(ReadOnlySpan<byte> body)
		{
			for (var i = 16; i + 2 < body.Length; i++)
			{
				if (body[i] != 0x0A)
					continue;

				int len = body[i + 1];
				if (len is < 1 or > 63 || i + 2 + len > body.Length)
					continue;

				var ok = true;
				for (var j = 0; j < len; j++)
				{
					var c = body[i + 2 + j];
					if (c is < 0x20 or > 0x7E)
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return Encoding.ASCII.GetString(body.Slice(i + 2, len));
			}

			return null;
		}

		private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int i)
		{
			ulong r = 0;
			var shift = 0;
			while (true)
			{
				var x = b[i++];
				r |= (ulong)(x & 0x7f) << shift;
				if ((x & 0x80) == 0)
					return r;

				shift += 7;
			}
		}

		private static int VarintLength(uint value)
		{
			var n = 1;
			while (value >= 0x80)
			{
				value >>= 7;
				n++;
			}

			return n;
		}
	}
}
