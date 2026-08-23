using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Dialogue
{
	// The create-world cutscene's STOCK-PHRASE subtitle channel (Kalanthes's beach lines), distinct from the
	// interactive ConversationService: the client fires PlayStockphraseAsSubtitle (0x205E) per line and blocks up
	// to 60s for a ReceivedStockPhraseText (0x205D) reply. The remoteformat payloads are DB-sourced (StockPhrases,
	// harvested from captures by the importer's stock-phrases module) - keyed by the {stock_N} token.
	internal sealed class StockConversationService
	{
		private readonly ICatalogRepository _database;
		private readonly ILogger _logger;

		// {stock_N} -> captured remoteformat, loaded once from the DB (small, stable server content).
		private Dictionary<string, StockPhrase>? _phrases;

		public StockConversationService(ILogger logger, ICatalogRepository database)
		{
			_logger = logger;
			_database = database;
		}

		internal async Task HandlePlayStockphraseAsSubtitle(INetworkClient client, byte[] requestBody)
		{
			PlayStockphraseAsSubtitle request;
			try
			{
				request = PlayStockphraseAsSubtitleCodec.Parse(requestBody);
			}
			catch (CodecParseException e)
			{
				// Say WHY rather than "could not parse": the codec names the offset and what it expected there.
				_logger.LogWarning(e, "PlayStockphraseAsSubtitle: could not parse request body ({Len} bytes)", requestBody.Length);
				return;
			}

			var (stockKey, threadContext) = (request.StockKey, request.ThreadContext);

			var phrases = _phrases ??= await _database.GetStockPhrases();
			if (!phrases.TryGetValue(stockKey, out var phrase))
			{
				_logger.LogWarning(
					"PlayStockphraseAsSubtitle: no StockPhrases row for {Key} (tc=0x{Tc:X8}) - client will time out; re-run the importer's stock-phrases module against a capture that has it",
					stockKey, threadContext);
				return;
			}

			var id = client.Session.Character.Id;
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceType, id),
				GameCharInterfaceMethods.DispatchReceivedStockPhraseText, ReceivedStockPhraseTextCodec.Build(new ReceivedStockPhraseText(threadContext, RemoteFormatScreenCodec.Build(RemoteFormatScreens.ForStockPhrase(phrase))))));

			_logger.LogInformation("PlayStockphraseAsSubtitle: answered {Key} (tc=0x{Tc:X8}) for client {Id} (#234 cutscene speech)", stockKey, threadContext, id);
		}

	}
}
