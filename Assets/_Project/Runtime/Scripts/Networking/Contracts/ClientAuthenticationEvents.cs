using Mirror;

namespace RPG4.Networking.Contracts
{
    public enum ClientAuthenticationFailure
    {
        None, InvalidConfiguration, Busy, InvalidInput, SecureConnectionRequired,
        ServerIdentityMismatch, InvalidResponse, ServerRejected, Cancelled, Disconnected, InternalError, AuthTimeout
    }

    public sealed class ClientAuthenticationContext
    {
        public long AttemptId { get; }
        public string RequestId { get; }
        public NetworkConnectionToServer Connection { get; }
        public ClientAuthenticationContext(long attemptId, string requestId, NetworkConnectionToServer connection)
        { AttemptId = attemptId; RequestId = requestId; Connection = connection; }
    }

    public sealed class ClientAuthenticationResult
    {
        public ClientAuthenticationContext Context { get; }
        public ClientAuthenticationFailure Failure { get; }
        public LoginResponseMessage? Response { get; }
        public bool Succeeded => Failure == ClientAuthenticationFailure.None && Response.HasValue
            && Response.Value.code == (ushort)LoginResultCode.Success;
        public ClientAuthenticationResult(ClientAuthenticationContext context, ClientAuthenticationFailure failure,
            LoginResponseMessage? response = null)
        { Context = context; Failure = failure; Response = response; }
    }
}
