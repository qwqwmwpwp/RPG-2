using System;
using System.Runtime.InteropServices;
using System.Text;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Frontend
{
    /// <summary>One last successful login, stored in the current Windows user's credential set.</summary>
    internal static class RememberedLoginStore
    {
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_SERVER
        public static bool IsSupported => true;
#else
        public static bool IsSupported => false;
#endif
        private const string Target = "RPG4.Frontend.RememberedLogin.v1";
        private const uint GenericCredential = 1, PersistLocally = 2;
        private const int NotFound = 1168, MaxBlobBytes = 2560;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [Serializable]
        private sealed class SavedLogin
        {
            public int version = 1;
            public string serverId, fingerprint, username, password;
        }

        public static bool TryRead(TrustedClientServer server, out string username, out string password, out string reason)
        {
            username = password = reason = null;
            if (!IsSupported || server == null) return false;
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_SERVER
            IntPtr handle = IntPtr.Zero;
            Credential credential = default;
            byte[] bytes = null;
            try
            {
                if (!CredRead(Target, GenericCredential, 0, out handle))
                {
                    if (Marshal.GetLastWin32Error() != NotFound) reason = "无法读取已保存的密码，请手动输入。";
                    return false;
                }
                credential = Marshal.PtrToStructure<Credential>(handle);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                    || credential.CredentialBlobSize > MaxBlobBytes)
                { reason = "已保存的登录信息无效，请重新输入。"; return false; }
                bytes = new byte[(int)credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var saved = JsonUtility.FromJson<SavedLogin>(Utf8.GetString(bytes));
                if (saved == null || saved.version != 1 || !EntryProtocol.IsUsername(saved.username)
                    || !EntryProtocol.IsUtf8(saved.password, 1, EntryProtocol.MaxPasswordBytes))
                { reason = "已保存的登录信息无效，请重新输入。"; return false; }
                if (saved.serverId != server.ServerId || saved.fingerprint != server.PublicKeyFingerprint) return false;
                username = saved.username; password = saved.password;
                return true;
            }
            catch (Exception) { reason = "无法读取已保存的密码，请手动输入。"; return false; }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (handle != IntPtr.Zero)
                {
                    if (credential.CredentialBlobSize <= MaxBlobBytes)
                        ClearBlob(credential.CredentialBlob, (int)credential.CredentialBlobSize);
                    CredFree(handle);
                }
            }
#else
            return false;
#endif
        }

        public static bool TrySave(string serverId, string fingerprint, string username, string password, out string reason)
        {
            reason = "密码未保存，请下次手动输入。";
            if (!IsSupported || !EntryProtocol.IsAscii(serverId, EntryProtocol.MaxServerIdBytes)
                || !EntryProtocol.IsLowerHex(fingerprint, EntryProtocol.HashLength) || !EntryProtocol.IsUsername(username)
                || !EntryProtocol.IsUtf8(password, 1, EntryProtocol.MaxPasswordBytes)) return false;
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_SERVER
            byte[] bytes = null;
            IntPtr blob = IntPtr.Zero;
            try
            {
                bytes = Utf8.GetBytes(JsonUtility.ToJson(new SavedLogin
                { serverId = serverId, fingerprint = fingerprint, username = username, password = password }));
                if (bytes.Length > MaxBlobBytes) return false;
                blob = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential { Type = GenericCredential, TargetName = Target, UserName = username,
                    Persist = PersistLocally, CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length };
                if (!CredWrite(ref credential, 0)) return false;
                reason = null; return true;
            }
            catch (Exception) { return false; }
            finally
            {
                if (blob != IntPtr.Zero) { ClearBlob(blob, bytes.Length); Marshal.FreeHGlobal(blob); }
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            }
#else
            return false;
#endif
        }

        public static bool TryDelete(out string reason)
        {
            reason = "未能清除已保存的密码，请重试。";
            if (!IsSupported) return false;
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_SERVER
            try
            {
                if (!CredDelete(Target, GenericCredential, 0) && Marshal.GetLastWin32Error() != NotFound) return false;
                reason = null; return true;
            }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }

#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_SERVER
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags, Type;
            public string TargetName, Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias, UserName;
        }

        private static void ClearBlob(IntPtr blob, int length)
        {
            if (blob == IntPtr.Zero) return;
            for (int i = 0; i < length; i++) Marshal.WriteByte(blob, i, 0);
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("Advapi32.dll", ExactSpelling = true)]
        private static extern void CredFree(IntPtr credential);
#endif
    }
}
