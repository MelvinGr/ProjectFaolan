using System.Collections.Generic;
using System.Data;

namespace LibFaolan.Extentions
{
    public static class DictionaryExtentions
    {
        // http://paul.kinlan.me/idatarecord-fields-to-dictionary-extension-me/
        public static Dictionary<string, object> FieldsToDictionary(this IDataRecord dataRecord)
        {
            var fieldBag = new Dictionary<string, object>(dataRecord.FieldCount);
            for (var fieldIdx = 0; fieldIdx < dataRecord.FieldCount; fieldIdx++)
            {
                var name = dataRecord.GetName(fieldIdx);
                var value = dataRecord[fieldIdx];
                fieldBag.Add(name, value);
            }
            return fieldBag;
        }
    }
}