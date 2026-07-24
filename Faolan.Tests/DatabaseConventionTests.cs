using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Enforces the EF model conventions that keep the schema readable from the ENTITY, not from a distant
	// configuration block.
	//
	// A relationship's foreign key is declared with `[ForeignKey]` on the navigation property of the entity that
	// owns it - never with a fluent `.HasForeignKey(...)` in DatabaseContext.OnModelCreating. Reading
	// `Faolan.Core.Database/Tables/Foo.cs` should tell you what Foo points at; a fluent FK moves half of that into a
	// 400-line method nobody reads next to the entity. COMPOSITE keys are no exception and are not a reason to
	// reach for the fluent API: `[ForeignKey]` takes a comma-separated column list, and an interpolated `nameof`
	// chain keeps it rename-safe -
	//
	//     [ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)}")]
	//     public virtual ConversationPacket Packet { get; set; } = null!;
	//
	// What legitimately STAYS fluent is everything that is not the FK column mapping: `.OnDelete(...)` (no
	// attribute exists), `.ValueGeneratedNever()`, and the `HasMany/WithOne` pairing that names both ends.
	public class DatabaseConventionTests
	{
		// Files that may declare a fluent foreign key, with the reason. NOT a place to silence the test - if you are
		// adding an entry, first check whether [ForeignKey] with a comma-separated column list does the job.
		// Currently EMPTY: every relationship in the model is attribute-declared.
		private static readonly Dictionary<string, string> FluentForeignKeyAllowlist = new(StringComparer.Ordinal);
		private static readonly Regex FluentForeignKey = new(@"\.Has(Foreign|Principal)Key\s*[(<]");

		[Fact]
		public void ForeignKeys_AreDeclaredByAttribute_NotFluently()
		{
			var databaseProject = TestPaths.Repo("Faolan.Core.Database");
			var offenders = new List<string>();

			foreach (var file in Directory.EnumerateFiles(databaseProject, "*.cs", SearchOption.AllDirectories))
			{
				var name = Path.GetFileName(file);
				if (FluentForeignKeyAllowlist.ContainsKey(name))
					continue;

				var text = File.ReadAllText(file);
				foreach (Match m in FluentForeignKey.Matches(text))
				{
					var line = text.Take(m.Index).Count(c => c == '\n') + 1;
					offenders.Add($"{name}:{line}");
				}
			}

			Assert.True(offenders.Count == 0,
				$"Foreign keys must be declared with [ForeignKey] on the navigation property, not fluently. " +
				$"A composite key takes a comma-separated column list - see ConversationScreen. Offenders: {string.Join(", ", offenders)}");
		}
	}
}
