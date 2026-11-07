using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace RPG4.Server.Inventory
{
    public static class InventorySchema
    {
        public static async Task ValidateAsync(MySqlConnection connection, int timeout, CancellationToken token)
        {
            const string tables = "'item_id_allocator','world_runs','inventory_profiles','inventory_operations','raid_runs','item_ownership','raid_drop_bags'";
            MySqlCommand Command(string sql) => new MySqlCommand(sql, connection) { CommandTimeout = timeout };
            using (var command = Command("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN (" + tables + ") AND ENGINE='InnoDB'"))
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 7)
                    throw new InventorySchemaException("inventory_schema_engine_missing");
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_id_allocator.id"]="tinyint unsigned", ["item_id_allocator.last_issued"]="bigint unsigned",
                ["world_runs.world_run_id"]="char(32)", ["world_runs.server_id"]="varchar(64)", ["world_runs.status"]="tinyint unsigned", ["world_runs.started_utc"]="datetime(6)",
                ["inventory_profiles.account_id"]="bigint unsigned", ["inventory_profiles.character_id"]="bigint unsigned", ["inventory_profiles.revision"]="bigint", ["inventory_profiles.grant_version"]="int unsigned",
                ["inventory_profiles.active_raid_id"]="char(32)", ["inventory_profiles.stash_payload"]="mediumblob", ["inventory_profiles.backpack_payload"]="mediumblob", ["inventory_profiles.equipment_payload"]="mediumblob",
                ["inventory_operations.account_id"]="bigint unsigned", ["inventory_operations.operation_id"]="varchar(64)", ["inventory_operations.operation_kind"]="varchar(16)", ["inventory_operations.fingerprint"]="binary(32)", ["inventory_operations.response_payload"]="mediumblob",
                ["raid_runs.raid_id"]="char(32)", ["raid_runs.world_run_id"]="char(32)", ["raid_runs.account_id"]="bigint unsigned", ["raid_runs.character_id"]="bigint unsigned", ["raid_runs.session_id"]="char(32)",
                ["raid_runs.status"]="tinyint unsigned", ["raid_runs.revision"]="bigint", ["raid_runs.entry_payload"]="mediumblob", ["raid_runs.settlement_payload"]="mediumblob", ["raid_runs.outcome"]="tinyint unsigned", ["raid_runs.created_utc"]="datetime(6)",
                ["item_ownership.item_id"]="bigint unsigned", ["item_ownership.owner_kind"]="tinyint unsigned", ["item_ownership.owner_scope"]="varchar(64)", ["item_ownership.raid_id"]="char(32)", ["item_ownership.bag_id"]="char(32)",
                ["raid_drop_bags.bag_id"]="char(32)", ["raid_drop_bags.raid_id"]="char(32)", ["raid_drop_bags.world_run_id"]="char(32)", ["raid_drop_bags.published"]="tinyint unsigned", ["raid_drop_bags.payload"]="mediumblob"
            };
            var nullable = new HashSet<string> { "inventory_profiles.active_raid_id", "raid_runs.settlement_payload", "raid_runs.outcome", "item_ownership.raid_id", "item_ownership.bag_id" };
            using (var command = Command("SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN (" + tables + ")"))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string key = reader.GetString(0) + "." + reader.GetString(1);
                        if (!expected.TryGetValue(key, out string type)) continue;
                        if (reader.GetString(2) != type || (reader.GetString(3) == "YES") != nullable.Contains(key)
                            || ((type.StartsWith("char(", StringComparison.Ordinal) || type.StartsWith("varchar(", StringComparison.Ordinal))
                                && (reader.IsDBNull(4) || reader.GetString(4) != "ascii_bin")))
                            throw new InventorySchemaException("inventory_schema_column_invalid");
                        expected.Remove(key);
                    }
            }
            if (expected.Count != 0) throw new InventorySchemaException("inventory_schema_column_missing");
            var keys = new Dictionary<string, string>
            {
                ["item_id_allocator.PRIMARY"]="id", ["world_runs.PRIMARY"]="world_run_id", ["inventory_profiles.PRIMARY"]="account_id", ["inventory_profiles.uq_inventory_character"]="character_id",
                ["inventory_operations.PRIMARY"]="account_id,operation_id", ["raid_runs.PRIMARY"]="raid_id", ["item_ownership.PRIMARY"]="item_id", ["raid_drop_bags.PRIMARY"]="bag_id", ["raid_drop_bags.uq_drop_raid"]="raid_id"
            };
            using (var command = Command("SELECT TABLE_NAME,INDEX_NAME,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX),SUM(SUB_PART IS NOT NULL) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN (" + tables + ") AND NON_UNIQUE=0 GROUP BY TABLE_NAME,INDEX_NAME"))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string key = reader.GetString(0) + "." + reader.GetString(1);
                        if (!keys.TryGetValue(key, out string columns)) continue;
                        if (reader.GetString(2) != columns || reader.GetInt32(3) != 0) throw new InventorySchemaException("inventory_schema_key_invalid");
                        keys.Remove(key);
                    }
            }
            if (keys.Count != 0) throw new InventorySchemaException("inventory_schema_key_missing");
            const string checks = "'ck_item_allocator','ck_world_status','ck_inventory_revision','ck_raid_state','ck_item_owner','ck_drop_published'";
            using (var command = Command("SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME IN (" + tables + ") AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES' AND CONSTRAINT_NAME IN (" + checks + ")"))
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 6) throw new InventorySchemaException("inventory_schema_check_missing");
            using (var command = Command("SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() AND REFERENCED_TABLE_SCHEMA=DATABASE() AND ((TABLE_NAME='inventory_profiles' AND ((COLUMN_NAME='account_id' AND REFERENCED_TABLE_NAME='accounts' AND REFERENCED_COLUMN_NAME='account_id') OR (COLUMN_NAME='character_id' AND REFERENCED_TABLE_NAME='characters' AND REFERENCED_COLUMN_NAME='character_id'))) OR (TABLE_NAME='inventory_operations' AND COLUMN_NAME='account_id' AND REFERENCED_TABLE_NAME='accounts' AND REFERENCED_COLUMN_NAME='account_id') OR (TABLE_NAME='raid_runs' AND ((COLUMN_NAME='world_run_id' AND REFERENCED_TABLE_NAME='world_runs' AND REFERENCED_COLUMN_NAME='world_run_id') OR (COLUMN_NAME='account_id' AND REFERENCED_TABLE_NAME='accounts' AND REFERENCED_COLUMN_NAME='account_id') OR (COLUMN_NAME='character_id' AND REFERENCED_TABLE_NAME='characters' AND REFERENCED_COLUMN_NAME='character_id'))) OR (TABLE_NAME='raid_drop_bags' AND COLUMN_NAME='raid_id' AND REFERENCED_TABLE_NAME='raid_runs' AND REFERENCED_COLUMN_NAME='raid_id'))"))
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 7) throw new InventorySchemaException("inventory_schema_foreign_key_missing");
        }
    }
}
