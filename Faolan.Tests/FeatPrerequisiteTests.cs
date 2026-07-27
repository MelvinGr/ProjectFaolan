using System.Collections.Generic;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// The feat-tree PREREQUISITE rule (Gitea #251), against the client's own edges (FeatPrereq, from RDB 1000042).
	//
	// The subtlety these gates exist for: the graph links ADJACENT RANKS (80004 requires 80003) while a character's
	// wire feat list carries only the HIGHEST rank per base - retail never stores the intermediate ranks. An exact-id
	// check would reject every captured retail character (NecroL80 19 unmet, DemoL80 18, Hakotep 3); the same-base rule
	// clears all three at zero.
	public class FeatPrerequisiteTests
	{
		[Theory]
		[InlineData(80004u, 80003u, true)]  // rank 4 held, rank 3 required - the retail case
		[InlineData(80004u, 80004u, true)]  // exactly what is required
		[InlineData(80004u, 80000u, true)]  // the unlock, implied by any rank
		[InlineData(80003u, 80004u, false)] // a LOWER rank does not satisfy a higher requirement
		[InlineData(80104u, 80003u, false)] // a different base never satisfies, at any rank
		public void Satisfies_IsSameBaseAtAnEqualOrHigherRank(uint held, uint required, bool expected)
		{
			Assert.Equal(expected, FeatRules.Satisfies(held, required));
		}

		// A retail-shaped set: only the top rank of each base is held, and the graph's per-rank edges still resolve.
		[Fact]
		public void MissingPrerequisites_AcceptsARetailShapedSet()
		{
			uint[] held = [80004, 80104];
			var prereqs = new Dictionary<uint, uint[]>
			{
				[80004] = [80003],
				[80104] = [80103]
			};

			Assert.Empty(FeatRules.MissingPrerequisites(held, prereqs));
		}

		// The rule still BITES: a feat whose prerequisite is on another base the character does not hold is rejected.
		[Fact]
		public void MissingPrerequisites_RejectsAFeatWhoseRequirementIsAbsent()
		{
			uint[] held = [80004];
			var prereqs = new Dictionary<uint, uint[]> { [80004] = [90000] };

			var missing = FeatRules.MissingPrerequisites(held, prereqs);

			Assert.Single(missing);
			Assert.Equal((80004u, 90000u), missing[0]);
		}

		// Training a feat together with its own prerequisite in one request is legal - the check runs against the
		// RESULTING set, so order within the request must not matter.
		[Fact]
		public void MissingPrerequisites_AllowsAFeatAndItsPrerequisiteInTheSameSet()
		{
			uint[] held = [300000, 6001];
			var prereqs = new Dictionary<uint, uint[]> { [300000] = [6001] };

			Assert.Empty(FeatRules.MissingPrerequisites(held, prereqs));
		}

		// A feat with no edges at all is unconstrained - most of the catalog has none.
		[Fact]
		public void MissingPrerequisites_IgnoresFeatsWithNoEdges()
		{
			Assert.Empty(FeatRules.MissingPrerequisites([1234u], new Dictionary<uint, uint[]>()));
		}
	}
}
