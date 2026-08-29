using System.Collections.Generic;
using Faolan.Core.Network.Codecs.FullCharSnapshot;

namespace Faolan.Core.Network.Constants
{
	// AUTO-GENERATED (#258, re-carved to the client's PlayfieldAccessHolder grammar in #464) from
	// BeachTailsTemplate.bin via the access-window typed model - the game-static PFAccessWindow_ playfield
	// catalog the FullChar snapshot tails carry, identical for every character. Replaces the embedded
	// AccessWindowTable.bin blob; SnapshotTails emits these byte-for-byte (AccessWindowCatalogTests). To
	// regenerate if the content revision ever changes: parse BeachTailsTemplate.bin ->
	// FullCharSnapshotSubClass.AccessWindowEntries and re-emit.
	public static class AccessWindowCatalog
	{
		public static readonly IReadOnlyList<PfAccessWindowEntry> Entries =
		[
			new PfAccessWindowEntry
			{
				PlayfieldId = 1000,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Hubs",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031697 },
					new PfAccessWindowChild { Flag = 1, Id = 5059159, DisplayName = "PFAccessWindow_ConarchVillage", ParentName = "PFAccessWindow_Hubs" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1010,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031813 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1020,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031814 },
					new PfAccessWindowChild { Flag = 1, Id = 5059160, DisplayName = "PFAccessWindow_FireBeaconLookout", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1022,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1024,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1026,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059189, DisplayName = "PFAccessWindow_CradleOfDecay", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1050,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031815 },
					new PfAccessWindowChild { Flag = 1, Id = 5059162, DisplayName = "PFAccessWindow_HuntingLodge", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1052,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1070,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059161, DisplayName = "PFAccessWindow_HouseOfCrom", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1071,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1080,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031817 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1082,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059191, DisplayName = "PFAccessWindow_HallsOfEternalFrost", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1084,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059204, DisplayName = "PFAccessWindow_VistrixLair", ParentName = "PFAccessWindow_Raid" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1085,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1090,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031816 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1092,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059200, DisplayName = "PFAccessWindow_AmphitheaterOfKarutonia", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 1215,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2000,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Hubs",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031706 },
					new PfAccessWindowChild { Flag = 1, Id = 5059170, DisplayName = "PFAccessWindow_OldTarantia", ParentName = "PFAccessWindow_Hubs" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2010,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031820 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2020,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031822 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2024,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059190, DisplayName = "PFAccessWindow_CrowsNest", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2025,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059185, DisplayName = "PFAccessWindow_AttilusMansion", ParentName = "PFAccessWindow_SoloDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2026,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2029,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2030,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2032,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2033,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2080,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031819 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2085,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059197, DisplayName = "PFAccessWindow_SanctumOfBurningSouls", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2090,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031821 },
					new PfAccessWindowChild { Flag = 1, Id = 5059174, DisplayName = "PFAccessWindow_PrisonColony", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2094,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_SoloDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059199, DisplayName = "PFAccessWindow_SlaughterhoseCellar", ParentName = "PFAccessWindow_SoloDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2095,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059205, DisplayName = "PFAccessWindow_Xibaluku", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 2205,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3000,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Hubs",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031707 },
					new PfAccessWindowChild { Flag = 1, Id = 5059166, DisplayName = "PFAccessWindow_Khemi", ParentName = "PFAccessWindow_Hubs" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3001,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3020,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031823 },
					new PfAccessWindowChild { Flag = 1, Id = 5059186, DisplayName = "PFAccessWindow_BlackCastle", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3024,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059192, DisplayName = "PFAccessWindow_PyramidOfAncients", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3030,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031824 },
					new PfAccessWindowChild { Flag = 1, Id = 5059167, DisplayName = "PFAccessWindow_Kheshatta", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3032,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059203, DisplayName = "PFAccessWindow_ThothAmonStronghold", ParentName = "PFAccessWindow_Raid" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3033,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059188, DisplayName = "PFAccessWindow_CaravanRaiderHideout", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3034,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059187, DisplayName = "PFAccessWindow_BlackRingCitadel", ParentName = "PFAccessWindow_Raid" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3035,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059198, DisplayName = "PFAccessWindow_ScorpionCave", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3042,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3044,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3045,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3071,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031818 },
					new PfAccessWindowChild { Flag = 1, Id = 5063659, DisplayName = "PFAccessWindow_DragonsSpine", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3077,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 3204,
				RequiresToken = 0,
				AccessType = 2,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 4020,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Hubs",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031768 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6001,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031811 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6004,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_SoloDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059202, DisplayName = "PFAccessWindow_ForgottenCity", ParentName = "PFAccessWindow_SoloDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6005,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_SoloDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059201, DisplayName = "PFAccessWindow_TheBreach", ParentName = "PFAccessWindow_SoloDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6007,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6008,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6100,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 4815624 },
					new PfAccessWindowChild { Flag = 1, Id = 5059169, DisplayName = "PFAccessWindow_NorthernGrasslands", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6110,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059164, DisplayName = "PFAccessWindow_KangPagoda", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6120,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059181, DisplayName = "PFAccessWindow_WarmonkMonastery", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6121,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6122,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6130,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059173, DisplayName = "PFAccessWindow_PillarsOfHeaven", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6200,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 4815635 },
					new PfAccessWindowChild { Flag = 1, Id = 5059165, DisplayName = "PFAccessWindow_KaraKorum", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6210,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059177, DisplayName = "PFAccessWindow_CavernOfMalice", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6211,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059178, DisplayName = "PFAccessWindow_CelestialNecropolis", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6212,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059179, DisplayName = "PFAccessWindow_EnigmataOfYag", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6213,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059175, DisplayName = "PFAccessWindow_RefugeOfApostate", ParentName = "PFAccessWindow_SoloDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6300,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 4815642 },
					new PfAccessWindowChild { Flag = 1, Id = 5059158, DisplayName = "PFAccessWindow_ChosainProvince", ParentName = "PFAccessWindow_Outdoor" },
					new PfAccessWindowChild { Flag = 1, Id = 5059176, DisplayName = "PFAccessWindow_Shaulun", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6310,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059168, DisplayName = "PFAccessWindow_MinesOfChosain", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6311,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6312,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6320,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059172, DisplayName = "PFAccessWindow_PalaceOfYunRau", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6400,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 4815636 },
					new PfAccessWindowChild { Flag = 1, Id = 5059171, DisplayName = "PFAccessWindow_Paikang", ParentName = "PFAccessWindow_Outdoor" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6420,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Raid",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059163, DisplayName = "PFAccessWindow_JadeCitadel", ParentName = "PFAccessWindow_Raid" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6421,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6427,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6432,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059157, DisplayName = "PFAccessWindow_AiDistrict", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 6433,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon",
				Children =
				[
					new PfAccessWindowChild { Flag = 1, Id = 5059180, DisplayName = "PFAccessWindow_TiananDistrict", ParentName = "PFAccessWindow_GroupDungeon" },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7000,
				RequiresToken = 1,
				AccessType = 3,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031812 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7011,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7013,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7020,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_SoloDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7030,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_GroupDungeon"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 7040,
				RequiresToken = 0,
				AccessType = 3,
				InternalName = "PFAccessWindow_Raid"
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 10800,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_Outdoor",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031810 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 11040,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_BorderKingdoms",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031735 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 12070,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_BorderKingdoms",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031764 },
				]
			},
			new PfAccessWindowEntry
			{
				PlayfieldId = 13050,
				RequiresToken = 1,
				AccessType = 2,
				InternalName = "PFAccessWindow_BorderKingdoms",
				Children =
				[
					new PfAccessWindowChild { Flag = 0, Id = 5031765 },
				]
			},
		];
	}
}
