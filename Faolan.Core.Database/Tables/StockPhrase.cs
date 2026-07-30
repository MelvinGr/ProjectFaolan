using System.ComponentModel.DataAnnotations;
using Faolan.Core.Protobuf;

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

		// The per-string LDB DECRYPTION KEY (LdbKey), stored as the 128-bit TEA key - the same ProtoDecryptionKey an
		// item template carries - rather than as the 20-character wire string, which LdbKey.ToWire regenerates from it
		// exactly, retail's attribute escaping included.
		//
		// ⚠️ Long described here as a "client-validated content hash" carried as raw attribute text. It is neither: the
		// client is not validating anything, it is DECRYPTING - eleven LDB categories are encrypted and this is the key
		// (#520). Text that came out blank was the client failing to decrypt, not failing a checksum.
		public virtual ProtoDecryptionKey FormatKey { get; set; } = new();

		[Key, MaxLength(32)]
		public string Key { get; set; } = null!; // the client's "{stock_N}" token
	}
}
