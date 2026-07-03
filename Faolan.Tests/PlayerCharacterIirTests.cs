using System;
using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// #258 follow-up: the base player-spawn ProtoCoreCharacterIIR is now built from code (PlayerCharacterIir) instead
	// of the captured PlayerCharacterIirTemplate.bin blob. These prove the bare template (PlayerCharacterIir.Build with
	// no character) reproduces the .bin exactly except for the worn-gear entries - which Build always replaces with the
	// character's own gear, so they are intentionally not reproduced. The .bin survives only as this golden oracle.
	public class PlayerCharacterIirTests
	{
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/PlayerCharacterIirTemplate.bin";

		[Fact]
		public void BuildBareTemplate_EqualsTheTemplate_ExceptWornGear()
		{
			var template = File.ReadAllBytes(TemplatePath);

			// The template with its worn-gear entries stripped (Build always replaces them per character).
			var stripped = ((ReadOnlySpan<byte>)template).FromProtobuf<ProtoCoreCharacterIIR>();
			stripped.InventoryEntries.Clear();
			var strippedBytes = stripped.ToProtobufBytes();

			var built = PlayerCharacterIir.Build(null, null).ToProtobufBytes();

			Assert.Equal(strippedBytes, built);
		}

		// Sanity: the captured template carried exactly 4 worn-gear entries (Strie's), which we deliberately drop -
		// guards the assumption above (if a future template shape changed, the equality test alone could mask it).
		[Fact]
		public void Template_CarriesOnlyWornGearThatIsAlwaysReplaced()
		{
			var template = File.ReadAllBytes(TemplatePath);
			var msg = ((ReadOnlySpan<byte>)template).FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal(4, msg.InventoryEntries.Count);
			Assert.Empty(PlayerCharacterIir.Build(null, null).InventoryEntries);
		}
	}
}
