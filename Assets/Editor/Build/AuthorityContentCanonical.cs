using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEditor.Build;
using UnityEngine;

namespace RPG4.Editor.Build
{
    [Serializable]
    internal sealed class AuthorityField
    {
        public string path, type, value;
        public AuthorityField(string path, string type, string value) { this.path = path; this.type = type; this.value = value; }
    }
    [Serializable]
    internal sealed class AuthorityRecord
    {
        public string key, source, type;
        public AuthorityField[] fields;
    }
    [Serializable]
    internal sealed class AuthorityPayload
    {
        public string rule = EntryContentManifest.GenerationRule;
        public uint protocolVersion = EntryProtocol.Version;
        public string unityVersion = "2022.3.62f3";
        public string scope = "Authority scene/prefab/component and ordered item-fragment projections; standalone UI/Frontend/assets/local settings excluded.";
        public string sourcePolicy = "Explicit shared-authority and Mirror Weaver source roots, owning assembly definitions and shared compilation binaries; mixed authority/presentation source files are compatibility units hashed whole after strict decoding and UTF8/LF normalization.";
        public string ordering = "Ordinal project-relative case-preserving paths with /; stable GUID+local IDs; original array and NetworkBehaviour indices; no target/time/instance/managed-reference IDs/output/self digest.";
        public AuthorityRecord[] records;
    }
    internal sealed class AuthoritySnapshot
    {
        public string Json { get; }
        public string Hash { get; }
        public int RecordCount { get; }
        public AuthoritySnapshot(IEnumerable<AuthorityRecord> records)
        {
            AuthorityRecord[] ordered = records.OrderBy(record => record.key, StringComparer.Ordinal).ToArray();
            if (ordered.Select(record => record.key).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
                throw new BuildFailedException("Duplicate authority manifest record key.");
            foreach (AuthorityRecord record in ordered)
                record.fields = record.fields.OrderBy(field => field.path, StringComparer.Ordinal).ToArray();
            Json = JsonUtility.ToJson(new AuthorityPayload { records = ordered }, false) + "\n";
            Hash = AuthorityContentCanonical.Hash(new UTF8Encoding(false, true).GetBytes(Json));
            RecordCount = ordered.Length;
        }
    }
    internal static class AuthorityContentCanonical
    {
        internal static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new BuildFailedException("Non-finite authority value.");
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
        internal static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
        internal static string TextHash(string text) => Hash(new UTF8Encoding(false, true).GetBytes(text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n')));
    }
}
