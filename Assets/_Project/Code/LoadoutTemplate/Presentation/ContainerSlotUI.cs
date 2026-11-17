using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>保留已有蓝框的脚本身份，容器槽与其他装备槽共用相同显示和事件交互。</summary>
    [DisallowMultipleComponent]
    public sealed class ContainerSlotUI : LoadoutSlotUI
    {
        public ContainerUI Owner { get; private set; }

        /// <summary>没有独立本体物品的箱子或地面容器只隐藏本体框，不收缩布局。</summary>
        public void SetBodyVisible(bool visible) => SetPresentationVisible(visible);

        public void Bind(ContainerUI owner)
        {
            Owner = owner;
            if (owner == null) { BindEndpoint(null, null); return; }
        }
    }
}
