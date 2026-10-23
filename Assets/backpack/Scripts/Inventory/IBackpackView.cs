using System.Collections.Generic;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>UI 数据源契约：表现层只依赖本接口，单机与网络两套宿主各实现一份，切换部署形态时界面代码零改动；物品读取经 <see cref="CopyItems"/>，变更通知与写入意图走事件管线的 <see cref="EventSource"/> 键。</summary>
    public interface IBackpackView
    {
        /// <summary>权威数据可用；未就绪时不得推测布局或提交写入。</summary>
        bool IsReady { get; }

        int Rows { get; }
        int Columns { get; }

        /// <summary>按持久标识读取权威放置记录。</summary>
        bool TryGetPlacement(ItemId itemId, out InventoryPlacement placement);

        /// <summary>面板配置的行数（本地配置，不代表权威容器的实际行数）。</summary>
        int ConfiguredRows { get; }

        /// <summary>面板配置的列数（本地配置，不代表权威容器的实际列数）。</summary>
        int ConfiguredColumns { get; }

        /// <summary>事件管线键：订阅 <c>InvChannels</c> 通知与发布 <c>CmdChannels</c> 意图都用它作 source。单机与主机模式为权威容器实例，纯客户端为宿主自身（客户端没有容器）。</summary>
        object EventSource { get; }

        /// <summary>按 ItemId 升序复制当前物品（先清空目标列表）；数据源未就绪时清空并返回。</summary>
        void CopyItems(List<RuntimeItem> destination);
    }
}
