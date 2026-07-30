using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #417: the shipped game data must carry no SESSION state harvested from the captures - no real character name, no
	// guild name, no retail server name. The one place it leaked was the dialogue screens: a captured `remoteformat`
	// carries the capturing player's name/guild and an NPC guild label naming their retail server ("Not in guild
	// [Legacy of Cimmeria]") in three `<stringentry>` parameters, all of which the runtime regenerates from the live
	// player + realm anyway (RemoteFormatXml.WithLivePlayer). The importer blanks them (ToStoredRow); this gate is what
	// keeps a future import - or a hand-edit - from putting them back into faolan.db and the committed faolan.sql seed.
	//
	// Runs against the fixture BUILT from faolan.sql, so it gates the seed that actually ships.
	public class SeedSessionDataTests
	{
		[Fact]
		public async Task ConversationScreens_CarryNoCapturedNameOrGuildOrServer()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var parameters = await db.ConversationScreenParameters.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
			Assert.NotEmpty(parameters);

			// Every session slot is present-but-empty: the parameter still exists (the client parses the screen by
			// parameter name and order, and `flags` drives the localized declension) with no string in it. Now that
			// the inner element is columnized, "no string in it" is simply an empty Text column.
			foreach (var p in parameters.Where(p => RemoteFormatSession.SessionSlots.Contains(p.Name) && p.Kind == InnerElementKind.StringEntry))
				Assert.Equal("", p.Text);

			// And nothing ELSE in a screen is a literal string entry: every other parameter is a <localized …>
			// reference into the client's LDB, which is content. So a captured literal can only be a session slot.
			var literals = parameters.Where(p => p.Kind == InnerElementKind.StringEntry && !RemoteFormatSession.SessionSlots.Contains(p.Name)).ToList();
			Assert.True(literals.Count == 0, $"Unexpected literal string entries in dialogue screens: {string.Join(", ", literals.Take(5).Select(p => $"{p.Name}={p.Text}"))}");
		}
	}
}
