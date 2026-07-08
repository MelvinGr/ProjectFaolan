using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Dialogue
{
	// The create-world cutscene's STOCK-PHRASE subtitle channel (Kalanthes's beach lines), distinct from the
	// interactive ConversationService: the client fires PlayStockphraseAsSubtitle (0x205E) per line and blocks up
	// to 60s for a ReceivedStockPhraseText (0x205D) reply, answered from captured remoteformats (#234). Extracted
	// out of CreateFlowService (was the StockPhraseResponder partial).
	internal sealed class StockConversationService
	{
		private readonly ILogger _logger;

		public StockConversationService(ILogger logger) => _logger = logger;

		// {stock_N} -> the exact captured remoteformat text bytes (hex to avoid transcribing the escaped
		// entities: &amp; &#39; &#92; etc). Stable server content - identical in every create capture.
		private static readonly Dictionary<string, byte[]> StockPhraseRemoteFormats = new Dictionary<string, byte[]>
		{
			// id=6195 key="gdVvZ+XDaiyqOA_OQ+_."
			["{stock_48}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236313935222063617465676f72793d22353030303022206b65793d22676456765a2b5844616979714f415f4f512b5f2e22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e"),
			// id=6196 key="Uj1v&amp;fwEoqN_lA8bnWOp"
			["{stock_49}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236313936222063617465676f72793d22353030303022206b65793d22556a317626616d703b6677456f714e5f6c4138626e574f7022206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e"),
			// id=6197 key="O,AyOT)I7kT+ECrK(V.?"
			["{stock_50}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236313937222063617465676f72793d22353030303022206b65793d224f2c41794f542949376b542b4543724b28562e3f22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e"),
			// id=6198 key="K&#39;B*LKSZ*)%&#92;mr^^wf0^"
			["{stock_51}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236313938222063617465676f72793d22353030303022206b65793d224b262333393b422a4c4b535a2a2925262339323b6d725e5e7766305e22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e"),
			// id=6199 key="!-u62.+NE`(`enfqa-MH"
			["{stock_52}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236313939222063617465676f72793d22353030303022206b65793d22212d7536322e2b4e45602860656e6671612d4d4822206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e"),
			// id=6200 key="l0YEA(3XEbloy4HkuHFC"
			["{stock_56}"] = Convert.FromHexString(
				"3c72656d6f7465666f726d61742069643d2236323030222063617465676f72793d22353030303022206b65793d226c3059454128335845626c6f7934486b7548464322206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")
		};
		internal async Task HandlePlayStockphraseAsSubtitle(INetworkClient client, byte[] requestBody)
		{
			if (!TryParsePlayStockphraseRequest(requestBody, out var stockKey, out var threadContext))
			{
				_logger.LogWarning("PlayStockphraseAsSubtitle: could not parse request body ({Len} bytes)", requestBody.Length);
				return;
			}

			if (!StockPhraseRemoteFormats.TryGetValue(stockKey, out var textBytes))
			{
				_logger.LogWarning("PlayStockphraseAsSubtitle: no remoteformat for stock phrase {Key} (tc=0x{Tc:X8}) - client will time out; add it to StockPhraseRemoteFormats from a capture", stockKey, threadContext);
				return;
			}

			var id = client.Session.Account.ClientInstance;
			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x08), Endpoints.EndpointBytes(Endpoints.GameCharInterfaceType, id), GameCharInterfaceDispatchOpcodes.DispatchReceivedStockPhraseText)
				.WriteArray(BuildReceivedStockPhraseBody(threadContext, textBytes))
				.SendAsync(client);

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
		// The stable remoteformat text for a {stock_N} phrase, or false if we have no capture of it.
		internal static bool TryGetStockPhraseText(string stockKey, out byte[] textBytes)
		{
			return StockPhraseRemoteFormats.TryGetValue(stockKey, out textBytes);
		}
		// Parse a c2s PlayStockphraseAsSubtitle (0x205E) request:
		//   [ProtoID npcId (u32 type, u32 instance)][String: u16 len + bytes "{stock_N}"][u32 threadCtx]
		// Returns false if the body is malformed.
		internal static bool TryParsePlayStockphraseRequest(ReadOnlySpan<byte> body, out string stockKey, out uint threadContext)
		{
			stockKey = null;
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
