using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Harvests cutscene stock-phrase subtitles (Kalanthes's beach lines,...) from create-flow captures.
	/// </summary>
	/// <remarks>
	///     Pairs each client PlayStockphraseAsSubtitle ([ProtoID npc][u16 len]{stock_N}[u32 threadCtx]) with the retail
	///     ReceivedStockPhraseText reply ([u32 threadCtx][u16 len][remoteformat]) by thread-context. Yields {stock_N} ->
	///     the category-50000 remoteformat payload, which StockConversationService rebuilds from the row's columns. Every
	///     captured phrase is a parameter-less root tag, so the four root attributes are the whole payload; a phrase that
	///     arrives with parameters would not round-trip through these columns, so it is rejected loudly rather than
	///     truncated.
	/// </remarks>
	public static class CaptureStockPhraseImporter
	{
		private static readonly uint PlayStockphraseAsSubtitle = CapturedPacket.Wire(GameCharAgentMethods.PlayStockphraseAsSubtitle); // c2s request
		private static readonly uint ReceivedStockPhraseText = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchReceivedStockPhraseText); // s2c reply

		public static IEnumerable<(string Key, byte[] RemoteFormat)> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var pending = new Dictionary<uint, string>(); // threadCtx -> {stock_N} awaiting its reply
			var found = new Dictionary<string, byte[]>();
			foreach (var p in packets)
			{
				if (p.Bytes.Length < 12)
					continue;

				var headerLen = (int)p.Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				if (headerLen <= 0 || bodyStart > p.Bytes.Length)
					continue;

				var body = p.Bytes.AsSpan(bodyStart);

				if (p.WireOpcode == PlayStockphraseAsSubtitle)
				{
					// [8-byte ProtoID][u16 strLen][{stock_N}][u32 threadCtx]
					if (body.Length < 8 + 2 + 4)
						continue;

					int strLen = body.ReadU16Be(8);
					if (10 + strLen + 4 > body.Length)
						continue;

					var key = Encoding.Latin1.GetString(body.Slice(10, strLen));
					var tc = body.ReadU32Be(10 + strLen);
					pending[tc] = key;
				}
				else if (p.WireOpcode == ReceivedStockPhraseText)
				{
					// [u32 threadCtx][u16 textLen][remoteformat]
					if (body.Length < 6)
						continue;

					var tc = body.ReadU32Be(0);
					int textLen = body.ReadU16Be(4);
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
				var screen = RemoteFormatScreenCodec.Parse(Encoding.Latin1.GetString(rf));
				if (screen.Parameters.Count > 0 || screen.Antitag != null)
					throw new InvalidOperationException(
						$"Stock phrase {key} has {screen.Parameters.Count} parameter(s)/antitag - the StockPhrases columns only model a bare root tag, so this would not round-trip. Extend the table before importing it.");

				var existing = await db.StockPhrases.FindAsync(key);
				var row = existing ?? new StockPhrase { Key = key };
				var before = (row.RemoteFormatId, row.Category, row.Knubot, row.FormatKey);

				row.RemoteFormatId = uint.Parse(screen.Id);
				row.Category = uint.Parse(screen.Category);
				row.Knubot = uint.Parse(screen.Knubot);
				row.FormatKey = LdbKey.Decode(screen.Key) ?? new ProtoDecryptionKey();

				if (existing == null)
				{
					db.StockPhrases.Add(row);
					added++;
				}
				else if (before != (row.RemoteFormatId, row.Category, row.Knubot, row.FormatKey))
				{
					updated++;
				}
			}

			await db.SaveChangesAsync();
			return (seen, added, updated);
		}
	}
}
