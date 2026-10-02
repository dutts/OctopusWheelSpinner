using Microsoft.Data.Sqlite;

namespace OctopusWheelSpinner;

public sealed class SpinStore
{
    private readonly string _connectionString;

    public SpinStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (dir is not null) Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={dbPath}";

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS spin_attempts (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                attempted_at   TEXT NOT NULL,
                account_number TEXT NOT NULL,
                fuel_type      TEXT NOT NULL,
                success        INTEGER NOT NULL,
                points_won     INTEGER NULL,
                error          TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Record(string account, FuelType fuel, SpinResult result)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO spin_attempts (attempted_at, account_number, fuel_type, success, points_won, error)
            VALUES ($at, $account, $fuel, $success, $points, $error);
            """;
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$fuel", fuel.ToString());
        cmd.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$points", (object?)result.Points ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
