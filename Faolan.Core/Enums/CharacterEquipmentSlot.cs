namespace Faolan.Core.Enums
{
    public enum CharacterEquipmentSlot : uint
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
}