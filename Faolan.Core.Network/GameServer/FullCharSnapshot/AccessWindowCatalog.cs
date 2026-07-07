using System.Collections.Generic;

namespace Faolan.Core.Network.GameServer.FullCharSnapshot
{
    // AUTO-GENERATED (#258) from BeachTailsTemplate.bin via the access-window typed model - the game-static
    // PFAccessWindow_ playfield catalog the FullChar snapshot tails carry, identical for every character.
    // Replaces the embedded AccessWindowTable.bin blob; FullCharSnapshotSubClass.SerializeAccessWindowEntries emits these
    // byte-for-byte (guarded by BeachTailsFromScratchTests + AccessWindowCatalogTests). To regenerate if the
    // content revision ever changes: parse BeachTailsTemplate.bin -> FullCharSnapshotSubClass.AccessWindowEntries and re-emit.
    // Strings are Latin1 (one char per wire byte); non-ASCII bytes are \uXXXX-escaped (round-trips via Encoding.Latin1).
    public static class AccessWindowCatalog
    {
        public static readonly IReadOnlyList<PfAccessWindowEntry> Entries =
        [
            new PfAccessWindowEntry
            {
                PlayfieldId = 1000,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Hubs",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031697,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2W",
                            DisplayName = "PFAccessWindow_ConarchVillage",
                            ParentName = "PFAccessWindow_Hubs"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1010,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031813,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1020,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031814,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2X",
                            DisplayName = "PFAccessWindow_FireBeaconLookout",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1022,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1024,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1026,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2u",
                            DisplayName = "PFAccessWindow_CradleOfDecay",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1050,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031815,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2Z",
                            DisplayName = "PFAccessWindow_HuntingLodge",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1052,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1070,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2Y",
                            DisplayName = "PFAccessWindow_HouseOfCrom",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1071,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1080,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031817,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1082,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2w",
                            DisplayName = "PFAccessWindow_HallsOfEternalFrost",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1084,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0084",
                            DisplayName = "PFAccessWindow_VistrixLair",
                            ParentName = "PFAccessWindow_Raid"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1085,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1090,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031816,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1092,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0080",
                            DisplayName = "PFAccessWindow_AmphitheaterOfKarutonia",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 1215,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2000,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Hubs",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031706,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2b",
                            DisplayName = "PFAccessWindow_OldTarantia",
                            ParentName = "PFAccessWindow_Hubs"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2010,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031820,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2020,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031822,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2024,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2v",
                            DisplayName = "PFAccessWindow_CrowsNest",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2025,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2q",
                            DisplayName = "PFAccessWindow_AttilusMansion",
                            ParentName = "PFAccessWindow_SoloDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2026,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2029,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2030,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2032,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2033,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2080,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031819,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2085,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2}",
                            DisplayName = "PFAccessWindow_SanctumOfBurningSouls",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2090,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031821,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2f",
                            DisplayName = "PFAccessWindow_PrisonColony",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2094,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u007F",
                            DisplayName = "PFAccessWindow_SlaughterhoseCellar",
                            ParentName = "PFAccessWindow_SoloDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2095,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0085",
                            DisplayName = "PFAccessWindow_Xibaluku",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 2205,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3000,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Hubs",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031707,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2^",
                            DisplayName = "PFAccessWindow_Khemi",
                            ParentName = "PFAccessWindow_Hubs"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3001,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3020,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031823,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2r",
                            DisplayName = "PFAccessWindow_BlackCastle",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3024,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2x",
                            DisplayName = "PFAccessWindow_PyramidOfAncients",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3030,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031824,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2_",
                            DisplayName = "PFAccessWindow_Kheshatta",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3032,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0083",
                            DisplayName = "PFAccessWindow_ThothAmonStronghold",
                            ParentName = "PFAccessWindow_Raid"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3033,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2t",
                            DisplayName = "PFAccessWindow_CaravanRaiderHideout",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3034,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2s",
                            DisplayName = "PFAccessWindow_BlackRingCitadel",
                            ParentName = "PFAccessWindow_Raid"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3035,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2~",
                            DisplayName = "PFAccessWindow_ScorpionCave",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3042,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3044,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3045,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3071,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031818,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "MC\u00EB",
                            DisplayName = "PFAccessWindow_DragonsSpine",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3077,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 3204,
                SectionOrTier = 0,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 4020,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Hubs",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031768,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6001,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031811,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6004,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0082",
                            DisplayName = "PFAccessWindow_ForgottenCity",
                            ParentName = "PFAccessWindow_SoloDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6005,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\u0081",
                            DisplayName = "PFAccessWindow_TheBreach",
                            ParentName = "PFAccessWindow_SoloDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6007,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6008,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6100,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 4815624,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2a",
                            DisplayName = "PFAccessWindow_NorthernGrasslands",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6110,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2\\",
                            DisplayName = "PFAccessWindow_KangPagoda",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6120,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2m",
                            DisplayName = "PFAccessWindow_WarmonkMonastery",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6121,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6122,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6130,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2e",
                            DisplayName = "PFAccessWindow_PillarsOfHeaven",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6200,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 4815635,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2]",
                            DisplayName = "PFAccessWindow_KaraKorum",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6210,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2i",
                            DisplayName = "PFAccessWindow_CavernOfMalice",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6211,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2j",
                            DisplayName = "PFAccessWindow_CelestialNecropolis",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6212,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2k",
                            DisplayName = "PFAccessWindow_EnigmataOfYag",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6213,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2g",
                            DisplayName = "PFAccessWindow_RefugeOfApostate",
                            ParentName = "PFAccessWindow_SoloDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6300,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 3,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 4815642,
                    Unknown = 0,
                    DetailsMode = 3,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2V",
                            DisplayName = "PFAccessWindow_ChosainProvince",
                            ParentName = "PFAccessWindow_Outdoor"
                        },
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2h",
                            DisplayName = "PFAccessWindow_Shaulun",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6310,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2`",
                            DisplayName = "PFAccessWindow_MinesOfChosain",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6311,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6312,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6320,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2d",
                            DisplayName = "PFAccessWindow_PalaceOfYunRau",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6400,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 2,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 4815636,
                    Unknown = 0,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2c",
                            DisplayName = "PFAccessWindow_Paikang",
                            ParentName = "PFAccessWindow_Outdoor"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6420,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2[",
                            DisplayName = "PFAccessWindow_JadeCitadel",
                            ParentName = "PFAccessWindow_Raid"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6421,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6427,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6432,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2U",
                            DisplayName = "PFAccessWindow_AiDistrict",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 6433,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 1,
                    DetailsMode = 1,
                    Details =
                    [
                        new PfAccessWindowChildDetail
                        {
                            DetailsMode = 1,
                            DetailUnknown = 0,
                            ShortCode = "M2l",
                            DisplayName = "PFAccessWindow_TiananDistrict",
                            ParentName = "PFAccessWindow_GroupDungeon"
                        }
                    ]
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7000,
                SectionOrTier = 1,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031812,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7011,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7013,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7020,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_SoloDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7030,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_GroupDungeon",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 7040,
                SectionOrTier = 0,
                Kind = 3,
                Unknown = 0,
                InternalName = "PFAccessWindow_Raid",
                UnknownBeforeChildMode = 0,
                ChildMode = 0
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 10800,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_Outdoor",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031810,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 11040,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_BorderKingdoms",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031735,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 12070,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_BorderKingdoms",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031764,
                    Unknown = 0
                }
            },
            new PfAccessWindowEntry
            {
                PlayfieldId = 13050,
                SectionOrTier = 1,
                Kind = 2,
                Unknown = 0,
                InternalName = "PFAccessWindow_BorderKingdoms",
                UnknownBeforeChildMode = 0,
                ChildMode = 1,
                Child = new PfAccessWindowChild
                {
                    Variant = 0,
                    Id = 5031765,
                    Unknown = 0
                }
            }
        ];
    }
}
