using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Data
{
    public abstract class BaseObject
    {
        [Key] // [PrimaryKey, AutoIncrement] 
        public uint Id { get; set; }

        public override string ToString()
        {
            return $"{Id} [{GetType().Name}]";
        }
    }
}