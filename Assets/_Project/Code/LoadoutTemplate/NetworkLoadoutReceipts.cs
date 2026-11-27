using System.Collections.Generic;

namespace RPG4.LoadoutTemplate
{
    /// <summary>管理一个网络请求编号域，保留可重放的原始提交和已受理的最高编号。</summary>
    internal sealed class NetworkLoadoutReceipts
    {
        private const int Capacity = 128;
        private readonly Dictionary<int, Receipt> entries = new Dictionary<int, Receipt>();
        private int highestRequest;

        /// <summary>保存与原请求逐字比较所需的令牌和报文，通道与原提交由结果持有。</summary>
        private readonly struct Receipt
        {
            internal readonly string Token, Json;
            internal readonly LoadoutOperationOutcome Outcome;

            /// <summary>记录一次已处理请求的输入和权威结果。</summary>
            internal Receipt(string token, string json, LoadoutOperationOutcome outcome)
            { Token = token; Json = json; Outcome = outcome; }
        }

        /// <summary>预占新编号；重复或过期时返回使用当前版本的结果，并标明是否重放了原回执。</summary>
        internal bool TryBegin(LoadoutOperationKey key, string token, string json, LoadoutStateVersion current,
            out LoadoutOperationOutcome outcome, out bool replayed)
        {
            replayed = false;
            if (entries.TryGetValue(key.RequestId, out Receipt receipt))
            {
                LoadoutOperationOutcome original = receipt.Outcome;
                replayed = original.Key.Channel == key.Channel && receipt.Token == token && receipt.Json == json;
                outcome = replayed
                    ? new LoadoutOperationOutcome(key, original.Code, original.Message, original.Commit, current)
                    : new LoadoutOperationOutcome(key, LoadoutResultCode.DuplicateRequestConflict,
                        "重复请求编号对应不同操作。", null, current);
                return false;
            }
            if (key.RequestId <= highestRequest)
            {
                string message = key.Channel == LoadoutOperationChannel.Runtime
                    ? "旧武器请求回执已过期，请重新同步。" : "请求回执已过期，请重新同步。";
                outcome = new LoadoutOperationOutcome(key, LoadoutResultCode.RequestExpired, message, null, current);
                return false;
            }
            highestRequest = key.RequestId;
            outcome = null;
            return true;
        }

        /// <summary>在对外发布结果前登记回执，满表时整体释放旧条目并保留编号高水位。</summary>
        internal void Record(string token, string json, LoadoutOperationOutcome outcome)
        {
            if (entries.Count >= Capacity) entries.Clear();
            entries[outcome.Key.RequestId] = new Receipt(token, json, outcome);
        }

        /// <summary>清除可重放内容，仍拒绝已受理过的编号。</summary>
        internal void DiscardReceipts() => entries.Clear();

        /// <summary>切换会话或世代时重新开始编号。</summary>
        internal void Reset()
        {
            entries.Clear();
            highestRequest = 0;
        }
    }
}
