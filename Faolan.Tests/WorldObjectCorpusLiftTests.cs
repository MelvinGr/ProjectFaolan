using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Enums;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The whole-corpus gate on the world-object column lift: EVERY captured SimpleDynelFullUpdateIIR must lift into
	// columns and rebuild its identity message + geometry record byte-exact.
	//
	// This exists because the lift is deliberately ALL-OR-NOTHING (SimpleDynelMessageCodec.TryLiftColumns): a record
	// carrying a field no column covers is left UN-LIFTED rather than half-stored, which is the right call but is also
	// SILENT - the importer ships the row, WorldObjectBuilder then refuses to build it, and the object simply never
	// spawns. One row sat like that (the map 11040 "Siege Camp", the only captured object that sets proto field 4
	// `show_popup_on_use`) and nothing failed. A per-row assertion in the importer would only cover rows the importer
	// happens to select; asserting over the corpus covers the grammar itself.
	public class WorldObjectCorpusLiftTests
	{
		[Fact]
		public void EveryCapturedWorldObject_LiftsIntoColumns()
		{
			var unlifted = new List<string>();
			var lifted = 0;

			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR))
			{
				var row = new WorldObject();
				if (SimpleDynelMessageCodec.TryLiftColumns(row, body))
				{
					lifted++;
					continue;
				}

				unlifted.Add($"{source}: name={row.Name ?? "<null>"}");
			}

			Assert.True(unlifted.Count == 0,
				$"{unlifted.Count} of {lifted + unlifted.Count} captured world objects did not lift into columns, so they would " +
				$"ship un-spawnable:\n  {string.Join("\n  ", unlifted.Take(10))}");
		}
	}
}
