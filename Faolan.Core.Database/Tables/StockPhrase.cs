using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// A cutscene stock-phrase subtitle line (e.g. Kalanthes's beach destiny lines). The client requests one by its
	// {stock_N} token via PlayStockphraseAsSubtitle (0x205E) and blocks for a ReceivedStockPhraseText (0x205D)
	// reply; RemoteFormat is the captured category-50000 remoteformat payload that reply carries. Harvested from
	// the create-flow captures (0x205E req -> 0x205D resp), served by GameServer StockConversationService.
	public class StockPhrase
	{
		public byte[] RemoteFormat { get; set; } = null!;

		[Key, MaxLength(32)]
		public string Key { get; set; } = null!; // the client's "{stock_N}" token
	}
}
