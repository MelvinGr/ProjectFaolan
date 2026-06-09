using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets._0x737A6DF9_Test;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Initial inventory snapshot packet.
		//
		// This packet is the giant 0x737A6DF9 payload sent inside a 0x2000 packet.
		// It appears to contain the initial equipment/bag/resource/quest inventory state
		// that the client uses after entering the game.
		//
		// Important capture observation:
		// - later inventory actions are slot-based on the wire;
		// - however, the random-actions capture did not show a clean incremental
		//   server->client bag sync for every move/change;
		// - the matching container data is present in this earlier giant 0x737A6DF9
		//   download, making this packet the strongest current candidate for the
		//   initial full inventory snapshot.
		//
		// Top-level protobuf structure currently observed:
		// - field 1 = small blob
		// - field 2 = varint
		// - field 3 = varint
		// - field 4 = repeated equipment/appearance-like entries
		// - field 6 = inventory container 101
		// - field 6 = inventory container 104
		// - field 6 = inventory container 112
		// - field 6 = inventory container 113
		//
		// The relevant field-6 container chain currently looks like:
		//   101 -> f6 -> 104 -> f6 -> 112 -> f6 -> 113
		//
		// Current container interpretation:
		// - 101 = equipment/appearance-style container.
		//         This matches item/style ids already seen in Spawn-like data.
		// - 104 = normal bag container.
		//         This contains visible bag slots and stack quantities.
		// - 112 = resources-like container.
		//         This is inferred mostly from count/profile shape.
		// - 113 = quest-like container.
		//         This is inferred mostly from count/profile shape.
		//
		// Container layout:
		// - f1 = wrapper message:
		//        f1 = container kind/id, for example 104
		//        f2 = owner/player/container owner id
		// - f2 = capacity / max slot index / size-like value
		//        This is NOT the number of occupied entries.
		//        Do not decrement this when removing an item.
		// - f3 = flags/state-like value
		// - f4 = repeated item entry
		//
		// Observed occupied slots per parsed container in this sample:
		// - Container 0:
		//   1, 2, 3, 5, 6, 7, 9, 10, 11, 12, 14, 15, 16, 18, 20, 21,
		//   1002, 1007, 1011, 1016, 1018
		// - Container 1:
		//   0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
		//   24, 25, 26, 27, 28, 29, 37, 38, 39, 40, 41, 42, 43, 44,
		//   45, 46, 47
		// - Container 2:
		//   0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
		//   17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
		//   31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45
		// - Container 3:
		//   0, 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 14, 15, 16, 18,
		//   19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
		//   33, 34, 35, 36, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47
		//
		// Bag/container 104 observations:
		// - slots 0..29 behave like the visible bag grid;
		// - later occupied slots, for example 37..47, likely belong to another
		//   page/subsection or hidden continuation area;
		// - the item quantity shown by the client is reproduced by a nested tiny
		//   property message shaped like { f1 = 26, f2 = quantity }.
		//
		// Item entry model:
		//
		// container.f4 = repeated item entry
		//   entry.f1 = slot index
		//   entry.f2 = item details
		//
		// itemDetails.f1 = item instance id / guid-like id
		//
		// itemDetails.f2 = repeated tiny property message:
		//   { f1 = 0,   f2 = flags/state }                // optional
		//   { f1 = 23,  f2 = local inventory record id }   // appears on every item
		//   { f1 = 26,  f2 = quantity }                    // optional; omitted likely means quantity 1
		//   { f1 = 265, f2 = special property }            // optional rare property
		//   Other rare property kinds have also been seen, such as 8 and 1290.
		//
		// itemDetails.f3 = stable item identity/template tail:
		//   f1 = 20-byte payload
		//   f2 = same 20-byte payload
		//   f3 = same 20-byte payload
		//   f4 = fixed32 item template/database id
		//   f5 = duplicate fixed32 item template/database id
		//   f7 = usually 60 in this sample
		//   f8 = fixed32 4-byte archetype/type code
		//
		// Practical interpretation from DumpBagSummary:
		// - slot = bag/grid slot.
		// - itemId = item instance id / guid-like value.
		// - property 23 = local inventory record id.
		// - property 26 = stack quantity.
		// - property 0 = item state flags.
		// - f4/f5 = duplicated item template/database id.
		// - f8 = 4-byte archetype/type code.
		//
		// Example health potion stack:
		// - slot = 3
		// - itemId = 2365515745234861398
		// - props = [0:1, 23:1119432, 26:20]
		// - f4 = 180450
		// - f5 = 180450
		// - f8 = 0x4847505A
		//
		// Another stack of the same template exists in this sample:
		// - slot = 9
		// - props = [0:1, 23:1119438, 26:82]
		// - f4/f5 = 180450
		// - f8 = 0x4847505A
		//
		// This confirms that property 26 is quantity and that f4/f5/f8 identify
		// the item type/template more reliably than slot or item instance id.
		//
		// Removing an entry:
		// - remove the whole container.f4 item entry;
		// - do not decrement container.f2, because it is capacity/size-like, not occupied item count; (is this true?)
		// - ProtoField.ToArray() recomputes protobuf length prefixes;
		// - WriteArrayUInt32Length recomputes the outer packet body length.
		// - Currently client does not like it when an entry is removed.
		//
		// Changing stack count:
		// - update property { f1 = 26, f2 = quantity };
		// - for values below 128, protobuf varint length remains one byte;
		// - changing 20 to 5 should only change one byte in the final packet;
		// - if the client hangs despite a one-byte valid protobuf edit, check for
		//   client-side cache/UI/state issues before assuming protobuf corruption.

		/*static GameServerListener()
		{
			Send0x2000_0x737A6DF9_Test(null);
		}*/

		private static Task Send0x2000_0x737A6DF9_Test(INetworkClient client)
		{
			var originalPacket = File.ReadAllBytes("/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-login-randomActions-bigboy.bin");
			var conanPacket = new ConanPacket(originalPacket);
			var bodyStream = new FaolanStream(conanPacket.Body.ReadArrayUInt32Length());

			var subOpcode = bodyStream.ReadUInt32();
			var unknownStuff1 = bodyStream.ReadUInt32(); // c350
			var oldClientInstance = bodyStream.ReadUInt32();
			// there is more data here, that is in packetBody.Prefix
			var packetBody = InventorySnapshotInspector.ParseInventoryPacketBody(bodyStream);

			/////////////////////////////

			// Tails contains:
			// Character stats, quests, abilities, etc..

			/////////////////////////////

			var equippedContainer = InventorySnapshotInspector.GetContainer(packetBody.Root, InventorySnapshotInspector.ContainerKind.Equipped);
			var bagContainer = InventorySnapshotInspector.GetContainer(packetBody.Root, InventorySnapshotInspector.ContainerKind.Bag);
			var resourceContainer = InventorySnapshotInspector.GetContainer(packetBody.Root, InventorySnapshotInspector.ContainerKind.Resource);
			var questContainer = InventorySnapshotInspector.GetContainer(packetBody.Root, InventorySnapshotInspector.ContainerKind.Quest);

			/////////////////////////////

			// Testing stats

			//var x = string.Join("\n", packetBody.Tails.PostAccessBlock.Values.Select((v, i) => $"{i}: {v}"));

			//packetBody.Tails.PostAccessBlock.CurrentHp = 69;
			//packetBody.Tails.PostAccessBlock.CurrentMana = 69;
			//packetBody.Tails.PostAccessBlock.CurrentStamina = 69;
			//packetBody.Tails.PostAccessBlock.PvpXpTillNextLevel = 69;
			//packetBody.Tails.PostAccessBlock.PvpKillsOverall = 69;
			//packetBody.Tails.PostAccessBlock.PvpDeathsOverall = 69;
			//packetBody.Tails.PostAccessBlock.SetValue(TailsPostAccessBlock.CharacterValueIndex.PvpKillsSeason, 69);

			//var values = TailsPostAccessBlock.CharacterValueIndex.GetValues()
			//	.Where(e => e.ToString().Contains("Unknown"))
			//	.ToDictionary(k => k, v => packetBody.Tails.PostAccessBlock.GetValue(v));

			//var x = values.ToJson(true);

			//packetBody.Tails.PostAccessBlock.SetValue(TailsPostAccessBlock.CharacterValueIndex.CurrentXp, 1000);

			//foreach (var value in TailsPostAccessBlock.CharacterValueIndex.GetValues().Except([TailsPostAccessBlock.CharacterValueIndex.CurrentHpExtra]))
			//{
			//	if (value.ToString().Contains("Unknown"))
			//		packetBody.Tails.PostAccessBlock.SetValue(value, (uint)value);
			//	//else //if (!value.ToString().Contains("Unknown"))
			//	//	packetBody.Tails.PostAccessBlock.SetValue(value, 1000);//6969u);
			//}

			// strength? (nope)
			//packetBody.Tails.PostAccessBlock.Values[85] = 6;
			//packetBody.Tails.PostAccessBlock.Values[233] = 6;

			// Testing inventory

			var entry = InventorySnapshotInspector.GetEntryBySlot(bagContainer, 3);
			if (entry != null)
			{
				InventorySnapshotInspector.SetEntryCount(entry, 69);

				//bagContainer.Fields.Remove(entry);
			}

			// Removing or adding a bagContainer entry, makes the client never pass the loading screen. changing counts is fine though, as long as current count > 1

			/////////////////////////////

			if (client == null) // manual running tests if client = null
			{
				//var x = new ConanPacket(BuildBurstPacket(new byte[] { 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xFF, 0xBD, 0xD9, 0x10 }, new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1 }, 0x2000, new byte[] { 0x00, 0x00, 0x00, 0x1B, 0x4D, 0x7D, 0x91, 0x23, 0x00, 0x00, 0xC3, 0x50, 0x02, 0x16, 0x5E, 0xFF, 0x00, 0x00, 0x01, 0x00, 0x00, 0xC3, 0x50, 0x00, 0x04, 0xDE, 0x8A, 0x00, 0x00, 0x00, 0x00 }));
				//var xx = x.PacketToCsCode();

				//

				return Task.CompletedTask;
			}

			var newStream = new PacketStream().WriteHeader(LoginWorldSender, LoginWorldReceiver(client), conanPacket.Opcode).WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(GameServer0X2000RespondsOpcodes.FullPlayerCharacterIIR); // subOpcode
				s.WriteUInt32(unknownStuff1); // unknownStuff1 (c350)
				s.WriteUInt32(client.Session.Account.ClientInstance); // oldClientInstance
				s.WriteArray(packetBody.Prefix);
				s.WriteArray(packetBody.Root.ToArray());
				s.WriteArray(packetBody.Tails.ToArray());
			});

			return newStream.SendAsync(client);
		}
	}
}
