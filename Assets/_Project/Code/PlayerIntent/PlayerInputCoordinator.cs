using System;
using UnityEngine;

namespace RPG4.PlayerIntent
{
    /// <summary>设备只向绑定的角色提交采样及许可撤销。</summary>
    public interface IPlayerInputSink
    {
        void SubmitLocalInput(PlayerInputFrame frame);
        void ClearInput();
        void ClearWeaponInput();
    }

    /// <summary>角色输入的本机分发端；即时控制许可独立于周期快照。</summary>
    public interface IPlayerInputEvents
    {
        bool LocalControlAllowed { get; }
        bool ConsumeInteractionPressed();
        void ReportInteraction(MonoBehaviour target, bool visible = false, string prompt = null);
        void ReportInteractionFeedback(string message, bool success);
    }

    /// <summary>每角色拥有缓冲与消费游标；同一 render 帧内合并多次逻辑发布，过帧自动失效。</summary>
    internal sealed class PlayerInputCoordinator : IPlayerInputSink, IPlayerInputEvents
    {
        internal PlayerInputBuffer Buffer { get; } = new PlayerInputBuffer();
        private readonly IPlayerReadState readState;
        private readonly Func<bool> localControlAllowed;
        private readonly Action<MonoBehaviour, bool, string> reportInteraction;
        private readonly Action<string, bool> reportFeedback;
        private ulong observedVersion;
        private int renderFrame = -1;
        private bool interactionPending, quickUsePending;
        private float zoom;
        internal PlayerInputCoordinator(IPlayerReadState readState, Func<bool> localControlAllowed,
            Action<MonoBehaviour, bool, string> reportInteraction, Action<string, bool> reportFeedback)
        {
            this.readState = readState; this.localControlAllowed = localControlAllowed;
            this.reportInteraction = reportInteraction; this.reportFeedback = reportFeedback;
        }
        public bool LocalControlAllowed => localControlAllowed();
        private bool CanConsume => renderFrame == Time.frameCount && LocalControlAllowed
            && readState.Input.Snapshot.InputAllowed;
        public void SubmitLocalInput(PlayerInputFrame frame)
        { if (LocalControlAllowed) Buffer.Accumulate(frame); }
        internal void Observe(PlayerInputSnapshot snapshot)
        {
            if (snapshot.Version == observedVersion) return;
            observedVersion = snapshot.Version;
            if (!LocalControlAllowed || !snapshot.InputAllowed) { ClearPending(); return; }
            if (renderFrame != Time.frameCount) ClearPending();
            renderFrame = Time.frameCount;
            zoom += snapshot.Frame.Zoom;
            interactionPending |= snapshot.Frame.InteractPressed;
            quickUsePending |= snapshot.Frame.QuickUsePressed;
        }
        public bool ConsumeInteractionPressed()
        {
            bool pressed = CanConsume && interactionPending && readState.Interaction.CanInteract;
            interactionPending = false;
            return pressed;
        }
        internal bool ConsumeQuickUsePressed()
        {
            bool pressed = CanConsume && quickUsePending;
            quickUsePending = false;
            return pressed;
        }
        internal float ConsumePresentationZoom()
        {
            float result = CanConsume ? zoom : 0f;
            zoom = 0f;
            return result;
        }
        private void ClearPending()
        { renderFrame = -1; interactionPending = quickUsePending = false; zoom = 0f; }
        public void ClearInput() { Buffer.Clear(); ClearPending(); }
        public void ClearWeaponInput() => Buffer.ClearWeaponInput();
        public void ReportInteraction(MonoBehaviour target, bool visible = false, string prompt = null)
        { if (LocalControlAllowed || target == null) reportInteraction(target, visible, prompt); }
        public void ReportInteractionFeedback(string message, bool success)
        { if (LocalControlAllowed) reportFeedback(message, success); }
    }
}
