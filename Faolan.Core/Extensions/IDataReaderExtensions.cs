//using System.Data;
//using System.Data.Common;

namespace Faolan.Core.Extensions
{
    /*public static class DataReaderExtentions
    {
        public static Dictionary<string, dynamic> ToDictionary(this DbDataReader dataRecord)
        {
            var fieldBag = new Dictionary<string, dynamic>(dataRecord.FieldCount);
            while (dataRecord.Read())
                for (var i = 0; i < dataRecord.FieldCount; i++)
                {
                    var typ = dataRecord.GetFieldType(i);
                    if (typ == typeof(float))
                        fieldBag.Add(dataRecord.GetName(i), (float) dataRecord.GetDecimal(i));
                    else if (typ == typeof(double))
                        fieldBag.Add(dataRecord.GetName(i), (double) dataRecord.GetDecimal(i));
                    else
                        fieldBag.Add(dataRecord.GetName(i), dataRecord.GetValue(i));
                }

            return fieldBag;
        }

        public static IEnumerable<Dictionary<string, dynamic>> ToIEnumerable(this IDataReader dataRecord)
        {
            while (dataRecord.Read())
            {
                // dataRecord.GetValue fails on float/double (wrong value), so do it like this
                var fieldBag = new Dictionary<string, dynamic>(dataRecord.FieldCount);
                for (var i = 0; i < dataRecord.FieldCount; i++)
                {
                    var typ = dataRecord.GetFieldType(i);
                    if (typ == typeof(float))
                        fieldBag.Add(dataRecord.GetName(i), (float) dataRecord.GetDecimal(i));
                    else if (typ == typeof(double))
                        fieldBag.Add(dataRecord.GetName(i), (double) dataRecord.GetDecimal(i));
                    else
                        fieldBag.Add(dataRecord.GetName(i), dataRecord.GetValue(i));
                }

                yield return fieldBag;
            }
        }
    }*/
}