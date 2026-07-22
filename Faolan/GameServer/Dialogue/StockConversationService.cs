using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Dialogue
{
	// The create-world cutscene's STOCK-PHRASE subtitle channel (Kalanthes's beach lines), distinct from the
	// interactive ConversationService: the client fires PlayStockphraseAsSubtitle (0x205E) per line and blocks up
	// to 60s for a ReceivedStockPhraseText (0x205D) reply. The remoteformat payloads are DB-sourced (StockPhrases,
	// harvested from captures by the importer's stock-phrases module) - keyed by the {stock_N} token.
	internal sealed class StockConversationService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// {stock_N} -> captured remoteformat, loaded once from the DB (small, stable server content).
		private Dictionary<string, byte[]>? _phrases;

		public StockConversationService(ILogger logger, IDatabaseRepository database)
		{
			_logger = logger;
			_database = database;
		}

		internal async Task HandlePlayStockphraseAsSubtitle(INetworkClient client, byte[] requestBody)
		{
			if (!TryParsePlayStockphraseRequest(requestBody, out var stockKey, out var threadContext))
			{
				_logger.LogWarning("PlayStockphraseAsSubtitle: could not parse request body ({Len} bytes)", requestBody.Length);
				return;
			}

			var phrases = _phrases ??= await _database.GetStockPhrases();
			if (!phrases.TryGetValue(stockKey, out var textBytes))
			{
				_logger.LogWarning(
					"PlayStockphraseAsSubtitle: no StockPhrases row for {Key} (tc=0x{Tc:X8}) - client will time out; re-run the importer's stock-phrases module against a capture that has it",
					stockKey, threadContext);
				return;
			}

			var id = client.Session.Character.Id;
			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x08), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, id),
				GameCharInterfaceOpcodes.DispatchReceivedStockPhraseText, BuildReceivedStockPhraseBody(threadContext, textBytes)));

			_logger.LogInformation("PlayStockphraseAsSubtitle: answered {Key} (tc=0x{Tc:X8}) for client {Id} (#234 cutscene speech)", stockKey, threadContext, id);
		}

		// Build the s2c ReceivedStockPhraseText (0x205D) body: [u32 threadCtx][u16 textLen][text].
		internal static byte[] BuildReceivedStockPhraseBody(uint threadContext, byte[] textBytes)
		{
			var body = new byte[4 + 2 + textBytes.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), threadContext);
			BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(4, 2), (ushort)textBytes.Length);
			textBytes.CopyTo(body.AsSpan(6));
			return body;
		}

		// Parse a c2s PlayStockphraseAsSubtitle (0x205E) request:
		//   [ProtoID npcId (u32 type, u32 instance)][String: u16 len + bytes "{stock_N}"][u32 threadCtx]
		// Returns false if the body is malformed.
		internal static bool TryParsePlayStockphraseRequest(ReadOnlySpan<byte> body, out string stockKey, out uint threadContext)
		{
			stockKey = null!;
			threadContext = 0;
			if (body.Length < 8 + 2 + 4)
				return false;

			int strLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(8, 2));
			var tcOffset = 10 + strLen;
			if (tcOffset + 4 > body.Length)
				return false;

			stockKey = Encoding.Latin1.GetString(body.Slice(10, strLen));
			threadContext = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(tcOffset, 4));
			return true;
		}
	}
}
