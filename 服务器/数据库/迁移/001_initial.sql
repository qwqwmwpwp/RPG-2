-- Explicit ServerAdmin migration only. MySQL 8 / InnoDB. DDL commits implicitly.
-- Safe to retry interrupted creation; never drops or rewrites an existing table/version.
CREATE TABLE IF NOT EXISTS schema_version (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    version INT NOT NULL,
    CONSTRAINT ck_schema_version CHECK (id = 1 AND version = 1)
) ENGINE=InnoDB;

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

INSERT INTO schema_version (id, version) VALUES (1, 1)
ON DUPLICATE KEY UPDATE version = version;
