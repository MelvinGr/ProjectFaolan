using System.Linq;
using Faolan.Mcp;
using Xunit;

namespace Faolan.Tests
{
	// `Mcp:Port` is a single global key and MCP runs only in a realm-hosting process, so one port for all of them
	// would have every realm host bind 7099 and the second die on `address already in use`. The port is the
	// configured base plus the realm's offset from the first realm.
	public class McpPortPerRealmTests
	{
		[Fact]
		// The offset rather than the raw id is the point: realm 1 keeps the configured port exactly, so every existing
		// single-realm setup still answers on 7099 and no `claude mcp add` has to change.
		public void TheFirstRealm_KeepsTheConfiguredPort()
		{
			Assert.Equal(7099, FaolanMcpServiceCollectionExtensions.McpPortFor(7099, 1));
			Assert.Equal(7200, FaolanMcpServiceCollectionExtensions.McpPortFor(7200, 1));
		}

		[Theory]
		[InlineData(2u, 7100)]
		[InlineData(3u, 7101)]
		[InlineData(9u, 7107)]
		public void ALaterRealm_TakesTheNextPort(uint realmId, int expected)
		{
			Assert.Equal(expected, FaolanMcpServiceCollectionExtensions.McpPortFor(7099, realmId));
		}

		[Fact]
		// Every realm in a run gets a different port - the property the bug violated.
		public void EveryRealmGetsItsOwnPort()
		{
			var ports = new[] { 1u, 2u, 3u, 4u, 5u }.Select(id => FaolanMcpServiceCollectionExtensions.McpPortFor(7099, id)).ToArray();

			Assert.Equal(ports.Length, ports.Distinct().Count());
		}

		[Fact]
		// Realm ids are not promised to be small or contiguous. A far-off id would run a ushort past its end, so the
		// derivation falls back to the base rather than wrapping into a port that means something else - a collision is
		// recoverable with an explicit `--Mcp:Port`, binding a wrapped-around port is not diagnosable at all.
		public void AnOutOfRangeRealmId_FallsBackToTheBasePort_RatherThanWrapping()
		{
			Assert.Equal(7099, FaolanMcpServiceCollectionExtensions.McpPortFor(7099, 1_000_000));
			Assert.Equal(7099, FaolanMcpServiceCollectionExtensions.McpPortFor(7099, uint.MaxValue));
		}
	}
}
