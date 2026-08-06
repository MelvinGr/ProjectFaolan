using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The combo RUNTIME (#139): the session-side tracking that turns the combo fields on each swing into an attempt
	// with a finisher. The wire claims those fields rest on are locked separately, in ComboWireTests.
	//
	// The combos used here are real shipped rows, addressed by id, because the whole point is that the ids on the
	// wire index this catalog: 161078 `Assassin_20b_poisondebuff_Grim_Corruption_II` [UP,UL] is a two-step combo and
	// 3095883 `Rogue_19_damagedetaunt_Cunning_Strike_VI_dagger` [UL,UR,UP] a three-step one, both taken from the
	// assassin combo capture.
	public class ComboServiceTests
	{
		private const uint TwoStep = 161078;
		private const uint ThreeStep = 3095883;

		[Fact]
		public async Task ASwingWithNoComboId_IsNotACombo()
		{
			var (service, client) = Setup();

			Assert.Null(await service.TrackSwing(client, new CommandMessage { CommandqueueClientId = 1 }));
		}

		[Fact]
		public async Task TheLastStepOfTheSequence_IsTheFinisher()
		{
			var (service, client) = Setup();

			var opener = await service.TrackSwing(client, Swing(TwoStep));
			Assert.NotNull(opener);
			Assert.Equal(2, opener.StepCount);
			Assert.True(opener.IsOpener);
			Assert.False(opener.IsFinisher);

			var finisher = await service.TrackSwing(client, Swing(TwoStep, 1));
			Assert.NotNull(finisher);
			Assert.True(finisher.IsFinisher);

			// The finisher's payload is the combo's own spell - 149094 "Grim Corruption II".
			Assert.Equal(149094u, finisher.SpellId);
		}

		[Fact]
		public async Task AThreeStepCombo_OnlyFinishesOnItsThirdSwing()
		{
			var (service, client) = Setup();

			Assert.False((await service.TrackSwing(client, Swing(ThreeStep)))!.IsFinisher);
			Assert.False((await service.TrackSwing(client, Swing(ThreeStep, 1)))!.IsFinisher);
			Assert.True((await service.TrackSwing(client, Swing(ThreeStep, 2)))!.IsFinisher);
		}

		// Retail re-sends step 0 when the player restarts a sequence (the assassin capture does it inside one
		// Cunning Strike), so a repeated opener must reset the attempt rather than be read as a continuation.
		[Fact]
		public async Task ARepeatedOpener_RestartsTheAttempt()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(ThreeStep, 0, activation: 60));
			await service.TrackSwing(client, Swing(ThreeStep, 0, activation: 60));
			var second = await service.TrackSwing(client, Swing(ThreeStep, 1, activation: 60));

			Assert.NotNull(second);
			Assert.Equal(120u, second.TotalCentiseconds); // the first attempt's 60 was dropped, not carried
		}

		// A plain swing between two combo swings breaks the chain: the next combo swing has to open a new attempt,
		// not resume at a stale step.
		[Fact]
		public async Task APlainSwingBetweenSteps_EndsTheAttempt()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(ThreeStep, 0, activation: 60));
			Assert.Null(await service.TrackSwing(client, new CommandMessage { CommandqueueClientId = 9 }));

			var resumed = await service.TrackSwing(client, Swing(ThreeStep, 1, activation: 60));
			Assert.NotNull(resumed);
			Assert.Equal(60u, resumed.TotalCentiseconds);
		}

		// 0xFFFFFFFF is the unstepped sentinel the corpus carries on the NPC and blank-Actions combos. Read as a
		// signed step it would be -1 and index off the front of the sequence.
		[Fact]
		public async Task TheUnsteppedSentinel_IsASingleStepCombo()
		{
			var (service, client) = Setup();

			// 3363574 `combo_npc_generic_1he` - an `action`-marker row, which is where the sentinel appears.
			var swing = await service.TrackSwing(client, Swing(3363574, ComboService.UnsteppedAttackNum));

			Assert.NotNull(swing);
			Assert.Equal(0, swing.Step);
			Assert.Equal(1, swing.StepCount);
			Assert.True(swing.IsFinisher);
		}

		// #673: a combo step IS a directional swing - the player reached it by pressing that key - and the corpus
		// agrees: word 5 (the direction ABILITY) is absent on 107 of the 515 captured player swings, yet every one of
		// those result records still carries a direction code. So the direction has to come from the catalog, which
		// is the only place a combo step states it. `Assassin_20b_..._Grim_Corruption_II` is `UP,UL` and
		// `Rogue_19_..._Cunning_Strike_VI_dagger` is `UL,UR,UP`, both read straight off `Combo.Actions`.
		[Fact]
		public async Task EachComboStep_CarriesTheZoneItsKeyNames()
		{
			var (service, client) = Setup();

			Assert.Equal(SwingDirection.Overhead, (await service.TrackSwing(client, Swing(TwoStep)))!.Direction);
			Assert.Equal(SwingDirection.UpperLeft, (await service.TrackSwing(client, Swing(TwoStep, 1)))!.Direction);

			Assert.Equal(SwingDirection.UpperLeft, (await service.TrackSwing(client, Swing(ThreeStep)))!.Direction);
			Assert.Equal(SwingDirection.UpperRight, (await service.TrackSwing(client, Swing(ThreeStep, 1)))!.Direction);
			Assert.Equal(SwingDirection.Overhead, (await service.TrackSwing(client, Swing(ThreeStep, 2)))!.Direction);
		}

		// A combo with no directional grammar states no zone rather than a made-up one - the `action` NPC rows, the
		// blank specials and the reactive markers are not key sequences at all, so the caller falls back explicitly
		// instead of being handed a fabricated direction.
		[Fact]
		public async Task AComboWithNoKeySequence_StatesNoZone()
		{
			var (service, client) = Setup();

			Assert.Null((await service.TrackSwing(client, Swing(3363574, ComboService.UnsteppedAttackNum)))!.Direction);
		}

		// A combo id outside the catalog resolves as a plain swing rather than throwing - the client is free to
		// name content we have not imported, and a hit is the safe reading of that.
		[Fact]
		public async Task AnUnknownComboId_ResolvesAsAPlainSwing()
		{
			var (service, client) = Setup();

			Assert.Null(await service.TrackSwing(client, Swing(0xDEADBEEF)));
		}

		// #623: the reuse gate. `Cunning Strike VI` carries a 14 s `Combo.Cooldown`, so once its finisher lands the
		// next OPENER of the same combo resolves as a plain white swing instead of starting a fresh attempt.
		[Fact]
		public async Task ACompletedCombo_RefusesItsNextOpenerForTheCatalogCooldown()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(ThreeStep));
			await service.TrackSwing(client, Swing(ThreeStep, 1));
			Assert.True((await service.TrackSwing(client, Swing(ThreeStep, 2)))!.IsFinisher);

			Assert.Null(await service.TrackSwing(client, Swing(ThreeStep)));

			// The refusal leaves NO half-open attempt behind: a step-1 swing after it is a fresh opener's worth of
			// state, not step 2 of a combo that never started.
			Assert.Null(client.Session.ActiveComboId);
			Assert.Equal(0, client.Session.ActiveComboStep);
		}

		// The gate is per combo id, so a different combo is unaffected by the one on cooldown.
		[Fact]
		public async Task AnotherCombo_IsNotHeldByTheFirstOnesCooldown()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(ThreeStep));
			await service.TrackSwing(client, Swing(ThreeStep, 1));
			await service.TrackSwing(client, Swing(ThreeStep, 2));

			Assert.NotNull(await service.TrackSwing(client, Swing(TwoStep)));
		}

		// A combo whose catalog cooldown is 0 - 452 of the 744 shipped rows, including `Grim Corruption II` - arms
		// nothing, so it can be chained back to back exactly as before.
		[Fact]
		public async Task AZeroCooldownCombo_ArmsNothing()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(TwoStep));
			Assert.True((await service.TrackSwing(client, Swing(TwoStep, 1)))!.IsFinisher);

			Assert.Empty(client.Session.ComboReadyAt);
			Assert.NotNull(await service.TrackSwing(client, Swing(TwoStep)));
		}

		// Only the OPENER is gated: a sequence already under way runs to its finisher. Arranged by arming the timer
		// directly, because the only way to get mid-attempt with the combo on cooldown is a client that ignored it.
		[Fact]
		public async Task AnAttemptAlreadyUnderWay_IsNotCutOffByTheCooldown()
		{
			var (service, client) = Setup();

			Assert.NotNull(await service.TrackSwing(client, Swing(ThreeStep)));
			client.Session.ComboReadyAt[ThreeStep] = Environment.TickCount64 + 60_000;

			Assert.NotNull(await service.TrackSwing(client, Swing(ThreeStep, 1)));
			Assert.True((await service.TrackSwing(client, Swing(ThreeStep, 2)))!.IsFinisher);
		}

		// The client's own stamina formula: MaxStamina x (Combo.Stamina / 100) x animationSeconds / StaminaDivisor.
		// Grim Corruption II costs 55, and the Assassin's level-1 divisor is 32.
		[Fact]
		public async Task StaminaCost_FollowsTheClientFormula()
		{
			var (service, client) = Setup();

			await service.TrackSwing(client, Swing(TwoStep, 0, activation: 60));
			var finisher = await service.TrackSwing(client, Swing(TwoStep, 1, activation: 115));
			Assert.NotNull(finisher);
			Assert.Equal(1.75f, finisher.AnimationSeconds, 3);

			// 1000 * 0.55 * 1.75 / 32 = 30.07
			Assert.Equal(30u, await service.StaminaCost(ConanClass.Assassin, 1, 1000, finisher));

			// No stamina pool -> no cost, rather than a divide into a zero bar.
			Assert.Equal(0u, await service.StaminaCost(ConanClass.Assassin, 1, 0, finisher));
		}

		private static CommandMessage Swing(uint comboId, uint step = 0, uint activation = 60)
		{
			var message = new CommandMessage
			{
				ComboId = comboId,
				ActivationTime = activation
			};

			// Step 0 is how the CLIENT sends an opener: protobuf omits the zero, so the field is genuinely absent.
			if (step != 0)
				message.ComboAttackNum = step;

			return message;
		}

		private static (ComboService Service, RecordingNetworkClient Client) Setup()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			return (new ComboService(repo, NullLogger.Instance), new RecordingNetworkClient(0x01316D8A));
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
