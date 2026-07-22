using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// One entry of the client's character-customization registry (RDB type 1000076 id 0): every selectable
	// appearance target - the face/body morph sliders AND the choosable resources (*hairmesh_N / *beardmesh_N /
	// *piercingmesh_N / *texscript_N for hair/eye/skin color, tattoos, scars, face paint). Imported by
	// Faolan.Importer --rdb. The spawn scaffold's weighted (1.0) entry-table records reference these by
	// WireTag = registry id - 1 (verified: faolanx's 7 weighted entries resolve 7/7 against his create-editor
	// resource refs; the few retail mismatches are live-server/client data-version drift). See
	// docs/re/player-spawn-generation.md "Weighted attributes".
	public class MorphTarget : BaseEntity
	{
		// The registry's category: 0 face sliders, 1 body sliders, 2 face paint (never emitted in spawns),
		// 3 hairmesh, 4 beardmesh, 5 piercingmesh, 6 hair color, 7 eye color, 8 skin color, 9/10 tattoos+scars.
		public byte Category { get; set; }

		// The registry weight (1.0 for resources; sliders carry 0.1/0.3/0.5).
		public float Weight { get; set; }
		// Id (from BaseEntity) = the wire tag = the registry's 1-based id minus 1.

		// The registry name: a slider name ("arm", "02_BrowLow") or a resource ref ("*texscript_1108153").
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
