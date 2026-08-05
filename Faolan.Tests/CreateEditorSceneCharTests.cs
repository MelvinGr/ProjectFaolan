using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Proves the create-editor SCENE CHARACTERS (the SimpleCharFullUpdateIIR / FA015807 records the initial burst
	// streams) are CONSTRUCTED, not replayed: CreateEditorCharBuilder.Reconstruct PARSES each into the typed
	// SimpleCharFullUpdateIIR and re-BUILDS it, setting the session's playfield on the model, and reproduces the
	// captured record byte-for-byte (issue #189, "the 5 remaining").
	//
	// ⚠️ Nothing inside the record is carried verbatim any more (#364). This gate used to say the pre-IIR held
	// "session-specific heap pointers/handles that no DB column holds" and that 5 of 6 post-IIR tables were a form
	// the codecs refuse - both were true when written and neither survived #466/#475/#477: the pre-IIR is
	// SpawnPreIir's 53 NAMED stat words and the post-IIR is SpawnPostIir's typed stream, so all six records parse
	// and re-encode whole.
	//
	// And the words that looked irreducible are the retail server's own uninitialised memory. Resolving the slots
	// they sit in names them `PvPAnnihilationMatchesWonSeasonal`, `PvPCTFMatchesLostSeasonal`,
	// `Onslaught2SpecialsKilled` and the like - a create-editor pedestal has no seasonal PvP record, so
	// `PvPAnnihilationMatchesWonSeasonal = 0x7151E7E0` is a leaked pointer in a stat slot, not data. The live
	// avatar in the same burst carries none of them (only the category word + the 15.0 constant), which is exactly
	// SpawnPreIirs.Minimal. What is still not done is SOURCING the scene from rows rather than from these bytes.
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

			foreach (var (name, record) in records)
			{
				// Reconstruct takes the BODY and returns the whole RECORD (sub-opcode included), so the caller never
				// names the IIR - #542.
				var rebuilt = CreateEditorCharBuilder.Reconstruct(record[4..], CreateEditorCharBuilder.EditorPlayfieldInstance);
				_o.WriteLine($"{name}: {(Convert.ToHexString(record) == Convert.ToHexString(rebuilt!) ? "ok" : "DIFF")}");
				Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(rebuilt!));
			}
		}

		[Fact]
		public void Reconstruct_ThreadsPlayfieldInstance()
		{
			var record = ExtractBurstCharRecords()[0].Record;
			const uint liveToken = 0x004FE1A8;

			var rebuilt = CreateEditorCharBuilder.Reconstruct(record[4..], liveToken);

			// only the 4-byte playfield varint changes (transform `18 <varint>` at body offset 53 for a char, i.e. 57
			// into the record)
			Assert.Equal(record.Length, rebuilt!.Length);
			const int varintOffset = 4 + 53;
			for (var i = 0; i < record.Length; i++)
			{
				if (i is < varintOffset or >= varintOffset + 4)
					Assert.Equal(record[i], rebuilt[i]);
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
