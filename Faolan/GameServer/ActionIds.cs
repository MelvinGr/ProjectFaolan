namespace Faolan.GameServer
{
    namespace ActionIds
    {
        public enum CharacterEquipSlotsIDs : uint
        {
            Neck = 1,
            Head = 2,
            Chest = 5,
            MainWeapon = 6,
            Belt = 7,
            LeftRing = 9,
            RightRing = 10,
            Cape = 11,
            Shoulder = 12,
            Boots = 14,
            Hands = 15,
            Legs = 16,
            Wrist = 18,
            Shield = 0xff, // unk
            SecondaryWeapon = 0xff, // unk
            SecondaryShield = 0xff, // unk

            // prob. all the same + 1000 still have to check
            VanityHead = 1002,
            VanityBelt = 1007,
            VanityCape = 1011,
            VanityBoots = 1014,
            VanityWrist = 1018
        }

        public enum ActionTypes : uint
        {
            CastSpell = 0x4,
            MoveEquippedToInventory = 0x9,
            SwitchWeaponsButton = 0x2e
        }

        public enum MovingTypes : ushort
        {
            MountedRun = 0x010a,
            Walk = 0x010e,
            Jump = 0x0156,
            WalkJump = 0x0152,
            MountedJump = 0x0142,
            Falling = 0x0116,
            Ox011A = 0x011a,
            Ox011E = 0x011e,
            Ox001E = 0x001e,
            Ox001A = 0x001a
        }
    }
}