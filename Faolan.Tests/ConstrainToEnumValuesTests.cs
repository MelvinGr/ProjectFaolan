using System;
using System.IO;
using System.Linq;
using Faolan.Core.Database.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Faolan.Tests
{
	// [ConstrainToEnumValues] emits "<Column> IN (<the enum's declared members>)". That is exactly wrong for a [Flags]
	// enum - a legitimate combination is not a declared member, so the CHECK would reject it - and nothing about it
	// fails at build or migration time: it would surface as a constraint violation from the database on the first
	// SaveChanges that stored a combination. So the attribute skips flags enums, loudly.
	public class ConstrainToEnumValuesTests
	{
		[Flags]
		private enum Privileges
		{
			None = 0,
			Read = 1,
			Write = 2,
			Admin = 4
		}

		private enum State
		{
			Pending = 0,
			Active = 1,
			Closed = 2
		}

		private sealed class Row
		{
			public int Id { get; set; }

			[ConstrainToEnumValues]
			public Privileges Privileges { get; set; }

			[ConstrainToEnumValues]
			public State State { get; set; }
		}

		// EF caches the built model per context type + provider, so without this only the FIRST test in the class would
		// run OnModelCreating - and the warning assertions would pass or fail purely on test order.
		private sealed class AlwaysRebuild : IModelCacheKeyFactory
		{
			public object Create(DbContext context, bool designTime) => Guid.NewGuid();
		}

		private sealed class Ctx(DbContextOptions<Ctx> options) : DbContext(options)
		{
			public DbSet<Row> Rows => Set<Row>();

			protected override void OnModelCreating(ModelBuilder modelBuilder)
			{
				modelBuilder.Entity<Row>().ToTable("Rows");
				ConstrainToEnumValuesAttribute.ApplyEnumCheckConstraints(modelBuilder);
			}
		}

		private static (string[] Constraints, string Warnings) Build()
		{
			var stderr = new StringWriter();
			var previous = Console.Error;
			Console.SetError(stderr);
			try
			{
				using var ctx = new Ctx(new DbContextOptionsBuilder<Ctx>().UseSqlite("Data Source=:memory:")
					.ReplaceService<IModelCacheKeyFactory, AlwaysRebuild>().Options);
				// CHECK constraints live on the DESIGN-TIME model - the runtime model is read-optimized and drops them.
				var model = ctx.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
				var names = model.GetEntityTypes().SelectMany(e => e.GetCheckConstraints()).Select(c => c.Name).ToArray();
				return (names, stderr.ToString());
			}
			finally
			{
				Console.SetError(previous);
			}
		}

		// A plain enum still gets its constraint - the skip must not be a blanket opt-out.
		[Fact]
		public void APlainEnum_IsStillConstrained()
		{
			Assert.Contains("CK_Row_State", Build().Constraints);
		}

		[Fact]
		public void AFlagsEnum_IsNotConstrained()
		{
			Assert.DoesNotContain("CK_Row_Privileges", Build().Constraints);
		}

		// Skipping SILENTLY would be the worst outcome: the author keeps the attribute, believes the column is
		// constrained, and nothing ever says otherwise. The warning has to name the property and the reason.
		[Fact]
		public void SkippingAFlagsEnum_WarnsNamingTheProperty()
		{
			var warnings = Build().Warnings;

			Assert.Contains("Row.Privileges", warnings);
			Assert.Contains("Privileges", warnings);
			Assert.Contains("[Flags]", warnings);
			Assert.DoesNotContain("Row.State", warnings); // and says nothing about the one it did constrain
		}
	}
}
