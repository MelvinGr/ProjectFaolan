using System.Linq;
using LibFaolan.Database;
using LibFaolan.Extentions;

namespace LibFaolan.Data
{
    public sealed class Spell
    {
        public ushort CastTime;
        public uint EffectSpell;
        public uint Id;
        public byte Level;
        public ushort ManaCost;
        public ushort MaxDamage;
        public ushort MinDamage;
        public string Name;
        public static Spell[] AllSpells { get; private set; }

        public static void Init(IDatabase database)
        {
            AllSpells = database.ExecuteReader("SELECT * FROM spells").ToIEnumerable().Select(c => new Spell
            {
                Id = (uint) c["id"],
                Level = (byte) c["level"],
                Name = (string) c["name"],
                CastTime = (ushort) c["castTime"],
                MinDamage = (ushort) c["minDmg"],
                MaxDamage = (ushort) c["maxDmg"],
                ManaCost = (ushort) c["costMana"],
                EffectSpell = (uint) c["effectSpell"]
            }).ToArray();
        }

        public override string ToString()
        {
            return Id + ": " + Name;
        }
    }
}