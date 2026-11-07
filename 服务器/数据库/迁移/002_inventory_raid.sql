-- Explicit ServerAdmin migration only. New databases and interrupted v1 -> v2 upgrades are retryable.
-- Do not publish version 2 here: MySqlAccountRepository verifies the complete structure first.
CREATE TABLE IF NOT EXISTS schema_version (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    version INT NOT NULL,
    CONSTRAINT ck_schema_version CHECK (id = 1 AND version IN (1,2))
) ENGINE=InnoDB;
SET @rpg4_has_check = (SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
    WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME='schema_version' AND CONSTRAINT_NAME='ck_schema_version');
SET @rpg4_drop_check = IF(@rpg4_has_check=1, 'ALTER TABLE schema_version DROP CHECK ck_schema_version', 'SELECT 1');
PREPARE rpg4_schema_check FROM @rpg4_drop_check;
EXECUTE rpg4_schema_check;
DEALLOCATE PREPARE rpg4_schema_check;
ALTER TABLE schema_version ADD CONSTRAINT ck_schema_version CHECK (id = 1 AND version IN (1,2));

CREATE TABLE IF NOT EXISTS accounts (
    account_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    normalized_username VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    disabled TINYINT UNSIGNED NOT NULL DEFAULT 0,
    password_algorithm VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    parameter_version INT NOT NULL,
    iterations INT NOT NULL,
    salt VARBINARY(16) NOT NULL,
    password_hash VARBINARY(32) NOT NULL,
    credential_version BIGINT NOT NULL DEFAULT 1,
    UNIQUE KEY uq_accounts_username (normalized_username),
    CONSTRAINT ck_account_username CHECK (normalized_username REGEXP '^[a-z0-9_]{3,32}$'),
    CONSTRAINT ck_account_disabled CHECK (disabled IN (0,1)),
    CONSTRAINT ck_account_password CHECK (password_algorithm = 'pbkdf2-sha256' AND parameter_version IN (1,2)
        AND iterations BETWEEN 600000 AND 2000000 AND OCTET_LENGTH(salt) = 16 AND OCTET_LENGTH(password_hash) = 32),
    CONSTRAINT ck_account_credential_version CHECK (credential_version BETWEEN 1 AND 9223372036854775806)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS characters (
    character_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    account_id BIGINT UNSIGNED NOT NULL,
    display_name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    spawn_config_version INT UNSIGNED NOT NULL,
    UNIQUE KEY uq_characters_account (account_id),
    CONSTRAINT fk_characters_account FOREIGN KEY (account_id) REFERENCES accounts (account_id),
    CONSTRAINT ck_character_account CHECK (account_id > 0),
    CONSTRAINT ck_character_profile CHECK (OCTET_LENGTH(display_name) <= 64 AND spawn_config_version > 0)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS item_id_allocator (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    last_issued BIGINT UNSIGNED NOT NULL,
    CONSTRAINT ck_item_allocator CHECK (id=1)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS world_runs (
    world_run_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    server_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    status TINYINT UNSIGNED NOT NULL,
    started_utc DATETIME(6) NOT NULL,
    CONSTRAINT ck_world_status CHECK (status IN (0,1))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS inventory_profiles (
    account_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
    character_id BIGINT UNSIGNED NOT NULL,
    revision BIGINT NOT NULL,
    grant_version INT UNSIGNED NOT NULL,
    active_raid_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    stash_payload MEDIUMBLOB NOT NULL,
    backpack_payload MEDIUMBLOB NOT NULL,
    equipment_payload MEDIUMBLOB NOT NULL,
    UNIQUE KEY uq_inventory_character (character_id),
    CONSTRAINT fk_inventory_account FOREIGN KEY (account_id) REFERENCES accounts(account_id),
    CONSTRAINT fk_inventory_character FOREIGN KEY (character_id) REFERENCES characters(character_id),
    CONSTRAINT ck_inventory_revision CHECK (revision > 0 AND grant_version=1)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS inventory_operations (
    account_id BIGINT UNSIGNED NOT NULL,
    operation_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    operation_kind VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fingerprint BINARY(32) NOT NULL,
    response_payload MEDIUMBLOB NOT NULL,
    PRIMARY KEY (account_id, operation_id),
    CONSTRAINT fk_inventory_operation FOREIGN KEY (account_id) REFERENCES accounts(account_id)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS raid_runs (
    raid_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    world_run_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    account_id BIGINT UNSIGNED NOT NULL,
    character_id BIGINT UNSIGNED NOT NULL,
    session_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    status TINYINT UNSIGNED NOT NULL,
    revision BIGINT NOT NULL,
    entry_payload MEDIUMBLOB NOT NULL,
    settlement_payload MEDIUMBLOB NULL,
    outcome TINYINT UNSIGNED NULL,
    created_utc DATETIME(6) NOT NULL,
    KEY ix_raid_world_status (world_run_id, status),
    CONSTRAINT fk_raid_world FOREIGN KEY (world_run_id) REFERENCES world_runs(world_run_id),
    CONSTRAINT fk_raid_account FOREIGN KEY (account_id) REFERENCES accounts(account_id),
    CONSTRAINT fk_raid_character FOREIGN KEY (character_id) REFERENCES characters(character_id),
    CONSTRAINT ck_raid_state CHECK (status IN (0,1,2,3,4) AND revision > 0)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS item_ownership (
    item_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
    owner_kind TINYINT UNSIGNED NOT NULL,
    owner_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    raid_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    bag_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    KEY ix_item_world_raid (owner_kind, owner_scope, raid_id),
    CONSTRAINT ck_item_owner CHECK (item_id > 0 AND owner_kind IN (0,1,2))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS raid_drop_bags (
    bag_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    raid_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    world_run_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    published TINYINT UNSIGNED NOT NULL DEFAULT 0,
    payload MEDIUMBLOB NOT NULL,
    UNIQUE KEY uq_drop_raid (raid_id),
    CONSTRAINT fk_drop_raid FOREIGN KEY (raid_id) REFERENCES raid_runs(raid_id),
    CONSTRAINT ck_drop_published CHECK (published IN (0,1))
) ENGINE=InnoDB;
INSERT INTO item_id_allocator(id,last_issued) VALUES(1,0) ON DUPLICATE KEY UPDATE id=id;
INSERT INTO schema_version(id,version) VALUES(1,1) ON DUPLICATE KEY UPDATE id=id;
