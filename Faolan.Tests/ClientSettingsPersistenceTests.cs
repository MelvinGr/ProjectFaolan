using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Network.Codecs.PlayerAgent;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Storing the client-authored settings/preview blobs, against a real throwaway SQLite DB.
	//
	// The half-upsert is the part worth exercising for real: the client uploads the settings and the preview as separate
	// messages on the same socket, so each write must leave the other column alone. Storing one over the other would
	// silently lose a player's UI layout or blank the select screen, and neither shows up until the next login.
	public class ClientSettingsPersistenceTests
	{
		[Fact]
		public async Task SettingsAndPreview_AreStoredIndependently_AndSurviveEachOther()
		{
			var (repo, _, path) = NewDb();
			try
			{
				var settings = UpdateCharacterSettingsCodec.Parse(TestPaths.AssetBytes("playeragent-update-character-settings.bin"));
				var preview = UpdateCharacterPreviewCodec.Parse(TestPaths.AssetBytes("playeragent-update-character-preview.bin"));

				await repo.SaveCharacterClientSettings(CharacterId, settings.Buffer.Payload, null);
				await repo.SaveCharacterClientSettings(CharacterId, null, preview.Buffer.Payload);

				var stored = await repo.GetCharacterClientSettings(CharacterId);
				Assert.NotNull(stored);
				Assert.Equal(settings.Buffer.Payload, stored.Settings);
				Assert.Equal(preview.Buffer.Payload, stored.Preview);

				// Stored verbatim, so the checksum the client will compare against is reproducible - which is the whole
				// reason the payload is kept compressed rather than re-encoded.
				Assert.Equal(settings.CheckSum, ClientSettingsBufferCodec.Parse(stored.Settings!).CheckSum);

				// And it serves straight back out as the same bytes the client uploaded (addressed to the id the capture
				// used, since the body carries it).
				Assert.Equal(TestPaths.AssetBytes("playeragent-update-character-preview.bin"),
					SendCharacterPreviewCodec.Build(new CharacterPreview(preview.CharacterId, ClientSettingsBufferCodec.Parse(stored.Preview!))));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task ReUpload_ReplacesTheBlob_AndTheAccountHalfIsSeparate()
		{
			var (repo, options, path) = NewDb();
			try
			{
				await repo.SaveCharacterClientSettings(CharacterId, [1, 2, 3], null);
				await repo.SaveCharacterClientSettings(CharacterId, [4, 5], null);

				await using var ctx = new DatabaseContext(options);
				var rows = await ctx.CharacterClientSettings.Where(s => s.CharacterId == CharacterId).ToListAsync(TestContext.Current.CancellationToken);

				Assert.Single(rows);
				Assert.Equal<byte[]>([4, 5], rows[0].Settings!);

				// Subscription settings live on the account - UpdateSubscriptionSettings carries no id at all, so the
				// socket's authentication is the addressing.
				var accountId = (await ctx.Characters.SingleAsync(c => c.Id == CharacterId, TestContext.Current.CancellationToken)).AccountId;
				var subscription = UpdateSubscriptionSettingsCodec.Parse(TestPaths.AssetBytes("playeragent-update-subscription-settings.bin"));
				await repo.SetAccountSubscriptionSettings(accountId, subscription.Buffer.Payload);

				var account = await repo.GetAccount(accountId);
				Assert.Equal(subscription.Buffer.Payload, account!.SubscriptionSettings);
				Assert.Equal<byte[]>([4, 5], (await repo.GetCharacterClientSettings(CharacterId))!.Settings!);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private const uint CharacterId = 0x01000001;

		private static (DatabaseRepository Repo, DbContextOptions<DatabaseContext> Options, string Path) NewDb()
		{
			var (options, path) = TestDb.NewSeededDatabase(CharacterId);
			return (new DatabaseRepository(new Factory(options)), options, path);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
