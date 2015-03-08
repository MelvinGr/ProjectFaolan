using System;
using System.Linq;
using System.Security.Permissions;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Math;

namespace LibFaolan.Data
{
    public sealed class Spell
    {
        public UInt32 Id;
        public byte Level;
        public string Name;
        public UInt16 CastTime;
        public UInt16 MinDamage;
        public UInt16 MaxDamage;
        public UInt16 ManaCost;
        public UInt32 EffectSpell;

        public static Spell[] AllSpells { get; private set; }

        public static void Init(IDatabase database)
        {
            AllSpells = database.ExecuteReader("SELECT * FROM spells").ToIEnumerable().Select(c => new Spell
            {
                Id = (UInt32)c["id"],
                Level = (byte)c["level"],
                Name = c["name"],
                CastTime = (UInt16)c["castTime"],
                MinDamage = (UInt16)c["minDmg"],
                MaxDamage = (UInt16)c["maxDmg"],
                ManaCost = (UInt16)c["costMana"],
                EffectSpell = (UInt32)c["effectSpell"]
            }).ToArray();
        }

        public override string ToString()
        {
            return Id + ": " + Name;
        }
    }
}