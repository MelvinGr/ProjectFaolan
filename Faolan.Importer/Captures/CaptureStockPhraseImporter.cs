using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Harvests cutscene stock-phrase subtitles (Kalanthes's beach lines, ...) from create-flow captures: pairs each
	// client PlayStockphraseAsSubtitle (0x205E: [ProtoID npc][u16 len]{stock_N}[u32 threadCtx]) with the retail
	// ReceivedStockPhraseText reply (0x205D: [u32 threadCtx][u16 len][remoteformat]) by thread-context. Yields
	// {stock_N} -> the category-50000 remoteformat payload, which GameServer StockConversationService replays.
	public static class CaptureStockPhraseImporter
	{
		private const ushort PlayStockphraseAsSubtitle = 0x205E; // c2s request
		private const ushort ReceivedStockPhraseText = 0x205D;   // s2c reply

		public static IEnumerable<(string Key, byte[] RemoteFormat)> Extract(IReadOnlyList<Extractors.CapturedPacket> packets)
		{
			var pending = new Dictionary<uint, string>();   // threadCtx -> {stock_N} awaiting its reply
			var found = new Dictionary<string, byte[]>();
			foreach (var p in packets)
			{
				if (p.Bytes.Length < 12)
					continue;

				var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Bytes.AsSpan(8, 4));
				var bodyStart = 12 + headerLen;
				if (headerLen <= 0 || bodyStart > p.Bytes.Length)
					continue;

				var body = p.Bytes.AsSpan(bodyStart);

				if (p.Opcode == PlayStockphraseAsSubtitle)
				{
					// [8-byte ProtoID][u16 strLen][{stock_N}][u32 threadCtx]
					if (body.Length < 8 + 2 + 4)
						continue;
					int strLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(8, 2));
					if (10 + strLen + 4 > body.Length)
						continue;
					var key = Encoding.Latin1.GetString(body.Slice(10, strLen));
					var tc = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(10 + strLen, 4));
					pending[tc] = key;
				}
				else if (p.Opcode == ReceivedStockPhraseText)
				{
					// [u32 threadCtx][u16 textLen][remoteformat]
					if (body.Length < 6)
						continue;
					var tc = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4));
					int textLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4, 2));
					if (6 + textLen <= body.Length && pending.TryGetValue(tc, out var key))
						found[key] = body.Slice(6, textLen).ToArray();
				}
			}

			return found.Select(kv => (kv.Key, kv.Value)).ToList();
		}

		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(DatabaseContext db, IEnumerable<(string Key, byte[] RemoteFormat)> phrases)
		{
			int seen = 0, added = 0, updated = 0;
			foreach (var (key, rf) in phrases)
			{
				seen++;
				var existing = await db.StockPhrases.FindAsync(key);
				if (existing == null)
				{
					db.StockPhrases.Add(new StockPhrase { Key = key, RemoteFormat = rf });
					added++;
				}
				else if (existing.RemoteFormat == null || !existing.RemoteFormat.AsSpan().SequenceEqual(rf))
				{
					existing.RemoteFormat = rf;
					updated++;
				}
			}

			await db.SaveChangesAsync();
			return (seen, added, updated);
		}
	}
}
