namespace Faolan.Core.Network.Codecs.Spawn
{
	// One 8-byte pair [u32 id][s32 value] in the three lists that CLOSE an FA015807 post-IIR stream (ids are decades
	// 10..110). Shared by both families - the closing lists are part of SpawnPostIirCommon, not of either codec.
	//
	// The first list IS the FACTION-REPUTATION block: Id = faction id, Value = signed standing (+/-50000) shown in the
	// client's Factions window. See CharacterScaffoldPair / Gitea #315.
	public readonly record struct ScaffoldPair(uint Id, int Value);
}
