using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	// Parses an Age of Conan RDB MonsterData record (type 1010201) - the XML model definition a character/monster
	// references (the CHARACTERCREATOR's <head monsterdataid="..."/> points here). It lists the render meshes that
	// make up the model: for a player head that is the head mesh + the eye mesh + the eyelash/teeth mesh (#275).
	//
	//   <monsterdata animsys=".." skeleton=".." name="..">
	//     <mesh mesh="222148"/>   <!-- head -->
	//     <mesh mesh="222146"/>   <!-- eyes -->
	//     <mesh mesh="222147"/>   <!-- lashes/teeth -->
	//   </monsterdata>
	public sealed record MonsterDataFile(uint Id, string? Name, uint? SkeletonId, IReadOnlyList<uint> MeshIds)
	{
		public const uint Type = 1010201;

		public static MonsterDataFile Parse(uint id, byte[] decompressed)
		{
			var text = Encoding.Latin1.GetString(decompressed);
			var start = text.IndexOf('<', text.IndexOf("<monsterdata", StringComparison.Ordinal) is var i && i >= 0
				? i
				: 0);
			var end = text.LastIndexOf('>');
			if (start < 0 || end <= start)
				return new MonsterDataFile(id, null, null, []);

			XElement root;
			try
			{
				root = XElement.Parse(text.Substring(start, end - start + 1));
			}
			catch
			{
				return new MonsterDataFile(id, null, null, []);
			}

			var meshes = root
				.Elements("mesh")
				.Select(e => uint.TryParse(e.Attribute("mesh")?.Value, out var m)
					? m
					: 0u)
				.Where(m => m != 0)
				.ToList();

			return new MonsterDataFile(id, root.Attribute("name")?.Value, uint.TryParse(root.Attribute("skeleton")?.Value, out var sk)
				? sk
				: null, meshes);
		}
	}
}
