using System.IO;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.Services;

internal static class WindowsSqliteReader
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadOnly = 0x00000001;
    private static readonly IntPtr SqliteTransient = new(-1);

    public static string? ReadItemValue(string databasePath, string key)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(databasePath, out database, SqliteOpenReadOnly, null) != SqliteOk)
            {
                return null;
            }

            _ = sqlite3_busy_timeout(database, 750);
            const string query = "SELECT value FROM ItemTable WHERE key = ?1 LIMIT 1;";
            if (sqlite3_prepare_v2(database, query, -1, out statement, IntPtr.Zero) != SqliteOk
                || sqlite3_bind_text(statement, 1, key, -1, SqliteTransient) != SqliteOk
                || sqlite3_step(statement) != SqliteRow)
            {
                return null;
            }

            IntPtr valuePointer = sqlite3_column_text(statement, 0);
            int byteCount = sqlite3_column_bytes(statement, 0);
            return valuePointer == IntPtr.Zero || byteCount <= 0
                ? null
                : Marshal.PtrToStringUTF8(valuePointer, byteCount);
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return null;
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                _ = sqlite3_finalize(statement);
            }
            if (database != IntPtr.Zero)
            {
                _ = sqlite3_close(database);
            }
        }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr database,
        int flags,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? virtualFileSystem);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string query,
        int byteCount,
        out IntPtr statement,
        IntPtr remainingQuery);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(
        IntPtr statement,
        int index,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int byteCount,
        IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
}
