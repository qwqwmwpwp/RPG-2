using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RPG4.Networking.Contracts;

namespace RPG4.Server
{
    public enum PasswordVerification { Invalid, Valid, NeedsRehash }

    public sealed class PasswordRecord
    {
        public string Algorithm;
        public int ParameterVersion, Iterations;
        public long CredentialVersion;
        public byte[] Salt, Hash;
    }

    public sealed class KdfQueueFullException : Exception { public KdfQueueFullException() : base("Password work queue is full.") { } }

    /// <summary>Running KDF work retains its permits until the CPU operation has actually completed.</summary>
    public sealed class PasswordHasher
    {
        public const string Algorithm = "pbkdf2-sha256";
        public const int CurrentParameterVersion = 2, CurrentIterations = 600000;
        public const int MaxIterations = 2000000;
        public const int SaltBytes = 16, HashBytes = 32;
        private readonly SemaphoreSlim admission, workers;
        private readonly int iterations;
        private int pending;
        public int PendingCount => Volatile.Read(ref pending);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public PasswordHasher(int workerCount, int queueCapacity, int iterations)
        {
            if (workerCount < 1 || queueCapacity < 0 || iterations < CurrentIterations || iterations > MaxIterations)
                throw new ArgumentOutOfRangeException(nameof(workerCount));
            admission = new SemaphoreSlim(workerCount + queueCapacity, workerCount + queueCapacity);
            workers = new SemaphoreSlim(workerCount, workerCount); this.iterations = iterations;
        }
        public static bool IsSupported(PasswordRecord record) => record != null && record.Algorithm == Algorithm
            && (record.ParameterVersion == 1 || record.ParameterVersion == CurrentParameterVersion)
            && record.Iterations >= CurrentIterations
            && record.Iterations <= MaxIterations && record.CredentialVersion >= 1 && record.CredentialVersion < long.MaxValue
            && record.Salt != null && record.Salt.Length == SaltBytes && record.Hash != null && record.Hash.Length == HashBytes;

        public Task<PasswordRecord> HashAsync(string password, long credentialVersion, CancellationToken token,
            int minimumIterations = CurrentIterations)
        {
            if (!EntryProtocol.IsUtf8(password, 1, EntryProtocol.MaxPasswordBytes) || credentialVersion < 1 || credentialVersion >= long.MaxValue
                || minimumIterations < CurrentIterations || minimumIterations > MaxIterations)
                throw new ArgumentException("Invalid new password parameters.");
            // A parameter-version upgrade must not lower an existing credential's work factor.
            int workIterations = Math.Max(iterations, minimumIterations);
            return RunAsync(() =>
            {
                byte[] salt = new byte[SaltBytes];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(salt);
                return new PasswordRecord { Algorithm = Algorithm, ParameterVersion = CurrentParameterVersion,
                    Iterations = workIterations, CredentialVersion = credentialVersion, Salt = salt, Hash = Derive(password, salt, workIterations) };
            }, token);
        }

        public Task<PasswordVerification> VerifyAsync(string password, PasswordRecord record, CancellationToken token)
        {
            if (!IsSupported(record)) throw new ArgumentException("Unsupported stored password parameters.");
            if (!EntryProtocol.IsUtf8(password, 1, EntryProtocol.MaxPasswordBytes)) throw new ArgumentException("Invalid password.");
            return RunAsync(() =>
            {
                byte[] actual = Derive(password, record.Salt, record.Iterations);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, record.Hash)) return PasswordVerification.Invalid;
                    return record.ParameterVersion != CurrentParameterVersion || record.Iterations < iterations
                        ? PasswordVerification.NeedsRehash : PasswordVerification.Valid;
                }
                finally { Array.Clear(actual, 0, actual.Length); }
            }, token);
        }

        private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!admission.Wait(0)) throw new KdfQueueFullException();
            Interlocked.Increment(ref pending);
            bool acquired = false;
            try
            {
                await workers.WaitAsync(token).ConfigureAwait(false); acquired = true;
                token.ThrowIfCancellationRequested();
                // Cancellation cannot make an executing PBKDF2 stop. Do not pass it to Task.Run or release early.
                T value = await Task.Run(operation).ConfigureAwait(false);
                if (token.IsCancellationRequested && value is PasswordRecord cancelled)
                    Array.Clear(cancelled.Hash, 0, cancelled.Hash.Length);
                token.ThrowIfCancellationRequested();
                return value;
            }
            finally { if (acquired) workers.Release(); admission.Release(); Interlocked.Decrement(ref pending); }
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            byte[] bytes = Utf8.GetBytes(password);
            try { using (var kdf = new Rfc2898DeriveBytes(bytes, salt, iterations, HashAlgorithmName.SHA256)) return kdf.GetBytes(HashBytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
    }
}
