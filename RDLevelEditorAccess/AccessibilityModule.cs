using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RDLevelEditor;
using RDLevelEditorAccess.IPC;
using UnityEngine;

namespace RDLevelEditorAccess
{
    // ===================================================================================
    // 1. 公共入口 (API)
    // ===================================================================================
    public static class AccessibilityBridge
    {
        private static bool _isInitialized;
        private static FileIPC _fileIPC;

        public static void Initialize(GameObject host)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            _fileIPC = new FileIPC();
            // 传递 MonoBehaviour 组件用于启动协程
            var monoBehaviour = host.GetComponent<MonoBehaviour>();
            if (monoBehaviour == null)
            {
                Debug.LogError("[RDEditorAccess] host GameObject 没有 MonoBehaviour 组件");
                return;
            }
            _fileIPC.Initialize(monoBehaviour);

            Debug.Log("[RDEditorAccess] AccessibilityBridge 已初始化");
        }

        public static void Update()
        {
            _fileIPC?.Update();
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

            _fileIPC.StartEditing(levelEvent);
            
            //Narration.Say(string.Format(RDString.Get("eam.editor.openEventEditor"), levelEvent.type), NarrationCategory.Instruction);// 废话太多了。
        }

        public static void EditRow(int rowIndex)
        {
            if (!_isInitialized)
            {
                Debug.LogError("请先调用 AccessibilityBridge.Initialize() !");
                return;
            }

            if (rowIndex < 0) return;

            var editor = scnEditor.instance;
            if (editor == null || editor.rowsData == null || rowIndex >= editor.rowsData.Count)
            {
                Debug.LogWarning($"[RDEditorAccess] 无效的轨道索引: {rowIndex}");
                return;
            }

            var rowData = editor.rowsData[rowIndex];
            Debug.Log($"[RDEditorAccess] 打开轨道编辑器: 轨道 {rowIndex}, 角色 {rowData.character}");

            _fileIPC.StartRowEditing(rowData, rowIndex);
            
            //Narration.Say(string.Format(RDString.Get("eam.editor.openRowEditor"), rowIndex + 1), NarrationCategory.Instruction);// 废话太多了。
        }

        public static void Shutdown()
        {
            _fileIPC = null;
            _isInitialized = false;
        }

        public static void EditSettings()
        {
            if (!_isInitialized) return;
            _fileIPC.StartSettingsEditing();
            Narration.Say(RDString.Get("eam.editor.openSettingsEditor"), NarrationCategory.Instruction);
        }

        /// <summary>
        /// 打开跳转到位置对话框
        /// </summary>
        public static void JumpToCursor()
        {
            if (!_isInitialized)
            {
                Debug.LogError("[AccessibilityBridge] 未初始化");
                return;
            }
            _fileIPC.StartJumpToCursorEdit();
        }

        /// <summary>
        /// 启动事件链命名对话框
        /// </summary>
        public static void SaveEventChain()
        {
            if (!_isInitialized)
            {
                Debug.LogError("[AccessibilityBridge] 未初始化");
                return;
            }
            _fileIPC.StartChainNameEdit();
        }

        /// <summary>
        /// 打开 Helper 新建条件，targetEvent 为新建后自动附加的事件（可为 null）
        /// </summary>
        public static void CreateCondition(LevelEvent_Base targetEvent)
        {
            if (!_isInitialized) return;
            _fileIPC.StartConditionCreate(targetEvent);
        }

        /// <summary>
        /// 打开 Helper 编辑已有本地条件
        /// </summary>
        public static void EditCondition(int localId)
        {
            if (!_isInitialized) return;
            _fileIPC.StartConditionEdit(localId);
        }

        /// <summary>
        /// 是否正在等待 Helper 返回结果
        /// </summary>
        public static bool IsEditing => _fileIPC?.IsPolling == true;

        /// <summary>
        /// 注册条件新建/编辑完成后的回调
        /// </summary>
        public static void SetConditionalSavedCallback(Action<int> callback)
        {
            if (_fileIPC != null)
                _fileIPC.OnConditionalSaved = callback;
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
