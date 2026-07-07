using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.GameServer.CreateFlow;
using Faolan.Tests.Support;
using Xunit;
using Xunit.Abstractions;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Proves the create-editor SCENE CHARACTERS (the SimpleCharFullUpdateIIR / FA015807 records the initial burst
	// streams) are CONSTRUCTED, not replayed: CreateEditorCharBuilder.Reconstruct rebuilds each from its own bytes
	// (transform from the typed pose + ProtoCoreCharacterIIR round-trip, pre-IIR scaffold carried verbatim, the
	// session playfield threaded) and reproduces the captured record byte-for-byte (issue #189, "the 5 remaining").
	// DB-sourcing these like the dynels is blocked on the un-lifted pre-IIR scaffold (#185/#54) - the captured
	// NpcSpawns rows are from a different session - so self-reconstruction is the byte-exact path.
	public class CreateEditorSceneCharTests
	{
		private readonly ITestOutputHelper _o;

		public CreateEditorSceneCharTests(ITestOutputHelper o)
		{
			_o = o;
		}

		[Fact]
		public void EverySceneChar_ReconstructsFromItself_ByteExact()
		{
			var records = ExtractBurstCharRecords();
			Assert.Equal(6, records.Count); // 5 fixed scene chars + the live "Character..." preview

			foreach (var (name, body) in records)
			{
				var rebuilt = CreateEditorCharBuilder.Reconstruct(body, CreateEditorCharBuilder.EditorPlayfieldInstance);
				_o.WriteLine($"{name}: {(Convert.ToHexString(body) == Convert.ToHexString(rebuilt) ? "ok" : "DIFF")}");
				Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(rebuilt));
			}
		}

		[Fact]
		public void Reconstruct_ThreadsPlayfieldInstance()
		{
			var body = ExtractBurstCharRecords()[0].Body;
			const uint liveToken = 0x004FE1A8;

			var rebuilt = CreateEditorCharBuilder.Reconstruct(body, liveToken);

			// only the 4-byte playfield varint changes (transform `18 <varint>` at body offset 53 for a char)
			Assert.Equal(body.Length, rebuilt.Length);
			const int varintOffset = 53;
			for (var i = 0; i < body.Length; i++)
			{
				if (i < varintOffset || i >= varintOffset + 4)
					Assert.Equal(body[i], rebuilt[i]);
			}
		}

		// SimpleChar records are framed by the Iir() helper as [u32 len][u32 0xFA015807][body]; the body is
		// [u32 0x0000C350][u32 handle][u32 0] 2C ... . Returns (name, body) for each, from InitialBurst.bin.
		private static List<(string Name, byte[] Body)> ExtractBurstCharRecords()
		{
			var burst = Golden.Bytes("InitialBurst");
			var op = new byte[]
			{
				0xFA,
				0x01,
				0x58,
				0x07
			};
			var result = new List<(string, byte[])>();
			for (var i = 0; i + 16 < burst.Length;)
			{
				var at = IndexOf(burst, op, i);
				if (at < 0)
					break;

				i = at + 1;
				if (BinaryPrimitives.ReadUInt32BigEndian(burst.AsSpan(at + 4, 4)) != 0x0000C350u)
					continue; // body must open with the character entity-class tag

				var bodyLen = (int)BinaryPrimitives.ReadUInt32BigEndian(burst.AsSpan(at - 4, 4)) - 4;
				var body = burst.AsSpan(at + 4, bodyLen).ToArray();
				PlayerSpawnReader.TryReadCharacterMessage(body, out var read);
				result.Add((read.Message?.Name ?? "?", body));
			}

			return result;
		}

		private static int IndexOf(byte[] haystack, byte[] needle, int start)
		{
			for (var i = start; i <= haystack.Length - needle.Length; i++)
			{
				var ok = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return i;
			}

			return -1;
		}
	}
}
