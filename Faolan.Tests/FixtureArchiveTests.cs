using System.Linq;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The fixture archive names a capture only by an OPAQUE ID (#465). The dumps in the private capture repo are named
	// after the characters that were played, and a manifest of 50k corpus entries carried those names ~39,000 times -
	// which meant every checkout shipped them. The id is the head of the SHA-256 of the dump's file name: stable
	// across re-harvests, distinct per capture, and meaningless without the repo.
	//
	// These gates keep a re-harvest from putting the names back: the writer hashes at harvest time and the repack
	// hashes what an older archive stored, and this is what makes both a checked property of the COMMITTED file.
	public class FixtureArchiveTests
	{
		[Fact]
		public void EveryCorpusEntry_NamesItsCaptureByAnOpaqueId()
		{
			var offenders = FixtureArchive.Corpus
				.Select(e => e.Source)
				.Distinct()
				.Where(s => !FixtureArchiveWriter.IsCaptureId(s))
				.ToList();

			Assert.True(offenders.Count == 0,
				$"{offenders.Count} corpus source(s) are not an 8-hex-digit capture id - the archive is carrying capture NAMES again (#465). " +
				"Repack it: dotnet run --project Faolan.Importer -- --report fixture-repack");

			// The corpus spans many captures; the ids must keep them apart, or provenance collapses into one bucket.
			Assert.True(FixtureArchive.Corpus.Select(e => e.Source).Distinct().Count() > 100,
				"the corpus should span more than a hundred captures");
		}

		// The recipe is the contract with `--report fixture-source`, which hashes every dump's name to find the one an
		// id came from - so it is pinned here on a name that is not a capture's.
		[Fact]
		public void CaptureId_IsTheHeadOfTheSha256OfTheDumpFileName()
		{
			// echo -n 'example.tshark.json' | sha256sum | cut -c1-8
			Assert.Equal("71407844", FixtureArchiveWriter.CaptureId("example.tshark.json"));
			Assert.True(FixtureArchiveWriter.IsCaptureId(FixtureArchiveWriter.CaptureId("example.tshark.json")));

			// A dump's file name can never read as an id, which is what lets the repack convert an older archive
			// without a flag: the two forms are disjoint.
			Assert.False(FixtureArchiveWriter.IsCaptureId("example.tshark.json"));
			Assert.False(FixtureArchiveWriter.IsCaptureId("7140784A")); // upper-case hex is not the form
			Assert.False(FixtureArchiveWriter.IsCaptureId("7140784")); // nor a short one
		}
	}

	// #465 option B - the tool that substitutes personal names inside the packet BYTES by an equal-length pseudonym.
	// These gate the substitution PRIMITIVE on synthetic bytes (never a real name in test source): framed-only,
	// equal-length, prefix-safe, case-insensitive. The archive round-trip is covered by the equal-length guarantee
	// the primitive enforces - a same-length overwrite moves no offset, so a byte-exact golden reading the archive
	// stays byte-exact by construction.
	public class FixtureSubstituteTests
	{
		private static byte[] Framed(byte tag, string name)
		{
			var n = System.Text.Encoding.Latin1.GetBytes(name);
			var b = new byte[2 + n.Length];
			b[0] = tag;                 // wiretype-2 tag (low 3 bits == 2)
			b[1] = (byte)n.Length;      // single-byte length varint
			n.CopyTo(b, 2);
			return b;
		}

		[Fact]
		public void ReplacesAFramedName_InPlace_PreservingLengthAndSurroundings()
		{
			// [0x0a=field1,wt2][len=6]"Aaaaaa" wrapped in unrelated bytes on both sides
			var body = new byte[] { 0xde, 0xad }.Concat(Framed(0x0a, "Aaaaaa")).Concat(new byte[] { 0xbe, 0xef }).ToArray();
			var before = (byte[])body.Clone();

			var changed = FixtureArchiveWriter.ReplaceFramed(body,
				System.Text.Encoding.Latin1.GetBytes("Aaaaaa"), System.Text.Encoding.Latin1.GetBytes("Bbbbbb"));

			Assert.Equal(1, changed);
			Assert.Equal(before.Length, body.Length);            // length unchanged
			Assert.Equal(before[..4], body[..4]);                // tag+len+prefix bytes untouched
			Assert.Equal(before[^2..], body[^2..]);              // trailing bytes untouched
			Assert.Equal("Bbbbbb", System.Text.Encoding.Latin1.GetString(body, 4, 6));
			Assert.Equal(0, FixtureArchiveWriter.CountFramed(body, System.Text.Encoding.Latin1.GetBytes("Aaaaaa")));
		}

		[Fact]
		public void LeavesAnUnframedByteCoincidence_Alone()
		{
			// The name bytes appear, but not preceded by [wt2 tag][len] - a coincidence, not a wire string.
			var name = System.Text.Encoding.Latin1.GetBytes("Aaaaaa");
			var body = new byte[] { 0x00, 0x05 }.Concat(name).ToArray(); // len byte is 5, not 6

			var changed = FixtureArchiveWriter.ReplaceFramed(body, name, System.Text.Encoding.Latin1.GetBytes("Bbbbbb"));
			Assert.Equal(0, changed);
			Assert.Equal("Aaaaaa", System.Text.Encoding.Latin1.GetString(body, 2, 6));
		}

		[Fact]
		public void DoesNotSubstituteAShorterNameInsideAFramedLongerOne()
		{
			// The 6-char "Aaaaaa" is the prefix of the framed 7-char "Aaaaaab"; the len byte there is 7, so the
			// framed check for the 6-char name fails and the longer field is left intact. This is the prefix trap.
			var body = Framed(0x0a, "Aaaaaab");
			var changed = FixtureArchiveWriter.ReplaceFramed(body,
				System.Text.Encoding.Latin1.GetBytes("Aaaaaa"), System.Text.Encoding.Latin1.GetBytes("Bbbbbb"));

			Assert.Equal(0, changed);
			Assert.Equal("Aaaaaab", System.Text.Encoding.Latin1.GetString(body, 2, 7));
		}

		[Fact]
		public void MatchesCaseInsensitively_LikeTheScanner()
		{
			// The wire carries the name as typed (capitalised); the block list is lower-case.
			var body = Framed(0x0a, "aaaaaa");
			var changed = FixtureArchiveWriter.ReplaceFramed(body,
				System.Text.Encoding.Latin1.GetBytes("AAAAAA"), System.Text.Encoding.Latin1.GetBytes("bbbbbb"));

			Assert.Equal(1, changed);
			Assert.Equal("bbbbbb", System.Text.Encoding.Latin1.GetString(body, 2, 6));
		}
	}
}
