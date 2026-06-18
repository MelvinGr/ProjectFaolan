using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;

namespace Faolan.GameServer.Economy
{
	// The canonical "Bag of loot" definition, baked from the captured bag's typed columns + its static geometry tail
	// (LootBagKdTreeTail.bin). The loot system builds every dropped/spawned bag from THIS via
	// WorldObjectBuilder.BuildFromScratch (#249) - so it no longer depends on a captured loot-bag WorldObject row
	// existing on the map (GetLootTemplate is now map-independent and survives deleting static world bags). The columns
	// reproduce the captured identity message byte-exact (LootBagTemplateTests); the KdTreeTail is the one piece of
	// reference geometry, embedded once. The LootBagFullUpdateIIR class + the loot typetag are the bag's wire identity.
	public static class LootBagTemplate
	{
		public const uint SubOpcode = 0xF096251E; // LootBagFullUpdateIIR
		public const uint TypeTag = 0x0000C87A;   // the loot typetag (WorldLoot.LootTypeTag)

		private static readonly byte[] KdTreeTail = LoadAsset("LootBagKdTreeTail.bin");

		// True once the geometry asset is embedded (always, in a built server) - guards the loot path.
		public static bool IsAvailable => KdTreeTail.Length > 0;

		// A fresh WorldObject carrying the generic loot bag's identity columns + geometry. Returned by GetLootTemplate
		// and fed to WorldObjectBuilder.BuildFromScratch with the live handle/position/token. Data stays null - this is
		// built from scratch, never from a captured packet.
		public static WorldObject Create()
		{
			return new WorldObject
			{
				Name = "Bag of loot",
				SubOpcode = SubOpcode,
				DisplayName = "<remoteformat id=\"13782\" category=\"50200\" key=\"!,1%VZc#)*ctk-zNFNxI\" knubot=\"0\"  ></remoteformat>",
				TemplateItemId = 13782,
				CarsGroup = 1,
				IsClosed = true,
				Kdtree = 0,
				KdTreeTail = KdTreeTail
			};
		}

		private static byte[] LoadAsset(string fileName)
		{
			var assembly = Assembly.GetExecutingAssembly();
			var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
			if (name == null)
				return [];

			using var stream = assembly.GetManifestResourceStream(name);
			if (stream == null)
				return [];

			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}
	}
}
