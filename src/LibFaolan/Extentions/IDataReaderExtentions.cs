using System.Collections.Generic;
using System.Data;

namespace LibFaolan.Extentions
{
    public static class DataReaderExtentions
    {
        // http://paul.kinlan.me/idatarecord-fields-to-dictionary-extension-me/
        public static Dictionary<string, dynamic> ToDictionary(this IDataReader dataRecord)
        {
            var fieldBag = new Dictionary<string, dynamic>(dataRecord.FieldCount);
            while (dataRecord.Read())
            {
                for (var i = 0; i < dataRecord.FieldCount; i++)
                    fieldBag.Add(dataRecord.GetName(i), dataRecord.GetValue(i));
            }

            return fieldBag;
        }

        public static IEnumerable<Dictionary<string, dynamic>> ToIEnumerable(this IDataReader dataRecord)
        {
            while (dataRecord.Read())
            {
                var row = new Dictionary<string, dynamic>(dataRecord.FieldCount);
                for (var i = 0; i < dataRecord.FieldCount; i++)
                    row.Add(dataRecord.GetName(i), dataRecord.GetValue(i));

                yield return row;
            }
        }
    }
}