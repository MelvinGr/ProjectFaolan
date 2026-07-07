using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Why the client showed money changing in the captures: a 12-byte 0x2005 download on the GameCharAgent
	// conversation, [u64 balanceTin][u32 reason]. Decoded from the loot/sell/buy captures - the value grows
	// +2 per beach coin loot, jumps on quest rewards, DECREASES on a purchase (reason 0), and persists across
	// sessions, so it is the absolute wallet balance, not a delta. Retail does NOT push one at login (the
	// initial balance presumably rides the FullChar snapshot, location undecoded); we push on every change.
	// The unit is TIN, the game's smallest coin (100 tin = 1 copper, 100 copper = 1 silver, 100 silver =
	// 1 gold) - verified in-client 2026-06-12: a balance of 100 displays as 1 copper.
	public enum UpdateCashReason : uint
	{
		Spend = 0, // also used for syncs that don't celebrate a gain
		Unknown2 = 2, // seen once in the captures; semantics unknown
		Gain = 3
	}

	public partial class GameServerListener
	{
		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		private async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			var ps = new PacketStream();
			ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchUpdateCash);
			WriteUpdateCashBody(ps, balanceTin, reason);
			await ps.SendAsync(client);

			Logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

		// The 12-byte wire body; golden-tested against the captured first-coin-loot update (UpdateCashTests).
		internal static void WriteUpdateCashBody(FaolanStream s, ulong balanceTin, UpdateCashReason reason)
		{
			s.WriteUInt64(balanceTin).WriteUInt32(reason);
		}
	}
}
