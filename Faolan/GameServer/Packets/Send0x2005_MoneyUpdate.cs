using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Why the client showed money changing in the captures: a 12-byte 0x2005 download on the GameCharAgent
	// conversation, [u64 balanceCopper][u32 reason]. Decoded from the loot/sell/buy captures - the value grows
	// +2 per beach coin loot, jumps on quest rewards, DECREASES on a purchase (reason 0), and persists across
	// sessions, so it is the absolute wallet balance, not a delta. Retail does NOT push one at login (the
	// initial balance presumably rides the FullChar snapshot, location undecoded); we push on every change.
	public enum MoneyUpdateReason : uint
	{
		Spend = 0, // also used for syncs that don't celebrate a gain
		Unknown2 = 2, // seen once in the captures; semantics unknown
		Gain = 3
	}

	public partial class GameServerListener
	{
		// The 12-byte wire body; golden-tested against the captured first-coin-loot update (MoneyUpdateTests).
		internal static void WriteMoneyUpdateBody(FaolanStream s, ulong balanceCopper, MoneyUpdateReason reason)
		{
			s
				.WriteUInt64(balanceCopper)
				.WriteUInt32((uint)reason);
		}

		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		private async Task SendMoneyUpdate(INetworkClient client, ulong balanceCopper, MoneyUpdateReason reason)
		{
			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.MoneyUpdate);
			WriteMoneyUpdateBody(ps, balanceCopper, reason);
			await ps.SendAsync(client);

			Logger.LogInformation("MoneyUpdate: {Balance} copper ({Reason}) -> {Character}.", balanceCopper, reason, client.Session.Character?.Name);
		}
	}
}
