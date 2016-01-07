using System.Data.Common;
using LibFaolan.Other;

namespace LibFaolan.Database
{
    public interface IDatabase
    {
        Logger Logger { get; }
        bool Connect();
        void Disconnect();
        bool IsConnected();
        // Used to execute a command that will not return any data, for example Insert, update or delete.
        int ExecuteNonQuery(string query);
        // Used to execute a command that will return 0 or more records, for example Select.
        DbDataReader ExecuteReader(string query);
        // Used to execute a command that will return only 1 value, for example Select Count(*).
        T ExecuteScalar<T>(string query);

        long LastInsertRowID();
    }
}