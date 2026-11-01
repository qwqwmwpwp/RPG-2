using RPG4.Networking.Contracts;
namespace RPG4.Frontend
{
    internal static class ClientFailureText
    {
        public static string Authentication(ClientAuthenticationResult result)
        {
            if (result.Failure == ClientAuthenticationFailure.ServerIdentityMismatch) return "服务器身份验证失败。";
            if (!result.Response.HasValue) return result.Failure == ClientAuthenticationFailure.AuthTimeout
                ? "账号验证超时，请重新登录。" : "安全登录未完成，请重新登录。[A01]";
            var response = result.Response.Value;
            switch ((LoginResultCode)response.code)
            {
                case LoginResultCode.InvalidCredentials: return "账号或密码错误。";
                case LoginResultCode.AccountDisabled: return "账号已停用，请联系管理员。";
                case LoginResultCode.AccountInUse: return "账号已在线，请先退出已有连接。";
                case LoginResultCode.VersionMismatch: return "客户端版本不兼容，请更新。";
                case LoginResultCode.ContentMismatch: return "游戏内容不一致，请更新客户端。";
                case LoginResultCode.ServerNotReady: return "服务器尚未就绪，请稍后重试。";
                case LoginResultCode.ServerFull: return "服务器已满，请稍后重试。";
                case LoginResultCode.RateLimited: return "操作过于频繁，请等待 " + response.retryAfterSeconds + " 秒后重试。";
                case LoginResultCode.AuthTimeout: return "账号验证超时，请重新登录。";
                case LoginResultCode.DatabaseUnavailable: return "登录服务暂不可用，请稍后重试。";
                case LoginResultCode.ProfileUnavailable: return "角色资料暂不可用，请联系管理员。";
                default: return "登录未完成，请检查客户端或联系管理员。[A02]";
            }
        }
        public static string Timeout(ClientSessionState state)
        {
            switch (state)
            {
                case ClientSessionState.Connecting: return "连接服务器超时，请检查地址与网络。";
                case ClientSessionState.Authenticating: return "账号验证超时，请重新登录。";
                case ClientSessionState.LoadingGameplay: return "场景加载超时，请重新登录。";
                default: return "角色同步超时，请重新登录。";
            }
        }
    }
}
