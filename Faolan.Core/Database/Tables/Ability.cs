namespace Faolan.Core.Database.Tables
{
	// An ability/skill template, imported once from the old C++ server's `p_abilities` (Archive/). Same shape as
	// the item identity: Data0..4 is the ability's stable identity quad (used by the cast/grant packets). Names are
	// client-side (resolved by id); the archive provides the few it has.
	public class Ability : BaseEntity
	{
		public byte Level { get; set; }
		public string Name { get; set; }
		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }
	}
}
