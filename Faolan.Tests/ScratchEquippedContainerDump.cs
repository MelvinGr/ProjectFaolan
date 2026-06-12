using System;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// TEMPORARY diagnostic dump (not an assertion): prints the captured Strie snapshot's container entries so the
	// generated equipped container can be field-diffed against retail. Delete after the equipment-window bug is fixed.
	public class ScratchEquippedContainerDump
	{
		private const string BigboyPath = "/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-login-randomActions-bigboy.bin";

		private readonly ITestOutputHelper _output;

		public ScratchEquippedContainerDump(ITestOutputHelper output)
		{
			_output = output;
		}

		[Fact]
		public void DumpCapturedContainers()
		{
			if (!File.Exists(BigboyPath))
				return;

			var packet = new ConanPacket(File.ReadAllBytes(BigboyPath));
			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			body.ReadUInt32(); // subOpcode
			body.ReadUInt32(); // c350
			body.ReadUInt32(); // instance
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(body);

			var root = ((ReadOnlySpan<byte>)parts.Root.ToArray()).FromProtobuf<ProtoFullCharacterIIR>();
			var sb = new StringBuilder();
			foreach (var inv in root.Inventories)
			{
				sb.AppendLine($"container type={inv.InventoryId?.Type} instance=0x{inv.InventoryId?.Instance:X8} maxSize={inv.MaxSize} disabled={inv.DisabledSlotsCount} items={inv.Items.Count}");
				foreach (var e in inv.Items)
				{
					var it = e.Item;
					sb.Append($"  pos={e.Pos} itemId=0x{it?.ItemId:X16} stats=[{string.Join(",", (it?.StatLists ?? []).Select(s => $"{s.Stat}:{s.Value}"))}]");
					sb.Append($" slotMaps={it?.SlotMaps.Count}");
					if (it?.SlotMaps.Count > 0)
						sb.Append($" [{string.Join(",", it.SlotMaps.Select(m => $"{m.Slot}->tmpl{m.Item?.Template1}"))}]");
					sb.Append($" creator='{it?.CreatorName}'");
					var d = it?.ItemData;
					if (d != null)
						sb.Append($" tmpl={d.Template1}/{d.Template2} lvl={d.Level} hash=0x{d.Hash:X8}");
					sb.AppendLine();
				}
			}

			File.WriteAllText("/tmp/strie_containers.txt", sb.ToString());
			_output.WriteLine(sb.ToString());
		}
	}
}
