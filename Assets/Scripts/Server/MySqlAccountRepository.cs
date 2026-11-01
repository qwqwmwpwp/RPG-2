using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using RPG4.Networking.Contracts;

namespace RPG4.Server
{
    public sealed class AccountRecord
    {
        public ulong AccountId;
        public bool Disabled;
        public PasswordRecord Password;
    }

    public sealed class AdmissionProfile
    {
        public bool AccountExists, Disabled;
        public long CredentialVersion;
        public ulong CharacterId;
        public string DisplayName;
        public uint SpawnConfigVersion;
    }

    /// <summary>Each operation owns its own pooled connection. No connection/transaction spans KDF work.</summary>
    public sealed class MySqlAccountRepository
    {
        public const int SchemaVersion = 1;
        private readonly string connectionString;
        private readonly int commandTimeout;

        public MySqlAccountRepository(LoadedServerConfiguration configuration)
            : this(ResolveDatabasePaths(configuration), configuration.Settings.databaseTimeoutSeconds,
                (uint)Math.Min(64, configuration.Settings.maxPendingAuthentications + 2)) { }

        private MySqlAccountRepository(string connectionString, int operationSeconds, uint poolSize)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                Pooling = true, MinimumPoolSize = 0, MaximumPoolSize = poolSize,
                ConnectionTimeout = (uint)operationSeconds, DefaultCommandTimeout = (uint)operationSeconds,
                CancellationTimeout = 1, ConnectionReset = true,
                AllowLoadLocalInfile = false, AllowPublicKeyRetrieval = false, PersistSecurityInfo = false
            };
            // No silent TLS fallback and no unverified server certificate.
            if (builder.SslMode != MySqlSslMode.VerifyFull || string.IsNullOrEmpty(builder.Database))
                throw new ArgumentException("Database requires a database name and verified TLS.");
            this.connectionString = builder.ConnectionString; commandTimeout = operationSeconds;
        }

        private static string ResolveDatabasePaths(LoadedServerConfiguration configuration)
        {
            var builder = new MySqlConnectionStringBuilder(configuration.ConnectionString);
            foreach (string option in new[] { "SSL CA", "SSL Cert", "SSL Key", "Certificate File", "Server RSA Public Key File" })
                if (builder.TryGetValue(option, out object value) && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
                {
                    string full = Path.GetFullPath(Path.Combine(configuration.DirectoryPath, Convert.ToString(value)));
                    ServerConfiguration.RequireOutsideAssets(full, configuration.AssetsPath);
                    builder[option] = full;
                }
            return builder.ConnectionString;
        }

        private MySqlCommand Command(MySqlConnection connection, string sql, MySqlTransaction transaction = null)
        { return new MySqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeout }; }

        public async Task ValidateSchemaAsync(CancellationToken token)
        {
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await RequireMySql8Async(connection, token).ConfigureAwait(false);
                using (var command = Command(connection, "SELECT version FROM schema_version WHERE id=1"))
                {
                    object version = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (version == null || Convert.ToInt32(version) != SchemaVersion)
                        throw new InvalidOperationException("Unsupported database schema.");
                }
                const string shape = "SELECT a.account_id,a.normalized_username,a.disabled,a.password_algorithm,a.parameter_version,a.iterations,a.salt,a.password_hash,a.credential_version,c.character_id,c.display_name,c.spawn_config_version FROM accounts a LEFT JOIN characters c ON c.account_id=a.account_id WHERE 1=0";
                using (var command = Command(connection, shape))
                await using ((await command.ExecuteReaderAsync(token).ConfigureAwait(false)).ConfigureAwait(false)) { }
                const string indexes = "SELECT COUNT(*) FROM (SELECT table_name,index_name FROM information_schema.statistics WHERE table_schema=DATABASE() AND non_unique=0 AND ((table_name='accounts' AND index_name='uq_accounts_username') OR (table_name='characters' AND index_name='uq_characters_account')) GROUP BY table_name,index_name HAVING COUNT(*)=1 AND SUM(sub_part IS NOT NULL)=0 AND SUM((table_name='accounts' AND column_name='normalized_username') OR (table_name='characters' AND column_name='account_id'))=1) verified_indexes";
                using (var command = Command(connection, indexes))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 2)
                        throw new InvalidOperationException("Required database uniqueness is unavailable.");
                const string primaryKeys = "SELECT COUNT(*) FROM (SELECT table_name FROM information_schema.statistics WHERE table_schema=DATABASE() AND index_name='PRIMARY' AND table_name IN ('schema_version','accounts','characters') GROUP BY table_name HAVING COUNT(*)=1 AND SUM(sub_part IS NOT NULL)=0 AND SUM((table_name='schema_version' AND column_name='id') OR (table_name='accounts' AND column_name='account_id') OR (table_name='characters' AND column_name='character_id'))=1) verified_primary_keys";
                using (var command = Command(connection, primaryKeys))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 3)
                        throw new ServerOperationException("schema_primary_key_mismatch");
                const string engines = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('accounts','characters','schema_version') AND engine='InnoDB'";
                using (var command = Command(connection, engines))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 3)
                        throw new InvalidOperationException("Required database storage engine is unavailable.");
                await ValidateColumnsAsync(connection, token).ConfigureAwait(false);
                const string checks = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES' AND ((TABLE_NAME='schema_version' AND CONSTRAINT_NAME='ck_schema_version') OR (TABLE_NAME='accounts' AND CONSTRAINT_NAME IN ('ck_account_username','ck_account_disabled','ck_account_password','ck_account_credential_version')) OR (TABLE_NAME='characters' AND CONSTRAINT_NAME IN ('ck_character_account','ck_character_profile')))";
                using (var command = Command(connection, checks))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 7)
                        throw new ServerOperationException("schema_checks_missing");
            }
        }

        public async Task ProbeAsync(CancellationToken token)
        {
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = Command(connection, "SELECT version FROM schema_version WHERE id=1"))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != SchemaVersion)
                        throw new ServerOperationException("schema_version_mismatch");
            }
        }

        private async Task RequireMySql8Async(MySqlConnection connection, CancellationToken token)
        {
            using (var command = Command(connection, "SELECT VERSION()"))
            {
                string version = Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                if (!version.StartsWith("8.", StringComparison.Ordinal) || version.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ServerOperationException("mysql8_required");
            }
        }

        private async Task ValidateColumnsAsync(MySqlConnection connection, CancellationToken token)
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema_version.id"]="tinyint unsigned", ["schema_version.version"]="int",
                ["accounts.account_id"]="bigint unsigned", ["accounts.normalized_username"]="varchar(32)",
                ["accounts.disabled"]="tinyint unsigned", ["accounts.password_algorithm"]="varchar(32)",
                ["accounts.parameter_version"]="int", ["accounts.iterations"]="int", ["accounts.salt"]="varbinary(16)",
                ["accounts.password_hash"]="varbinary(32)", ["accounts.credential_version"]="bigint",
                ["characters.character_id"]="bigint unsigned", ["characters.account_id"]="bigint unsigned",
                ["characters.display_name"]="varchar(64)", ["characters.spawn_config_version"]="int unsigned"
            };
            using (var command = Command(connection, "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLLATION_NAME,EXTRA "
                + "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('schema_version','accounts','characters')"))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    string key = reader.GetString(0) + "." + reader.GetString(1);
                    if (!expected.TryGetValue(key, out string type) || !string.Equals(type, reader.GetString(2), StringComparison.OrdinalIgnoreCase)
                        || reader.GetString(3) != "NO") throw new ServerOperationException("schema_structure_mismatch");
                    if (key == "accounts.normalized_username" && (reader.IsDBNull(4) || reader.GetString(4) != "ascii_bin"))
                        throw new ServerOperationException("schema_username_collation_mismatch");
                    if ((key == "accounts.account_id" || key == "characters.character_id") && reader.GetString(5) != "auto_increment")
                        throw new ServerOperationException("schema_identity_mismatch");
                    expected.Remove(key);
                }
            }
            if (expected.Count != 0) throw new ServerOperationException("schema_structure_mismatch");
            using (var command = Command(connection, "SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() "
                + "AND TABLE_NAME='characters' AND COLUMN_NAME='account_id' AND REFERENCED_TABLE_SCHEMA=DATABASE() "
                + "AND REFERENCED_TABLE_NAME='accounts' AND REFERENCED_COLUMN_NAME='account_id'"))
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                    throw new ServerOperationException("schema_foreign_key_missing");
        }

        public async Task<AccountRecord> FindAccountAsync(string normalizedUsername, CancellationToken token)
        {
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = Command(connection, "SELECT account_id,disabled,password_algorithm,parameter_version,iterations,salt,password_hash,credential_version FROM accounts WHERE normalized_username=@username LIMIT 1"))
                {
                    command.Parameters.Add("@username", MySqlDbType.VarChar, 32).Value = normalizedUsername;
                    var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                        return new AccountRecord { AccountId = reader.GetUInt64(0), Disabled = reader.GetBoolean(1),
                            Password = new PasswordRecord { Algorithm = reader.GetString(2), ParameterVersion = reader.GetInt32(3),
                                Iterations = reader.GetInt32(4), Salt = (byte[])reader.GetValue(5), Hash = (byte[])reader.GetValue(6),
                                CredentialVersion = reader.GetInt64(7) } };
                    }
                }
            }
        }

        public async Task<bool> TryUpgradeCredentialsAsync(ulong accountId, long expectedVersion, PasswordRecord replacement, CancellationToken token)
        {
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = Command(connection, "UPDATE accounts SET password_algorithm=@algorithm,parameter_version=@parameters,iterations=@iterations,salt=@salt,password_hash=@hash,credential_version=@next WHERE account_id=@account AND credential_version=@expected AND disabled=0"))
                {
                    AddPassword(command, replacement);
                    command.Parameters.Add("@account", MySqlDbType.UInt64).Value = accountId;
                    command.Parameters.Add("@expected", MySqlDbType.Int64).Value = expectedVersion;
                    command.Parameters.Add("@next", MySqlDbType.Int64).Value = replacement.CredentialVersion;
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        public async Task<AdmissionProfile> ReadAdmissionProfileAsync(ulong accountId, CancellationToken token)
        {
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (var command = Command(connection, "SELECT a.disabled,a.credential_version,c.character_id,c.display_name,c.spawn_config_version FROM accounts a LEFT JOIN characters c ON c.account_id=a.account_id WHERE a.account_id=@account LIMIT 1"))
                {
                    command.Parameters.Add("@account", MySqlDbType.UInt64).Value = accountId;
                    var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return new AdmissionProfile();
                        return new AdmissionProfile { AccountExists = true, Disabled = reader.GetBoolean(0), CredentialVersion = reader.GetInt64(1),
                            CharacterId = reader.IsDBNull(2) ? 0 : reader.GetUInt64(2), DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            SpawnConfigVersion = reader.IsDBNull(4) ? 0 : reader.GetUInt32(4) };
                    }
                }
            }
        }

        public async Task<CreatedAccount> CreateAccountAsync(string username, PasswordRecord password, string displayName, uint spawnConfigVersion, CancellationToken token)
        {
            if (!EntryProtocol.IsUsername(username) || !PasswordHasher.IsSupported(password)
                || password.ParameterVersion != PasswordHasher.CurrentParameterVersion || password.CredentialVersion != 1
                || !EntryProtocol.IsUtf8(displayName, 1, EntryProtocol.MaxDisplayNameBytes) || spawnConfigVersion == 0)
                throw new ArgumentException("Invalid new account.");
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    try
                    {
                        ulong accountId;
                        using (var command = Command(connection, "INSERT INTO accounts(normalized_username,disabled,password_algorithm,parameter_version,iterations,salt,password_hash,credential_version) VALUES(@username,0,@algorithm,@parameters,@iterations,@salt,@hash,1)", transaction))
                        {
                            command.Parameters.Add("@username", MySqlDbType.VarChar, 32).Value = username.ToLowerInvariant();
                            AddPassword(command, password);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            accountId = checked((ulong)command.LastInsertedId);
                            if (accountId == 0) throw new ServerOperationException("database_identity_invalid");
                        }
                        ulong characterId;
                        using (var command = Command(connection, "INSERT INTO characters(account_id,display_name,spawn_config_version) VALUES(@account,@display,@spawn)", transaction))
                        {
                            command.Parameters.Add("@account", MySqlDbType.UInt64).Value = accountId;
                            command.Parameters.Add("@display", MySqlDbType.VarChar, 64).Value = displayName;
                            command.Parameters.Add("@spawn", MySqlDbType.UInt32).Value = spawnConfigVersion;
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            characterId = checked((ulong)command.LastInsertedId);
                            if (characterId == 0) throw new ServerOperationException("database_identity_invalid");
                        }
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        return new CreatedAccount(accountId, characterId);
                    }
                    catch
                    {
                        using (var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        { try { await transaction.RollbackAsync(recovery.Token).ConfigureAwait(false); } catch (Exception) { } }
                        throw;
                    }
                }
            }
        }

        private static void AddPassword(MySqlCommand command, PasswordRecord value)
        {
            command.Parameters.Add("@algorithm", MySqlDbType.VarChar, 32).Value = value.Algorithm;
            command.Parameters.Add("@parameters", MySqlDbType.Int32).Value = value.ParameterVersion;
            command.Parameters.Add("@iterations", MySqlDbType.Int32).Value = value.Iterations;
            command.Parameters.Add("@salt", MySqlDbType.VarBinary, PasswordHasher.SaltBytes).Value = value.Salt;
            command.Parameters.Add("@hash", MySqlDbType.VarBinary, PasswordHasher.HashBytes).Value = value.Hash;
        }

        /// <summary>Explicit administrative action only. Startup never calls this method.</summary>
        public async Task MigrateAsync(string sql, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(sql) || sql.Length > 131072) throw new ArgumentException("Invalid migration file.");
            var connection = new MySqlConnection(connectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                await RequireMySql8Async(connection, token).ConfigureAwait(false);
                const string lockName = "SHA2(CONCAT(DATABASE(), ':rpg4-schema'),256)";
                using (var command = Command(connection, "SELECT GET_LOCK(" + lockName + ",0)"))
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new ServerOperationException("schema_migration_busy");
                try
                {
                    bool exists;
                    using (var command = Command(connection, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='schema_version'"))
                        exists = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
                    if (exists)
                        using (var command = Command(connection, "SELECT version FROM schema_version WHERE id=1"))
                        {
                            object current = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                            if (current != null && Convert.ToInt32(current) != SchemaVersion)
                                throw new ServerOperationException("schema_version_mismatch");
                        }
                    // Explicit reviewed v1 batch. MySQL DDL commits implicitly; interrupted creation may be retried.
                    using (var command = Command(connection, sql))
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        using (var command = Command(connection, "SELECT RELEASE_LOCK(" + lockName + ")"))
                            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception) { MySqlConnection.ClearPool(connection); }
                }
            }
            await ValidateSchemaAsync(token).ConfigureAwait(false);
        }
    }

    public readonly struct CreatedAccount
    {
        public ulong AccountId { get; }
        public ulong CharacterId { get; }
        public CreatedAccount(ulong accountId, ulong characterId) { AccountId = accountId; CharacterId = characterId; }
    }
}
