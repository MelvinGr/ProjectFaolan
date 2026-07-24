using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// A cutscene stock-phrase subtitle line (e.g. Kalanthes's beach destiny lines). The client requests one by its
	// {stock_N} token via PlayStockphraseAsSubtitle (0x205E) and blocks for a ReceivedStockPhraseText (0x205D)
	// reply; the reply carries a category-50000 remoteformat payload. Harvested from the create-flow captures
	// (0x205E req -> 0x205D resp), served by GameServer StockConversationService.
	//
	// The captured payload used to ride here as a raw `RemoteFormat` blob; it is COLUMNIZED (#410) - every captured
	// stock phrase is a PARAMETER-LESS root tag, so these four root attributes ARE the whole payload and the reply
	// is rebuilt with RemoteFormatXml.Build (byte-exact against the capture for all six phrases). NB the format key
	// is the remoteformat's own `key=` attribute - unrelated to this row's {stock_N} primary key.
	public class StockPhrase
	{
		// Root attributes, stored as the raw ATTRIBUTE TEXT the capture carries: FormatKey keeps its XML entities
		// (`&amp;`, `&#39;`, `&#92;`) exactly as sent, since the client is fed these bytes verbatim and re-escaping
		// is not round-trip safe.
		public uint Category { get; set; } // always 50000 in the captures - the stock-phrase category
		public uint Knubot { get; set; }
		public uint RemoteFormatId { get; set; }

		[MaxLength(64)]
		public string FormatKey { get; set; } = null!;

		[Key, MaxLength(32)]
		public string Key { get; set; } = null!; // the client's "{stock_N}" token
	}
}
