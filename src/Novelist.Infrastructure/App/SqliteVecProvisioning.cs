using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

public interface ISqliteVecTableProvisioner
{
    ValueTask ProvisionAsync(
        string databasePath,
        SqliteVecProvisionRequest request,
        CancellationToken cancellationToken);
}

public interface ISqliteVecQueryProvider
{
    ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
        string databasePath,
        SqliteVecSearchRequest request,
        CancellationToken cancellationToken);
}

public interface ISqliteVecExtensionResolver
{
    SqliteVecExtensionResolution Resolve();
}

public sealed record SqliteVecExtensionResolution(
    bool Available,
    string ExtensionPath,
    string RuntimeIdentifier,
    string Status,
    string Error);

public sealed record SqliteVecProvisionRequest(
    string TableName,
    int Dimensions,
    string CreateTableSql,
    IReadOnlyList<SqliteVecVectorRecord> Vectors);

public sealed record SqliteVecVectorRecord(
    long RowId,
    string ChunkId,
    IReadOnlyList<float> Vector);

public sealed record SqliteVecSearchRequest(
    string TableName,
    int Dimensions,
    IReadOnlyList<float> QueryVector,
    int TopK);

public sealed record SqliteVecSearchRecord(
    long RowId,
    double Distance);

public sealed class SqliteVecTableProvisioner : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
{
    private readonly ISqliteVecExtensionResolver _extensionResolver;

    public SqliteVecTableProvisioner(string? extensionPath = null)
        : this(string.IsNullOrWhiteSpace(extensionPath)
            ? new PackagedSqliteVecExtensionResolver()
            : new FixedSqliteVecExtensionResolver(extensionPath))
    {
    }

    public SqliteVecTableProvisioner(ISqliteVecExtensionResolver extensionResolver)
    {
        _extensionResolver = extensionResolver ?? throw new ArgumentNullException(nameof(extensionResolver));
    }

    public async ValueTask ProvisionAsync(
        string databasePath,
        SqliteVecProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(request);
        var extensionPath = ResolveExtensionPath();

        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadExtension(extensionPath);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = request.CreateTableSql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {QuoteIdentifier(request.TableName)};";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var vector in request.Vectors)
        {
            if (vector.Vector.Count != request.Dimensions)
            {
                throw new InvalidOperationException("Vector dimensions do not match the sqlite-vec table definition.");
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {QuoteIdentifier(request.TableName)}(rowid, embedding) VALUES ($rowid, vec_f32($embedding));";
            insert.Parameters.AddWithValue("$rowid", vector.RowId);
            insert.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(vector.Vector));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
        string databasePath,
        SqliteVecSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(request);
        var extensionPath = ResolveExtensionPath();

        if (request.QueryVector.Count != request.Dimensions)
        {
            throw new InvalidOperationException("Query vector dimensions do not match the sqlite-vec table definition.");
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadExtension(extensionPath);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT rowid, distance
            FROM {QuoteIdentifier(request.TableName)}
            WHERE embedding MATCH vec_f32($query)
            ORDER BY distance
            LIMIT $top_k;
            """;
        command.Parameters.AddWithValue("$query", JsonSerializer.Serialize(request.QueryVector));
        command.Parameters.AddWithValue("$top_k", request.TopK);

        var results = new List<SqliteVecSearchRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SqliteVecSearchRecord(reader.GetInt64(0), reader.GetDouble(1)));
        }

        return results;
    }

    public static string BuildVectorTableName(long novelId, int dimensions)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }

        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Vector dimensions must be positive.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"vec_novel_{novelId}_{dimensions}");
    }

    public static string BuildCreateTableSql(string tableName, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(tableName) ||
            tableName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
        {
            throw new ArgumentException("Vector table name must be a simple SQLite identifier.", nameof(tableName));
        }

        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Vector dimensions must be positive.");
        }

        return $"CREATE VIRTUAL TABLE IF NOT EXISTS {QuoteIdentifier(tableName)} USING vec0(embedding float[{dimensions}]);";
    }

    private string ResolveExtensionPath()
    {
        var resolved = _extensionResolver.Resolve();
        if (resolved.Available && !string.IsNullOrWhiteSpace(resolved.ExtensionPath))
        {
            return resolved.ExtensionPath;
        }

        var message = string.IsNullOrWhiteSpace(resolved.Error)
            ? "sqlite-vec native extension is unavailable."
            : resolved.Error;
        throw new InvalidOperationException(message);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class FixedSqliteVecExtensionResolver : ISqliteVecExtensionResolver
    {
        private readonly string _extensionPath;

        public FixedSqliteVecExtensionResolver(string extensionPath)
        {
            _extensionPath = extensionPath;
        }

        public SqliteVecExtensionResolution Resolve()
        {
            if (string.IsNullOrWhiteSpace(_extensionPath))
            {
                return new SqliteVecExtensionResolution(
                    false,
                    string.Empty,
                    PackagedSqliteVecExtensionResolver.CurrentRuntimeIdentifier(),
                    "not_configured",
                    "sqlite-vec extension path is not configured.");
            }

            var fullPath = Path.GetFullPath(_extensionPath);
            return File.Exists(fullPath)
                ? new SqliteVecExtensionResolution(
                    true,
                    fullPath,
                    PackagedSqliteVecExtensionResolver.CurrentRuntimeIdentifier(),
                    "available",
                    string.Empty)
                : new SqliteVecExtensionResolution(
                    false,
                    string.Empty,
                    PackagedSqliteVecExtensionResolver.CurrentRuntimeIdentifier(),
                    "not_found",
                    "sqlite-vec extension path does not exist.");
        }
    }
}

public sealed class PackagedSqliteVecExtensionResolver : ISqliteVecExtensionResolver
{
    private const string OverrideEnvironmentVariable = "NOVELIST_SQLITE_VEC_PATH";

    private readonly string _baseDirectory;
    private readonly string? _overridePath;
    private readonly string _runtimeIdentifier;

    public PackagedSqliteVecExtensionResolver(
        string? baseDirectory = null,
        string? overridePath = null,
        string? runtimeIdentifier = null)
    {
        _baseDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory);
        _overridePath = string.IsNullOrWhiteSpace(overridePath)
            ? Environment.GetEnvironmentVariable(OverrideEnvironmentVariable)
            : overridePath;
        _runtimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? CurrentRuntimeIdentifier()
            : runtimeIdentifier;
    }

    public SqliteVecExtensionResolution Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_overridePath))
        {
            var overridePath = Path.GetFullPath(Path.IsPathRooted(_overridePath)
                ? _overridePath
                : Path.Combine(_baseDirectory, _overridePath));
            return File.Exists(overridePath)
                ? Available(overridePath)
                : new SqliteVecExtensionResolution(
                    false,
                    string.Empty,
                    _runtimeIdentifier,
                    "not_found",
                    "sqlite-vec native extension override path does not exist.");
        }

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate))
            {
                return Available(candidate);
            }
        }

        return new SqliteVecExtensionResolution(
            false,
            string.Empty,
            _runtimeIdentifier,
            "not_found",
            $"sqlite-vec native extension was not found for {_runtimeIdentifier}. Expected one of: {string.Join(", ", CandidateFileNames(_runtimeIdentifier))}.");
    }

    public static string CurrentRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : "unknown";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }

    private SqliteVecExtensionResolution Available(string path)
    {
        return new SqliteVecExtensionResolution(
            true,
            Path.GetFullPath(path),
            _runtimeIdentifier,
            "available",
            string.Empty);
    }

    private IEnumerable<string> CandidatePaths()
    {
        var directories = new[]
        {
            Path.Combine(_baseDirectory, "runtimes", _runtimeIdentifier, "native"),
            Path.Combine(_baseDirectory, "native"),
            Path.Combine(_baseDirectory, "sqlite-vec", _runtimeIdentifier),
            _baseDirectory
        };

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var fileName in CandidateFileNames(_runtimeIdentifier))
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }

    private static IReadOnlyList<string> CandidateFileNames(string runtimeIdentifier)
    {
        if (runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
        {
            return ["vec0.dll", "sqlite_vec.dll", "sqlite-vec.dll"];
        }

        if (runtimeIdentifier.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
        {
            return ["vec0.dylib", "libvec0.dylib", "sqlite_vec.dylib", "libsqlite_vec.dylib"];
        }

        return ["vec0.so", "libvec0.so", "sqlite_vec.so", "libsqlite_vec.so"];
    }
}
