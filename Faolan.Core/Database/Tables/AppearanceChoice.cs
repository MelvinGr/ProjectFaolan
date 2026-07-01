namespace Faolan.Core.Database.Tables
{
	// One selectable character-appearance choice for a given sex+race, imported from the client's CHARACTERCREATOR
	// (RDB 1010700 - the per-sex/race legal option lists) with the resource's friendly Name resolved from the
	// referenced RDB object's own name= (texscript for tattoos, monsterdata for heads) and the swatch Color from the
	// creator XML. The morph slider bounds and the height range ride as special Category rows (Min/Max set). This is
	// what the Faolan.Web appearance editor reads for its race/sex-gated pickers (replacing a runtime XML parse).
	public class AppearanceChoice
	{
		public int Id { get; set; }

		public byte Sex { get; set; }
		public byte Race { get; set; }

		// hair | haircolor | eyecolor | skincolor | bodymarkings | facemarkings | head | morph | height
		public string Category { get; set; }

		// Editor send order within the category (drives the picker order).
		public int Sequence { get; set; }

		// The stored value: a "*hairmesh_N"/"*texscript_N" ref, a head monsterdataid, or (for a morph row) the morph
		// name. Empty for the height row.
		public string Value { get; set; }

		// Swatch hex for the colour categories (else null).
		public string Color { get; set; }

		// Friendly name from the referenced resource's name= (else null - the UI falls back to an ordinal label).
		public string Name { get; set; }

		// Bounds for the morph/height rows (in CharacterBody signed-slider units for morphs); 0 for option rows.
		public int Min { get; set; }
		public int Max { get; set; }
	}
}
