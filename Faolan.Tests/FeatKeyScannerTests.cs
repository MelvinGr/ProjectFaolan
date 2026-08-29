using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core.Crypt;
using Faolan.Importer.Captures;
using Xunit;

namespace Faolan.Tests
{
	// ⭐ The POSITIVE CONTROL for `FeatKeyScanner` - the thing whose absence let it ship broken twice over (#895).
	//
	// The scanner's whole output is a NEGATIVE: "no captured packet contains a key that opens these records". That
	// answer is worthless unless the instrument can find a key that IS there, and for the corpus scan it never could:
	//
	//   1. it reconstructed every candidate as four LITTLE-endian words, in a protocol that is big-endian and whose
	//      one key-carrying reader (`AddSpecialAbilityCodec`, the 0x2052 ability grant) takes four `ReadU32Be`; and
	//   2. its oracle asserted that a correct key yields `[u32 1000020][u32 id]`, when `RdbReader.ReadDecrypted`
	//      decrypts and THEN calls `UnwrapIOz1` - what comes out from under the cipher is an `IOz1` container.
	//
	// Both were invisible because a broken search and an empty corpus produce the same output. The corpus check that
	// caught them (two ability records whose keys we already hold, planted in the target list) needs the private data
	// repo; this reproduces it synthetically so a plain clone gates the same property.
	public class FeatKeyScannerTests
	{
		// Any 128-bit value; the point is that the scanner recovers exactly this one.
		private static readonly uint[] Key = [0x798691A8, 0x9412F0F2, 0xAB787BFE, 0x1FE8C24A];

		private const uint IOz1Magic = 0x317A4F49; // "IOz1" read little-endian, the container the plaintext opens with
		private const uint TemplateId = 3663271;

		[Fact]
		public void AKeyThatIsInACapture_IsFound()
		{
			var dump = WriteDump(BodyCarrying(Key));
			try
			{
				var hits = FeatKeyScanner.Scan([dump], [new FeatKeyScanner.Target(TemplateId, EncryptedFirstBlock(Key, IOz1Magic, 412))]);

				var hit = Assert.Single(hits);
				Assert.Equal(TemplateId, hit.TemplateId);
				Assert.Equal(Key, hit.Key);
				// The word order the wire uses. A hit reported as `le` here would mean the scan is matching by
				// accident rather than reproducing the grant's own reading.
				Assert.StartsWith("be", hit.WordOrder);
			}
			finally
			{
				File.Delete(dump);
			}
		}

		// The negative half: a capture that does NOT carry the key must not produce a hit, or the control above proves
		// nothing. 16 bytes of a plausible-looking body, none of them the key.
		[Fact]
		public void AKeyThatIsNotInACapture_IsNotFound()
		{
			var dump = WriteDump([.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
			try
			{
				Assert.Empty(FeatKeyScanner.Scan([dump], [new FeatKeyScanner.Target(TemplateId, EncryptedFirstBlock(Key, IOz1Magic, 412))]));
			}
			finally
			{
				File.Delete(dump);
			}
		}

		// ⚠️ And the oracle has to be the CONTAINER, not the template: a record whose first plaintext block is
		// `[1000020][id]` is the un-wrapped shape, which does occur, so both are accepted - but the wrapped one is
		// what an encrypted 1000020 record actually opens with, and asserting only the bare form is bug 2 above.
		[Fact]
		public void TheBareTemplateForm_IsAlsoAccepted()
		{
			var dump = WriteDump(BodyCarrying(Key));
			try
			{
				var hits = FeatKeyScanner.Scan([dump], [new FeatKeyScanner.Target(TemplateId, EncryptedFirstBlock(Key, 1000020, TemplateId))]);

				Assert.Equal(Key, Assert.Single(hits).Key);
			}
			finally
			{
				File.Delete(dump);
			}
		}

		// The record's first 8 bytes as the pack file holds them: the plaintext pair, TEA-encrypted with `key`.
		private static byte[] EncryptedFirstBlock(uint[] key, uint v0, uint v1)
		{
			Tea.EncryptBlock(ref v0, ref v1, key);
			var block = new byte[8];
			BinaryPrimitives.WriteUInt32LittleEndian(block, v0);
			BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), v1);

			return block;
		}

		// A packet body with the key inside it, BIG-endian, at a deliberately unaligned offset - the scanner's windows
		// are byte-aligned for exactly this reason.
		private static byte[] BodyCarrying(uint[] key)
		{
			var body = new byte[3 + 16 + 5];
			for (var i = 0; i < key.Length; i++)
				BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(3 + i * 4), key[i]);

			return body;
		}

		private static string WriteDump(byte[] body)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-keyscan-{Path.GetRandomFileName()}.tshark.json");
			var doc = new Dictionary<string, object> { ["Packets"] = new[] { new Dictionary<string, string> { ["BodyHex"] = Convert.ToHexStringLower(body) } } };
			File.WriteAllText(path, JsonSerializer.Serialize(doc));

			return path;
		}
	}
}
