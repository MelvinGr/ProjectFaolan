using System;
using System.IO;
using Faolan.Tests.Support;
using Faolan.Tests.Support.ProtocolDocs;
using Xunit;

namespace Faolan.Tests
{
	// The anti-drift guard for docs/protocol/gen/. Each generated page is rendered from the repo's own source of
	// truth (the compiled opcode enums, the .proto, ...) and compared to what is committed. Editing Opcodes.cs then
	// forces a regenerated page in the same commit - the page can never silently disagree with the code.
	//
	// To regenerate after an intentional change: `FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`
	// then commit the updated docs/protocol/gen/*.md.
	public class ProtocolDocsTests
	{
		private static bool Updating => Environment.GetEnvironmentVariable("FAOLAN_UPDATE_DOCS") == "1";

		[Fact]
		public void CoveragePage_IsUpToDate()
		{
			AssertGenerated(CoveragePage.RelativePath, CoveragePage.Render());
		}

		[Fact]
		public void IirTypesPage_IsUpToDate()
		{
			AssertGenerated(IirTypesPage.RelativePath, IirTypesPage.Render());
		}

		[Fact]
		public void MessagesPage_IsUpToDate()
		{
			AssertGenerated(MessagesPage.RelativePath, MessagesPage.Render());
		}

		[Fact]
		public void OpcodesPage_IsUpToDate()
		{
			AssertGenerated(OpcodesPage.RelativePath, OpcodesPage.Render());
		}

		[Fact]
		public void ProtobufPage_IsUpToDate()
		{
			AssertGenerated(ProtobufPage.RelativePath, ProtobufPage.Render());
		}

		// The BKDR-31 class-id guard is the same one the Wireshark generator applies; assert it independently so a
		// renamed/typo'd RPC class fails here too (with a clear message) rather than only in the page diff.
		[Fact]
		public void RpcClassIds_AreBkdr31OfTheClassName()
		{
			Assert.Equal(0xA9ECB95Du, OpcodesPage.Bkdr31("GameCharAgent"));
			Assert.Equal(0x6DD55614u, OpcodesPage.Bkdr31("CSPlayerAgent"));
			Assert.Equal(0x8B87F791u, OpcodesPage.Bkdr31("GameCharInterface"));
		}

		private static void AssertGenerated(string relativePath, string rendered)
		{
			// Normalize trailing newline so the committed file always ends with exactly one.
			rendered = rendered.TrimEnd('\n', '\r') + "\n";
			var path = TestPaths.Repo(relativePath.Split('/'));

			if (Updating)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, rendered);
				return;
			}

			Assert.True(File.Exists(path), $"{relativePath} is missing. Generate it: FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'");

			var committed = File.ReadAllText(path).Replace("\r\n", "\n");
			if (committed == rendered)
				return;

			var diffLine = FirstDiffLine(committed, rendered);
			Assert.Fail($"{relativePath} is stale (first diff at line {diffLine}). It is GENERATED from Opcodes.cs - " +
			            "do not hand-edit. Regenerate: FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'");
		}

		private static int FirstDiffLine(string a, string b)
		{
			var la = a.Split('\n');
			var lb = b.Split('\n');
			for (var i = 0; i < Math.Min(la.Length, lb.Length); i++)
			{
				if (la[i] != lb[i])
					return i + 1;
			}

			return Math.Min(la.Length, lb.Length) + 1;
		}
	}
}
