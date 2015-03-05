using System.Collections.Generic;
using LibFaolan.Data;

namespace LibFaolan.Database
{
    public interface IDatabase
    {
        ConanMap[] AllMaps { get; }
        bool Connect();
        void Disconnect();
        bool IsConnected();
        // Used to execute a command that will not return any data, for example Insert, update or delete.
        int ExecuteNonQuery(string query);
        // Used to execute a command that will return 0 or more records, for example Select.
        object ExecuteReader(string query);
        // Used to execute a command that will return only 1 value, for example Select Count(*).
        T ExecuteScalar<T>(string query);
        // ...
        IEnumerable<Dictionary<string, dynamic>> ExecuteDynamic(string query);
        //////////////////////////////////////////////////////////////////////////////////////////////

        bool CheckLogin(string username, string password);
    }
}