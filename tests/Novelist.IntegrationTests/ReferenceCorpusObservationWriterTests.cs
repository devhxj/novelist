using Microsoft.Data.Sqlite;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusObservationWriterTests
{
    [Fact]
    public async Task UpsertAsyncUpdatesExistingObservationForTheSameGenerationKey()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var first = BuildObservation(valueText: "slow_tension", confidence: 0.80, evidenceStart: 0, evidenceEnd: 6);
        var second = first with
        {
            ValueText = "urgent_tension",
            Confidence = 0.95,
            Explanation = "retry updated value"
        };

        var firstIdentity = await ReferenceCorpusObservationWriter.UpsertAsync(connection, first, CancellationToken.None);
        var secondIdentity = await ReferenceCorpusObservationWriter.UpsertAsync(connection, second, CancellationToken.None);

        Assert.Equal(firstIdentity, secondIdentity);
        Assert.Equal(1, await ReadObservationCountAsync(connection));
        Assert.Equal("urgent_tension", await ReadStringAsync(connection, "value_text"));
        Assert.Equal(0.95, await ReadDoubleAsync(connection, "confidence"), precision: 6);
        Assert.Equal("retry updated value", await ReadStringAsync(connection, "explanation"));
    }

    [Fact]
    public async Task UpsertAsyncTreatsNullEvidenceBoundsAsTheSameGenerationKey()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        var first = BuildObservation(valueText: "auditory_pressure", confidence: 0.82, evidenceStart: null, evidenceEnd: null);
        var second = first with
        {
            ValueText = "auditory_pressure_retry",
            ValueJson = """[{"sense":"auditory","intensity":8}]"""
        };

        var firstIdentity = await ReferenceCorpusObservationWriter.UpsertAsync(connection, first, CancellationToken.None);
        var secondIdentity = await ReferenceCorpusObservationWriter.UpsertAsync(connection, second, CancellationToken.None);

        Assert.Equal(firstIdentity.ObservationId, secondIdentity.ObservationId);
        Assert.Equal(-1, firstIdentity.NormalizedEvidenceStart);
        Assert.Equal(-1, firstIdentity.NormalizedEvidenceEnd);
        Assert.Equal(1, await ReadObservationCountAsync(connection));
        Assert.Equal("auditory_pressure_retry", await ReadStringAsync(connection, "value_text"));
    }

    [Fact]
    public async Task UpsertAsyncKeepsDifferentEvidenceSpansAsSeparateObservations()
    {
        await using var connection = await OpenFixtureConnectionAsync();
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            BuildObservation(valueText: "first_span", confidence: 0.82, evidenceStart: 0, evidenceEnd: 4),
            CancellationToken.None);
        await ReferenceCorpusObservationWriter.UpsertAsync(
            connection,
            BuildObservation(valueText: "second_span", confidence: 0.84, evidenceStart: 5, evidenceEnd: 9),
            CancellationToken.None);

        Assert.Equal(2, await ReadObservationCountAsync(connection));
    }

    [Fact]
    public async Task UpsertAsyncKeepsConcurrentRetriesIdempotentAcrossConnections()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "corpus-observations.sqlite");
        try
        {
            await using (var setupConnection = await OpenFixtureConnectionAsync(databasePath))
            {
            }

            var tasks = Enumerable.Range(0, 8)
                .Select(async index =>
                {
                    await using var connection = await OpenFixtureConnectionAsync(databasePath, provision: false);
                    await ReferenceCorpusObservationWriter.UpsertAsync(
                        connection,
                        BuildObservation(
                            valueText: "retry_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            confidence: 0.80 + index / 100.0,
                            evidenceStart: 0,
                            evidenceEnd: 6),
                        CancellationToken.None);
                })
                .ToArray();

            await Task.WhenAll(tasks);

            await using var verifyConnection = await OpenFixtureConnectionAsync(databasePath, provision: false);
            Assert.Equal(1, await ReadObservationCountAsync(verifyConnection));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async ValueTask<SqliteConnection> OpenFixtureConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ProvisionFixtureAsync(connection);

        return connection;
    }

    private static async ValueTask<SqliteConnection> OpenFixtureConnectionAsync(
        string databasePath,
        bool provision = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        if (provision)
        {
            await ProvisionFixtureAsync(connection);
        }

        return connection;
    }

    private static async ValueTask ProvisionFixtureAsync(SqliteConnection connection)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS reference_anchors (
                  anchor_id INTEGER PRIMARY KEY
                );
                INSERT OR IGNORE INTO reference_anchors(anchor_id) VALUES (101);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO reference_text_nodes
                  (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                   chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                VALUES
                  ('node-1', 101, NULL, 'sentence', 1, 1,
                   1, 0, 10, 10, 'hash-node-1', '雨声贴着门缝往里挤。', '2026-07-09T00:00:00Z');

                INSERT OR IGNORE INTO reference_analysis_runs
                  (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
                   scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
                VALUES
                  ('run-1', 101, 'fake-analyzer', 'corpus-v1', 'fake', 'fake-model',
                   'sentence', 'running', 100, 12, 'node-1:sensory', '2026-07-09T00:00:00Z', NULL, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static ReferenceCorpusFeatureObservation BuildObservation(
        string valueText,
        double confidence,
        int? evidenceStart,
        int? evidenceEnd)
    {
        return new ReferenceCorpusFeatureObservation(
            NodeId: "node-1",
            NodeType: "sentence",
            RunId: "run-1",
            AnchorId: 101,
            FeatureFamily: "sensory",
            FeatureKey: "senses",
            ValueKind: "array",
            ValueText: valueText,
            ValueNum: null,
            ValueBool: null,
            ValueJson: """[{"sense":"auditory","intensity":7}]""",
            Intensity: 0.7,
            confidence,
            evidenceStart,
            evidenceEnd,
            Explanation: "fixture observation",
            ReviewState: "unverified",
            ValidityState: "active",
            SupersededByRunId: null,
            CreatedAt: DateTimeOffset.Parse("2026-07-09T00:00:00Z"));
    }

    private static async ValueTask<int> ReadObservationCountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reference_feature_observations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async ValueTask<string> ReadStringAsync(SqliteConnection connection, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + columnName + " FROM reference_feature_observations ORDER BY observation_id LIMIT 1;";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask<double> ReadDoubleAsync(SqliteConnection connection, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + columnName + " FROM reference_feature_observations ORDER BY observation_id LIMIT 1;";
        return Convert.ToDouble(await command.ExecuteScalarAsync());
    }
}
