using System;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Importer.Cli;
using Xunit;

namespace Faolan.Tests
{
	// The `--report` index is a table (#928 item 4); these are the properties the old switch enforced by construction
	// and the table has to keep by test: one entry per name, kebab-case names, and no entry shadowing the built-in
	// `list`.
	public class ReportCatalogTests
	{
		[Fact]
		public void EveryReportName_IsUniqueKebabCase_AndNoneIsTheBuiltInList()
		{
			var names = ReportCli.Reports.Select(r => r.Name).ToList();

			Assert.NotEmpty(names);
			Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
			Assert.All(names, n => Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), n));
			Assert.DoesNotContain("list", names);
			Assert.All(ReportCli.Reports, r => Assert.NotNull(r.Run));
		}
	}
}
