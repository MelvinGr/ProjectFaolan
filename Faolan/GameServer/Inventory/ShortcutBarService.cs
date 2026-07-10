using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Inventory
{
	// The player's action bar / hotbar (#71). The client persists its bar as it drags shortcuts (AddShortcut
	// 0x206E / RemoveShortcut / UpdateShortcut) into CharacterShortcut rows, and requests the saved bar at login
	// (RequestShortcutBar 0x206D), which we answer with ShortcutBarReply (0x2076) rebuilt from those rows so the
	// bar survives relog. Extracted out of the GameServerListener dispatch switch as a composed collaborator
	// (#200): the switch cases are now thin one-liners delegating here, like the loot/shop cases.
	//
	// NB the in-game AddShortcut branch lives here, but the AddShortcut opcode (0x206E) ALSO doubles as the
	// create-editor enter-world signal - that create-flow branch stays on the listener (it's #197 handshake glue),
	// which routes to HandleAddShortcut only for the in-game case.
	public sealed class ShortcutBarService
	{
		private static readonly byte[] ReplySender = Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x58);
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		public ShortcutBarService(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// AddShortcut (0x206E, in-game branch): the player dragged an ability/item onto the bar. Persist the slot
		// (upsert - re-dragging overwrites); the login RESTORE is rebuilt from these rows (SendRestoredBar).
		public async Task HandleAddShortcut(INetworkClient client, ConanPacket packet)
		{
			var type = packet.Body.ReadUInt32(); // observed 4 = ability (ids match the 0x2052 grants), 2/3 = other
			var slotId = packet.Body.ReadUInt32(); // client slot id (ranges 0x64.. and 0x4E20.. = bar pages)
			var sourceId = packet.Body.ReadUInt32(); // ability id or item ref
			var unk = packet.Body.ReadUInt32();

			if (client.Session.Character != null)
				await _database.SetCharacterShortcut(client.Session.Character.Id, slotId, type, sourceId);

			_logger.LogInformation("AddShortcut: type {Type}, slot 0x{Slot:X4}, target 0x{Target:X8} (unk {Unk:X8}).", type, slotId, sourceId, unk);
		}

		// RemoveShortcut: body = [u32 slot].
		public async Task HandleRemoveShortcut(INetworkClient client, ConanPacket packet)
		{
			var slot = packet.Body.ReadUInt32();
			if (client.Session.Character != null)
				await _database.RemoveCharacterShortcut(client.Session.Character.Id, slot);

			_logger.LogInformation("RemoveShortcut slot 0x{Slot:X4}.", slot);
		}

		// UpdateShortcut: body = [u32 slot][u32 flag][u32 container]. Partial decode (#71, necrol80
		// randomActions/changemap captures): the slot is in the bar-PAGE range (0x4E20.., not the 0x64.. item
		// range), container is always 0x68 (the backpack kind), and flag is 0/1. The bar's restorable contents ride
		// AddShortcut; this bar-page toggle's exact semantics are still fuzzy, so it stays log-only.
		public Task HandleUpdateShortcut(INetworkClient client, ConanPacket packet)
		{
			var slot = packet.Body.ReadUInt32();
			var flag = packet.Body.ReadUInt32();
			var container = packet.Body.ReadUInt32();

			_logger.LogInformation("UpdateShortcut bar-page slot 0x{Slot:X4} flag={Flag} container=0x{Container:X} (#71; log-only).", slot, flag, container);
			return Task.CompletedTask;
		}

		// RequestShortcutBar (0x206D): the client asks for its saved hotbar at login; reply with the restored bar.
		// Sent from the GameCharAgent world conversation (sub-id 0x58 / the client instance), NOT echoed from the
		// request bytes - the 0x206D request's receiver carries no instance, so echoing it produced an empty-instance
		// sender the client silently dropped (#71: bar stayed empty in every slot though the body was byte-exact).
		// Retail sends 0x2076 from this same endpoint as the 0x2052 grants (login capture pkt 182 vs 237).
		public async Task SendRestoredBar(INetworkClient client)
		{
			if (client.Session.Character == null)
				return;

			var shortcuts = await _database.GetCharacterShortcuts(client.Session.Character.Id);
			var ps = new PacketStream();
			ps.WriteHeader(ReplySender, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchShortcutBarReply);
			ps.WriteArray(ShortcutBarBuilder.BuildBody(shortcuts));
			await ps.SendAsync(client);
			_logger.LogInformation("ShortcutBarReply: restored {Count} hotbar shortcut(s) (#71).", shortcuts.Length);
		}
	}
}
