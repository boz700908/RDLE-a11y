using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RDLevelEditor;
using UnityEngine;

namespace RDLevelEditorAccess
{
    // ===================================================================================
    // 1. 公共入口 (API)
    // ===================================================================================
    public static class AccessibilityBridge
    {
        private static bool _isInitialized;

        public static void Initialize(GameObject host)
        {
            if (_isInitialized) return;
            _isInitialized = true;
            Debug.Log("[RDEditorAccess] AccessibilityBridge 已初始化");
        }

        public static void EditEvent(LevelEvent_Base levelEvent)
        {
            if (!_isInitialized)
            {
                Debug.LogError("请先调用 AccessibilityBridge.Initialize() !");
                return;
            }

            if (levelEvent == null) return;

            Debug.Log($"[RDEditorAccess] 打开事件编辑器: {levelEvent.type}");
            Narration.Say("事件编辑器功能暂未启用", NarrationCategory.Notification);
        }
    }

    // ===================================================================================
    // 2. 调度器 (Unity Main Thread Dispatcher) - 保留用于将来扩展
    // ===================================================================================
    public class UnityDispatcher : MonoBehaviour
    {
        public static UnityDispatcher Instance;
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Update()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError($"[Dispatcher] 更新异常: {e}"); }
            }
        }

        public void Enqueue(Action action) => _queue.Enqueue(action);
    }
}
