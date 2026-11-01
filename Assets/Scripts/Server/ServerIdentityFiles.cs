using System;
using System.IO;
using System.Text;
using Mirror.BouncyCastle.Crypto.Parameters;
using Mirror.Transports.Encryption;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Server
{
    public static class ServerIdentityFiles
    {
        [Serializable]
        private sealed class SerializedPair
        { public string PublicKeyFingerprint; public string PublicKey; public string PrivateKey; }

        public static string ValidateExisting(string path, string assetsPath)
        {
            ServerConfiguration.RequireOutsideAssets(path, assetsPath);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 65536) throw new ServerOperationException("server_identity_missing");
                EncryptionCredentials credentials = EncryptionCredentials.LoadFromFile(path);
                if (!EntryProtocol.IsLowerHex(credentials.PublicKeyFingerprint, EntryProtocol.HashLength) || credentials.PrivateKey == null)
                    throw new ServerOperationException("server_identity_invalid");
                ECPrivateKeyParameters key = credentials.PrivateKey;
                var publicKey = EncryptionCredentials.DeserializePublicKey(new ArraySegment<byte>(credentials.PublicKeySerialized)) as ECPublicKeyParameters;
                // Compare EC points/domains. Re-encoding can change named-curve DER into explicit-curve DER.
                if (publicKey == null || !key.Parameters.Equals(publicKey.Parameters)
                    || !publicKey.Q.Normalize().Equals(key.Parameters.G.Multiply(key.D).Normalize()))
                    throw new ServerOperationException("server_identity_keypair_mismatch");
                return credentials.PublicKeyFingerprint;
            }
            catch (ServerOperationException) { throw; }
            catch (Exception) { throw new ServerOperationException("server_identity_invalid"); }
        }

        /// <summary>Explicit editor action only. CreateNew is authoritative even if the path appears free beforehand.</summary>
        public static string GenerateIdentity(string path, string assetsPath)
        {
            ServerConfiguration.RequireOutsideAssets(path, assetsPath);
            try
            {
                if (File.Exists(path)) throw new ServerOperationException("server_identity_already_exists");
                EncryptionCredentials credentials = EncryptionCredentials.Generate();
                byte[] privateBytes = EncryptionCredentials.SerializePrivateKey(credentials.PrivateKey);
                try
                {
                    string json = JsonUtility.ToJson(new SerializedPair
                    {
                        PublicKeyFingerprint = credentials.PublicKeyFingerprint,
                        PublicKey = Convert.ToBase64String(credentials.PublicKeySerialized), PrivateKey = Convert.ToBase64String(privateBytes)
                    }, true);
                    // Parent directory must already exist and have administrator-controlled permissions.
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
                    return credentials.PublicKeyFingerprint;
                }
                finally { Array.Clear(privateBytes, 0, privateBytes.Length); }
            }
            catch (ServerOperationException) { throw; }
            catch (Exception) { throw new ServerOperationException("server_identity_create_failed"); }
        }
    }
}
