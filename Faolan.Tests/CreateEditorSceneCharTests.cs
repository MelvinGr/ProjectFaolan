using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The create-editor scene characters (the SimpleCharFullUpdateIIR / FA015807 records the initial burst streams)
	// are built from typed data and reproduce retail byte-for-byte.
	//
	// The data is `CreateEditorScene`, generated from the burst and emitted as typed values, so the build side touches
	// no captured bytes at all. It needs no DB table behind it: the scene is five fixed pedestals, not content anyone
	// edits.
	//
	// Some pre-IIR words are the retail server's own uninitialised memory. Resolving the slots they sit in names them
	// `PvPAnnihilationMatchesWonSeasonal`, `PvPCTFMatchesLostSeasonal`, `Onslaught2SpecialsKilled` and the like - a
	// create-editor pedestal has no seasonal PvP record, so `0x7151E7E0` in that slot is a leaked pointer, not data.
	// They are reproduced because the gate is byte-exactness against retail; dropping them would be a behavioural
	// change needing an in-client check. The live avatar carries none of them.
	public class CreateEditorSceneCharTests
	{
		private readonly ITestOutputHelper _o;

		public CreateEditorSceneCharTests(ITestOutputHelper o)
		{
			_o = o;
		}

		// Each pedestal is rebuilt from the generated typed data - no captured bytes involved on the build side at all -
		// and must reproduce the captured record exactly. This is what makes CreateEditorScene a parse of the capture
		// rather than a transcription of it: a wrong emit cannot pass here.
		[Fact]
		public void EverySceneChar_BuildsFromTypedData_ByteExact()
		{
			var captured = ExtractBurstCharRecords();
			var scene = Core.Network.Constants.CreateEditorScene.All;
			Assert.Equal(captured.Count, scene.Count);

			for (var i = 0; i < captured.Count; i++)
			{
				var built = SimpleCharFullUpdateCodec.Build(scene[i]());
				Assert.Equal(Convert.ToHexString(captured[i].Record), Convert.ToHexString(built));
			}
		}

		// The session's playfield token is stamped on the model and rides the ordinary codec - it is not patched into
		// bytes. Everything else in the record must be untouched by that, which is what this pins.
		[Fact]
		public void ThePlayfieldToken_IsTheOnlyThingTheSessionChanges()
		{
			var captured = ExtractBurstCharRecords()[0].Record;
			const uint liveToken = 0x004FE1A8;

			var scene = Core.Network.Constants.CreateEditorScene.Pedestals[0]();
			scene.Playfield = liveToken;
			var rebuilt = SimpleCharFullUpdateCodec.Build(scene);

			// only the 4-byte playfield varint changes (transform `18 <varint>` at body offset 53 for a char, i.e. 57
			// into the record)
			Assert.Equal(captured.Length, rebuilt.Length);
			const int varintOffset = 4 + 53;
			for (var i = 0; i < captured.Length; i++)
			{
				if (i is < varintOffset or >= varintOffset + 4)
					Assert.Equal(captured[i], rebuilt[i]);
			}
		}

		// SimpleChar records are framed by the burst sender as [u32 len][record]; the record is
		// [u32 0xFA015807][u32 0x0000C350][u32 handle][u32 0] 2C ... . Returns (name, record) for each, from
		// InitialBurst.bin.
		private static List<(string Name, byte[] Record)> ExtractBurstCharRecords()
		{
			var burst = Golden.Bytes("InitialBurst");
			var op = new byte[] { 0xFA, 0x01, 0x58, 0x07 };
			var result = new List<(string, byte[])>();
			for (var i = 0; i + 16 < burst.Length;)
			{
				var at = IndexOf(burst, op, i);
				if (at < 0)
					break;

				i = at + 1;
				if (burst.ReadU32Be(at + 4) != 0x0000C350u)
					continue; // body must open with the character entity-class tag

				var recordLen = (int)burst.ReadU32Be(at - 4);
				var record = burst.AsSpan(at, recordLen).ToArray();
				SpawnBodyReader.TryReadCharacterMessage(record.AsSpan(4).ToArray(), out var read);
				result.Add((read.Message?.Name ?? "?", record));
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
