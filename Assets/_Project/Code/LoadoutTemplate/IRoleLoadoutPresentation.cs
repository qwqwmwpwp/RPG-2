using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>网络拥有者的展示边界，只接收只读投影和会话状态。</summary>
    public interface IRoleLoadoutPresentation
    {
        bool IsWindowOpen { get; }
        bool IsLootOpen { get; }
        event Action WindowChanged;
        event Action LootClosed;
        void Initialize(Transform actor, Transform authority);
        bool BeginSession(string ownerKey, out string error);
        void EndSession();
        void SetInputAllowed(bool allowed);
        bool OwnsSession(LoadoutWindowSession session);
        bool CanOpenLoot(out string error);
        bool ShowLoot(IReadOnlyList<LootSectionSnapshot> sections, out string error);
        void CloseLoot();
    }

    public sealed class LootSectionSnapshot
    {
        public LootSectionSnapshot(LootSectionKind kind, ContainerSnapshot grid, ContainerSnapshot body = null)
        {
            Kind = kind;
            Grid = ContainerDisplaySnapshot.Capture(grid);
            Body = ContainerDisplaySnapshot.Capture(body);
        }

        public LootSectionKind Kind { get; }
        public ContainerDisplaySnapshot Grid { get; }
        public ContainerDisplaySnapshot Body { get; }
    }
}
