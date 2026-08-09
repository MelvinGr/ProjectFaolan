using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #654: the guild bank's ITEM vault.
	//
	// Pinned by the captured session (demol80-demonologist_login_gameplay_annotated_3, 09:45:09-09:46:12) with the
	// player narrating each step in /say - "deposidet an item", "withdrew it".
	//
	// ⚠️ The vault is owned by the GUILD, not by the member who filled it. That is the whole reason `ItemInstance`
	// grew `OwnerGuildId`, and it is what these tests assert hardest: an item in the vault must not hang off the
	// depositor, or leaving the guild (or being deleted) would take it with them.
	public class GuildBankVaultTests : IDisposable
	{
		private const int BagSlot = 18;
		private const uint Depositor = 0x0802E5D4;
		private const uint GuildId = 552007; // 0x86C47, the captured guild
		private const uint OtherGuildId = 999001;
		private const uint Template = 220208;
		private const int VaultSlot = 0;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		// The s2c echo, byte-exact against retail's own two replies - the deposit and the withdraw, which are the
		// same six words with the halves swapped.
		[Theory]
		[InlineData("00000068110099bd000000120000006d00086c4700000000")] // deposit: bag slot 18 -> vault slot 0
		[InlineData("0000006d00086c47000000000000006811 0099bd00000019")] // withdraw: vault slot 0 -> bag slot 25
		public void TheEcho_IsByteExactAgainstTheCapture(string captured)
		{
			captured = captured.Replace(" ", "");
			var parsed = GuildBankMoveCodec.Parse(Convert.FromHexString(captured));

			Assert.Equal(captured, Convert.ToHexStringLower(GuildBankMoveCodec.Build(parsed)));
			Assert.Contains(ContainerKind.GuildBank, new[] { parsed.From.Container, parsed.To.Container });
		}

		// The container header retail pushes for an EMPTY vault, byte-exact - and the thing that makes it a guild
		// container rather than a personal one: its instance is the GUILD id.
		[Fact]
		public void TheVaultPush_IsByteExactAgainstTheCapture()
		{
			const string captured = "0000000c0a06086d10c7d8211064180000000000";

			var parsed = GuildBankCodec.Parse(Convert.FromHexString(captured));

			Assert.Equal((uint)ContainerKind.GuildBank, parsed.Vault.InventoryId.Type);
			Assert.Equal(GuildId, parsed.Vault.InventoryId.Instance);
			Assert.Empty(parsed.Vault.Items);
			Assert.Equal(captured, Convert.ToHexStringLower(GuildBankCodec.Build(parsed.Vault)));
		}

		// A deposit TRANSFERS ownership: the guild owns it, the depositor does not. Asserted on the row rather than
		// on the reply, because this is the half the wire cannot show.
		[Fact]
		public async Task ADeposit_TransfersOwnershipToTheGuild()
		{
			var (service, client, outbound, database, options) = await Harness();

			Assert.True(await service.Move(client, Move(ContainerKind.Bag, Depositor, BagSlot, ContainerKind.GuildBank, GuildId, VaultSlot)));

			await using var ctx = new DatabaseContext(options);
			var item = await ctx.ItemInstances.AsNoTracking().SingleAsync(i => i.TemplateId == Template, TestContext.Current.CancellationToken);
			Assert.Null(item.OwnerCharacterId);
			Assert.Equal(GuildId, item.OwnerGuildId);
			Assert.Equal(ContainerKind.GuildBank, item.Container);

			// It is the SAME row - a deposit is an update, not a re-mint, which is what keeps a crafted or gemmed
			// item intact through the vault.
			Assert.Equal("Melvin", item.CreatorName);
			Assert.Single(outbound.Moves);
			Assert.Equal(GuildId, (await database.GetGuildBankItems(GuildId)).Single().OwnerGuildId);
		}

		// ...and a withdraw gives it back, to whoever takes it out - not necessarily whoever put it in.
		[Fact]
		public async Task AWithdraw_GivesOwnershipToTheTakingCharacter()
		{
			var (service, client, _, database, options) = await Harness();
			Assert.True(await service.Move(client, Move(ContainerKind.Bag, Depositor, BagSlot, ContainerKind.GuildBank, GuildId, VaultSlot)));
			Assert.True(await service.Move(client, Move(ContainerKind.GuildBank, GuildId, VaultSlot, ContainerKind.Bag, Depositor, 25)));

			await using var ctx = new DatabaseContext(options);
			var item = await ctx.ItemInstances.AsNoTracking().SingleAsync(i => i.TemplateId == Template, TestContext.Current.CancellationToken);
			Assert.Equal(Depositor, item.OwnerCharacterId);
			Assert.Null(item.OwnerGuildId);
			Assert.Equal(25, item.Slot);
			Assert.Empty(await database.GetGuildBankItems(GuildId));
		}

		// ⚠️ THE SECURITY ONE. The vault instance is client-supplied, so a member could name ANOTHER guild's vault
		// and help themselves. The service checks it against the character's own guild.
		[Fact]
		public async Task AMoveNamingAnotherGuildsVault_IsRefused()
		{
			var (service, client, outbound, _, options) = await Harness();

			Assert.False(await service.Move(client, Move(ContainerKind.Bag, Depositor, BagSlot, ContainerKind.GuildBank, OtherGuildId, VaultSlot)));

			Assert.Empty(outbound.Moves);
			await using var ctx = new DatabaseContext(options);
			var item = await ctx.ItemInstances.AsNoTracking().SingleAsync(i => i.TemplateId == Template, TestContext.Current.CancellationToken);
			Assert.Equal(Depositor, item.OwnerCharacterId);
		}

		// An occupied destination is refused rather than swapped: both captured moves land in a free cell, so what
		// retail does with an occupied one is unobserved and a wrong guess costs somebody else's deposit.
		[Fact]
		public async Task AnOccupiedVaultCell_IsRefused()
		{
			var (service, client, outbound, _, _) = await Harness(secondBagSlot: 19);

			Assert.True(await service.Move(client, Move(ContainerKind.Bag, Depositor, BagSlot, ContainerKind.GuildBank, GuildId, VaultSlot)));
			Assert.False(await service.Move(client, Move(ContainerKind.Bag, Depositor, 19, ContainerKind.GuildBank, GuildId, VaultSlot)));

			Assert.Single(outbound.Moves);
		}

		// A guildless character has no vault to move into, and gets silence rather than a crash.
		[Fact]
		public async Task AGuildlessCharacter_IsRefused()
		{
			var (service, client, outbound, _, options) = await Harness();
			await using (var ctx = new DatabaseContext(options))
			{
				var character = await ctx.Characters.SingleAsync(c => c.Id == Depositor, TestContext.Current.CancellationToken);
				character.GuildId = null;
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			client.Session.Character.GuildId = null;
			Assert.False(await service.Move(client, Move(ContainerKind.Bag, Depositor, BagSlot, ContainerKind.GuildBank, GuildId, VaultSlot)));
			Assert.Empty(outbound.Moves);
		}

		private static ProtoMoveInventoryItemIIR Move(ContainerKind from, uint fromInstance, int fromSlot, ContainerKind to, uint toInstance, int toSlot)
		{
			return new ProtoMoveInventoryItemIIR
			{
				SourceContainerId = new ProtoID32 { Type = (uint)from, Instance = fromInstance },
				SourceItemPos = fromSlot,
				DestinationContainerId = new ProtoID32 { Type = (uint)to, Instance = toInstance },
				DestinationItemPos = toSlot
			};
		}

		private async Task<(GuildBankService Service, RecordingNetworkClient Client, Recorder Outbound, DatabaseRepository Database, DbContextOptions<DatabaseContext> Options)>
			Harness(int? secondBagSlot = null)
		{
			var (options, path) = TestDb.NewSeededDatabase(Depositor);
			_databases.Add(path);

			await using (var ctx = new DatabaseContext(options))
			{
				var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
				ctx.Guilds.Add(new Guild { Id = GuildId, Name = "Faolan", RealmId = realm });
				ctx.Guilds.Add(new Guild { Id = OtherGuildId, Name = "Somebody Else", RealmId = realm });
				ctx.Items.Add(new Item { Id = Template, Name = "Fine Steelsilk Armbands", Key = new ProtoDecryptionKey() });

				var character = await ctx.Characters.SingleAsync(c => c.Id == Depositor, TestContext.Current.CancellationToken);
				character.GuildId = GuildId;

				ctx.ItemInstances.Add(new ItemInstance
				{
					OwnerCharacterId = Depositor, Container = ContainerKind.Bag, Slot = BagSlot, TemplateId = Template, Count = 1, CreatorName = "Melvin"
				});
				if (secondBagSlot is { } second)
					ctx.ItemInstances.Add(new ItemInstance { OwnerCharacterId = Depositor, Container = ContainerKind.Bag, Slot = second, TemplateId = Template, Count = 1 });

				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new TestDb.ContextFactory(options));
			var client = new RecordingNetworkClient(Depositor);
			client.Session.Character = (await database.GetCharacter(Depositor))!;
			client.Session.Character.Name = "DemoL80";

			var outbound = new Recorder();
			return (new GuildBankService(database, outbound, NullLogger.Instance), client, outbound, database, options);
		}

		private sealed class Recorder : NoOpEconomyOutbound
		{
			public List<GuildBankMove> Moves { get; } = [];
			public List<ProtoInventoryGame> Vaults { get; } = [];

			public override Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
			{
				Vaults.Add(vault);
				return Task.CompletedTask;
			}

			public override Task SendGuildBankMove(INetworkClient client, GuildBankMove move)
			{
				Moves.Add(move);
				return Task.CompletedTask;
			}
		}
	}
}
