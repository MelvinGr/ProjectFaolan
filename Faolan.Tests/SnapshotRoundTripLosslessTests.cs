using System;
using System.IO;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Protobuf;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// Regression guard for the DB-container world-entry path (RetailLoginReplay.BuildBeachInventoryPacket): the typed
	// protobuf round-trip it relies on (FromProtobuf<ProtoFullCharacterIIR> -> Inventories.Clear()/Add() ->
	// ToProtobufBytes) MUST be byte-lossless for everything we do NOT change. A plain round-trip with NO inventory
	// edit must reproduce the captured root exactly; otherwise the rebuilt root would differ from a valid one before
	// we even touch the containers, and the client rejects it ("ReadSubClass for IIR 1937403385
	// (FullPlayerCharacterIIR) failed"). This proves the round-trip is NOT the source of the 2026-06-13 regression -
	// it does not drop the per-container 0x18 subclass version byte (the suspect in docs/archive/db-container-handoff.md).
	public class SnapshotRoundTripLosslessTests
	{
		private const string CapturesDir = "/home/melvin/Development/C#/Project Faolan/Captures";
		private readonly ITestOutputHelper _o;

		public SnapshotRoundTripLosslessTests(ITestOutputHelper o)
		{
			_o = o;
		}

		[Theory, InlineData("strie-login-inventory.bin"), InlineData("account-stricken997-character-Strie-login-randomActions-bigboy.bin")]
		public void TypedRoundTrip_NoEdit_IsByteLossless(string bin)
		{
			if (!File.Exists(Path.Combine(CapturesDir, bin)))
				return;

			var verbatim = SplitRoot(bin);
			var typed = ((ReadOnlySpan<byte>)verbatim).FromProtobuf<ProtoFullCharacterIIR>();
			var roundTripped = typed.ToProtobuf();

			_o.WriteLine($"{bin}: verbatim={verbatim.Length} roundTripped={roundTripped.Length} inventories={typed.Inventories.Count}");

			if (!roundTripped.SequenceEqual(verbatim))
			{
				var firstDiff = Enumerable.Range(0, Math.Min(verbatim.Length, roundTripped.Length)).FirstOrDefault(i => verbatim[i] != roundTripped[i], -1);
				_o.WriteLine($"FIRST DIFF at offset {firstDiff} (len delta {roundTripped.Length - verbatim.Length})");
				_o.WriteLine($"verbatim    : {Convert.ToHexString(verbatim.AsSpan(Math.Max(0, firstDiff - 4), Math.Min(32, verbatim.Length - Math.Max(0, firstDiff - 4))))}");
				_o.WriteLine($"roundTripped: {Convert.ToHexString(roundTripped.AsSpan(Math.Max(0, firstDiff - 4), Math.Min(32, roundTripped.Length - Math.Max(0, firstDiff - 4))))}");
			}

			Assert.True(roundTripped.SequenceEqual(verbatim), $"typed round-trip is NOT byte-lossless ({verbatim.Length} -> {roundTripped.Length})");
		}

		private static byte[] SplitRoot(string bin)
		{
			var packet = new ConanPacket(File.ReadAllBytes(Path.Combine(CapturesDir, bin)));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = FullCharSnapshotReader.ParseInventoryPacketBody(stream);
			return parts.Root.ToProtobuf();
		}
	}
}
