using UnityEngine;
using UnityEngine.InputSystem;

namespace RPG4.Input
{
    /// <summary>全局输入宿主：独占持有输入资产实例并统一启用 / 释放映射，对外只提供动作集与指针可用性查询；未挂到场景时 Instance 为 null，消费方据此降级为无输入。</summary>
    [DefaultExecutionOrder(-1000)] // 早于界面组件的 OnEnable 就绪，保证 Instance 与动作集可取。
    public class InputManager : MonoBehaviour
    {
        private InputActions inputActions; // 输入资产的内存副本（由生成类的 FromJson 构造），映射的启用与释放都由本组件独占。

        /// <summary>背包映射的动作集（struct，重复访问不产生堆分配）；订阅方只增删回调，不启用 / 释放映射。</summary>
        public InputActions.BackpackActions backpackInput => inputActions.backpack;

        /// <summary>玩家动作集；映射生命周期由本宿主统一管理。</summary>
        public InputActions.PlayerActions playerInput => inputActions.player;

        /// <summary>宿主实例；场景里没有挂载时为 null。</summary>
        public static InputManager Instance { get; private set; }

        /// <summary>本机是否存在指针设备；设备口径只在输入层判定，消费方据此丢弃点击。</summary>
        public static bool HasPointer => Mouse.current != null;

        /// <summary>建立单例并常驻，随后构造输入资产并启用动作映射。</summary>
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            inputActions = new InputActions(); // 放在单例判定之后：被回收的重复实例不会构造出无人释放的资产副本。
            inputActions.backpack.Enable(); // 映射常驻启用，订阅方注册的 performed 才会回调。
            inputActions.player.Enable();
        }

        /// <summary>释放映射与资产副本；只在自身仍是当前单例时执行，避免重复实例误清 Instance。</summary>
        private void OnDestroy()
        {
            if (Instance != this) return;

            Instance = null;
            if (inputActions == null) return;

            inputActions.backpack.Disable();
            inputActions.player.Disable();
            inputActions.Dispose();
            inputActions = null;
        }
    }
}
