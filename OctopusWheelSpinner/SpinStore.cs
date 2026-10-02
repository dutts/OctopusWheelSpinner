using Microsoft.Data.Sqlite;

namespace OctopusWheelSpinner;

public sealed record SpinAttempt(long Id, DateTimeOffset AttemptedAt, string Fuel, bool Success, int? PointsWon, string? Error);

public sealed record SpinSummary(int Attempts, int Successful, int Failed, long TotalPointsWon);

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

    public (SpinSummary Summary, List<SpinAttempt> Attempts) Query(int limit, FuelType? fuel, bool? success)
    {
        var where = new List<string>();
        if (fuel is not null) where.Add("fuel_type = $fuel");
        if (success is not null) where.Add("success = $success");
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var conn = Open();

        void Bind(SqliteCommand cmd)
        {
            if (fuel is not null) cmd.Parameters.AddWithValue("$fuel", fuel.ToString());
            if (success is not null) cmd.Parameters.AddWithValue("$success", success.Value ? 1 : 0);
        }

        using var summaryCmd = conn.CreateCommand();
        summaryCmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(success), 0), COALESCE(SUM(points_won), 0) FROM spin_attempts {clause}";
        Bind(summaryCmd);
        using var sr = summaryCmd.ExecuteReader();
        sr.Read();
        var total = sr.GetInt32(0);
        var ok = sr.GetInt32(1);
        var summary = new SpinSummary(total, ok, total - ok, sr.GetInt64(2));

        using var listCmd = conn.CreateCommand();
        listCmd.CommandText = $"""
            SELECT id, attempted_at, fuel_type, success, points_won, error
            FROM spin_attempts {clause}
            ORDER BY id DESC
            LIMIT $limit
            """;
        Bind(listCmd);
        listCmd.Parameters.AddWithValue("$limit", limit);
        var attempts = new List<SpinAttempt>();
        using var lr = listCmd.ExecuteReader();
        while (lr.Read())
            attempts.Add(new SpinAttempt(
                lr.GetInt64(0),
                DateTimeOffset.Parse(lr.GetString(1)),
                lr.GetString(2),
                lr.GetInt32(3) == 1,
                lr.IsDBNull(4) ? null : lr.GetInt32(4),
                lr.IsDBNull(5) ? null : lr.GetString(5)));
        return (summary, attempts);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
