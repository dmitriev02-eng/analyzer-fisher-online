using System;
using Microsoft.Data.Sqlite;

namespace FisherLogger
{
    /// <summary>MASTER-01: дешёвая отметка «в базе появились новые забросы/уловы» (только чтение). Окна карты и Советника
    /// опрашивают её таймером и обновляются сами — раньше новые уловы были видны только после повторного открытия.</summary>
    public static class LiveWatch
    {
        public static string Stamp(string dbPath)
        {
            try
            {
                using var c = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT (SELECT COALESCE(MAX(id),0) FROM catches) || '/' || (SELECT COUNT(*) FROM catches WHERE status='confirmed') || '/' || " +
                                  "(SELECT COALESCE(MAX(id),0) FROM casts) || '/' || (SELECT COUNT(*) FROM casts WHERE outcome IN ('caught','missed','no_bite','unknown'))";
                return Convert.ToString(cmd.ExecuteScalar()) ?? "";
            }
            catch { return ""; }
        }
    }
}
