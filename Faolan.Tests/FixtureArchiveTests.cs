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
}
