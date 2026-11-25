using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace nuel;

public class SqliteCaptchaStore : ICaptchaStore
{
	private const string CreateSchemaSql = """
		CREATE TABLE IF NOT EXISTS CaptchaCodes (
			Id INTEGER NOT NULL PRIMARY KEY,
			Captcha INTEGER NOT NULL,
			CreationDate INTEGER NOT NULL,
			ExpiresAt INTEGER NOT NULL
		);
		CREATE INDEX IF NOT EXISTS IX_CaptchaCodes_ExpiresAt ON CaptchaCodes (ExpiresAt);
		""";

	private static readonly ConcurrentDictionary<string, long> LastCleanUpTicks = new();

	private readonly string _connectionString;
	private readonly TimeSpan _cleanUpInterval;
	private readonly bool _autoCreateTable;

	public SqliteCaptchaStore(IOptions<CaptchaOptions> options)
		: this(GetConfiguredOptions(options))
	{
	}

	private SqliteCaptchaStore(CaptchaOptions options)
		: this(options.ConnectionString!, options.CleanUpInterval, options.AutoCreateDatabaseTable)
	{
	}

	public SqliteCaptchaStore(
		string connectionString,
		TimeSpan? cleanUpInterval = null,
		bool autoCreateTable = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

		var builder = new SqliteConnectionStringBuilder(connectionString);
		if (string.IsNullOrWhiteSpace(builder.DataSource)
			|| builder.DataSource == ":memory:"
			|| builder.Mode == SqliteOpenMode.Memory)
		{
			throw new ArgumentException("The captcha store requires a file-backed SQLite database.", nameof(connectionString));
		}

		_connectionString = builder.ConnectionString;
		_cleanUpInterval = cleanUpInterval ?? TimeSpan.FromMinutes(10);
		_autoCreateTable = autoCreateTable;
	}

	public async Task EnsureDatabaseInitializedAsync(CancellationToken cancellationToken = default)
	{
		await using var connection = new SqliteConnection(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	public async Task StoreAsync(long id, int captcha, TimeSpan expiration, CancellationToken cancellationToken = default)
	{
		await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO CaptchaCodes (Id, Captcha, CreationDate, ExpiresAt)
			VALUES (@id, @captcha, @createdAt, @expiresAt);
			""";

		DateTime now = DateTime.UtcNow;
		command.Parameters.AddWithValue("@id", id);
		command.Parameters.AddWithValue("@captcha", captcha);
		command.Parameters.AddWithValue("@createdAt", now.Ticks);
		command.Parameters.AddWithValue("@expiresAt", now.Add(expiration).Ticks);

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		TryOpportunisticCleanUp();
	}

	public async Task<bool> ValidateAsync(long id, int userInput, CancellationToken cancellationToken = default)
	{
		await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		using var command = connection.CreateCommand();
		command.CommandText = """
			DELETE FROM CaptchaCodes
			WHERE Id = @id AND Captcha = @captcha AND ExpiresAt > @now;
			""";
		command.Parameters.AddWithValue("@id", id);
		command.Parameters.AddWithValue("@captcha", userInput);
		command.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);

		return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
	}

	public async Task<int> CleanUpAsync(TimeSpan? olderThan = null, CancellationToken cancellationToken = default)
	{
		await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
		using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM CaptchaCodes WHERE ExpiresAt <= @now";
		DateTime now = DateTime.UtcNow;
		command.Parameters.AddWithValue("@now", now.Ticks);

		if (olderThan is { } age)
		{
			command.CommandText += " AND CreationDate < @createdBefore";
			command.Parameters.AddWithValue("@createdBefore", now.Subtract(age).Ticks);
		}

		return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static CaptchaOptions GetConfiguredOptions(IOptions<CaptchaOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var configured = options.Value;
		if (string.IsNullOrWhiteSpace(configured.ConnectionString))
			throw new InvalidOperationException("CaptchaOptions.ConnectionString must be configured.");

		return configured;
	}

	private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
	{
		var connection = new SqliteConnection(_connectionString);
		try
		{
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			if (_autoCreateTable)
				await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

			return connection;
		}
		catch
		{
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		using var command = connection.CreateCommand();
		command.CommandText = CreateSchemaSql;
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private void TryOpportunisticCleanUp()
	{
		long now = Environment.TickCount64;
		if (LastCleanUpTicks.TryGetValue(_connectionString, out long last))
		{
			if (now - last < _cleanUpInterval.TotalMilliseconds
				|| !LastCleanUpTicks.TryUpdate(_connectionString, now, last))
				return;
		}
		else if (!LastCleanUpTicks.TryAdd(_connectionString, now))
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await CleanUpAsync().ConfigureAwait(false);
			}
			catch
			{
				// Opportunistic cleanup is best effort. Explicit cleanup still reports errors.
			}
		});
	}
}
