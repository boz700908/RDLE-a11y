using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using RDLevelEditor;
using UnityEngine;
using Application = System.Windows.Forms.Application;
using Button = System.Windows.Forms.Button;
using CheckBox = System.Windows.Forms.CheckBox;
using ComboBox = System.Windows.Forms.ComboBox;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Label = System.Windows.Forms.Label;
using TextBox = System.Windows.Forms.TextBox;

namespace RDLevelEditorAccess
{
    // ===================================================================================
    // 1. 公共入口 (API)
    // ===================================================================================
    public static class AccessibilityBridge
    {
        private static Thread _formThread;
        private static EditorForm _activeForm;
        private static bool _isInitialized;

        /// <summary>
        /// 初始化桥接器（请在 Plugin.Awake 中调用）
        /// </summary>
        public static void Initialize(GameObject host)
        {
            if (_isInitialized) return;

            // 挂载主线程调度器
            if (host.GetComponent<UnityDispatcher>() == null)
                host.AddComponent<UnityDispatcher>();

            // 启动 WinForm 线程
            _formThread = new Thread(WinFormLoop);
            _formThread.SetApartmentState(ApartmentState.STA);
            _formThread.IsBackground = true;
            _formThread.Start();

            _isInitialized = true;
        }

        /// <summary>
        /// 核心方法：打开当前选中事件的属性编辑器
        /// </summary>
        public static void EditEvent(LevelEvent_Base levelEvent)
        {
            if (!_isInitialized)
            {
                Debug.LogError("请先调用 AccessibilityBridge.Initialize() !");
                return;
            }

            if (levelEvent == null) return;

            // 1. 在 Unity 线程提取数据
            var dtos = PropertyExtractor.Extract(levelEvent);
            string title = $"编辑事件: {levelEvent.type}";

            // 2. 发送到 WinForm 线程显示
            if (_activeForm != null && _activeForm.InvokeRequired)
            {
                _activeForm.Invoke(new Action(() => _activeForm.ShowEditor(title, levelEvent, dtos)));
            }
        }

        private static void WinFormLoop()
        {
            Application.EnableVisualStyles();
            _activeForm = new EditorForm();
            Application.Run(_activeForm);
        }
    }

    // ===================================================================================
    // 2. 调度器 (Unity Main Thread Dispatcher)
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
                catch (Exception e) { Debug.LogError($"[WinForm] 更新异常: {e}"); }
            }
        }

        public void Enqueue(Action action) => _queue.Enqueue(action);
    }

    // ===================================================================================
    // 3. 数据传输对象 (DTO) - 用于跨线程
    // ===================================================================================
    public class PropertyDTO
    {
        public string DisplayName;   // 显示给用户的名字 (Bar, Beat)
        public string PropertyName;  // 代码里的变量名 (bar, beat)
        public object Value;         // 当前值
        public string Type;          // 类型标识 (Int, Float, Enum...)
        public string[] EnumOptions; // 枚举选项
        public bool IsReadOnly;      // 是否只读
    }

    // ===================================================================================
    // 4. 数据提取器 (Extractor) - 运行在 Unity 线程
    // ===================================================================================
    public static class PropertyExtractor
    {
        public static List<PropertyDTO> Extract(LevelEvent_Base ev)
        {
            var list = new List<PropertyDTO>();

            // 获取元数据
            // 如果事件没有缓存 info，临时生成一个
            LevelEventInfo info = ev.info ?? new LevelEventInfo(ev.GetType());

            foreach (var prop in info.propertiesInfo)
            {
                //
                // 过滤掉不可用的属性
                if (prop.enableIf != null && !prop.enableIf(ev)) continue;

                var dto = new PropertyDTO
                {
                    DisplayName = prop.name, // 本地化名称
                    PropertyName = prop.propertyInfo.Name,
                    Value = prop.propertyInfo.GetValue(ev),
                    IsReadOnly = false
                };

                // 类型映射逻辑
                if (prop is IntPropertyInfo) dto.Type = "Int";
                else if (prop is FloatPropertyInfo) dto.Type = "Float";
                else if (prop is BoolPropertyInfo) dto.Type = "Bool";
                else if (prop is StringPropertyInfo) dto.Type = "String";
                else if (prop is EnumPropertyInfo enumProp)
                {
                    dto.Type = "Enum";
                    dto.EnumOptions = Enum.GetNames(enumProp.enumType);
                }
                else if (prop is ColorPropertyInfo) dto.Type = "Color";
                else if (prop is Vector2PropertyInfo) dto.Type = "Vector2";
                // 更多类型可在此扩展...
                else dto.Type = "String"; // 默认回退

                list.Add(dto);
            }
            return list;
        }
    }

    // ===================================================================================
    // 5. WinForm 界面实现 (The Dialog)
    // ===================================================================================
    public class EditorForm : Form
    {
        private FlowLayoutPanel _panel;
        private Button _btnOk, _btnCancel, _btnApply;
        private LevelEvent_Base _targetEvent; // 仅用于回传标识，不直接访问
        private Dictionary<string, Func<object>> _valueGetters = new Dictionary<string, Func<object>>();

        public EditorForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "属性编辑器";
            this.Size = new Size(450, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = false;
            this.TopMost = true; // 保持在游戏上层

            // 隐藏窗口，直到 explicit Show
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visible = false;

            // 布局容器
            _panel = new FlowLayoutPanel();
            _panel.Dock = DockStyle.Top;
            _panel.Height = 500;
            _panel.AutoScroll = true;
            _panel.FlowDirection = FlowDirection.TopDown;
            _panel.WrapContents = false;
            this.Controls.Add(_panel);

            // 按钮面板
            var btnPanel = new FlowLayoutPanel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = 50;
            btnPanel.FlowDirection = FlowDirection.RightToLeft;

            _btnCancel = new Button { Text = "取消(&C)", DialogResult = DialogResult.Cancel, Width = 80 };
            _btnApply = new Button { Text = "应用(&A)", Width = 80 };
            _btnOk = new Button { Text = "确定(&O)", DialogResult = DialogResult.OK, Width = 80 };

            _btnOk.Click += (s, e) => { ApplyChanges(); HideEditor(); };
            _btnApply.Click += (s, e) => ApplyChanges();
            _btnCancel.Click += (s, e) => HideEditor();

            btnPanel.Controls.Add(_btnCancel);
            btnPanel.Controls.Add(_btnApply);
            btnPanel.Controls.Add(_btnOk);
            this.Controls.Add(btnPanel);

            // 绑定 ESC 关闭
            this.CancelButton = _btnCancel;
            this.AcceptButton = _btnOk;

            // 关闭时不销毁，而是隐藏
            this.FormClosing += (s, e) =>
            {
                e.Cancel = true;
                HideEditor();
            };
        }

        // 公开方法：由 Unity 线程通过 Invoke 调用
        public void ShowEditor(string title, LevelEvent_Base target, List<PropertyDTO> props)
        {
            this.Text = title;
            _targetEvent = target;
            BuildUI(props);

            this.WindowState = FormWindowState.Normal;
            this.Visible = true;
            this.BringToFront();
            this.Activate(); // 聚焦，让读屏软件读标题

            // 默认聚焦第一个控件
            if (_panel.Controls.Count > 0)
                _panel.Controls[0].Focus();
        }

        private void HideEditor()
        {
            this.Visible = false;
            _targetEvent = null;
        }

        private void BuildUI(List<PropertyDTO> props)
        {
            _panel.Controls.Clear();
            _valueGetters.Clear();

            foreach (var prop in props)
            {
                var group = new System.Windows.Forms.GroupBox();
                group.Text = prop.DisplayName;
                group.Size = new Size(400, 60);
                group.Padding = new Padding(5);

                Control inputCtrl = null;

                switch (prop.Type)
                {
                    case "Int":
                    case "Float":
                    case "String":
                        var txt = new TextBox { Text = prop.Value?.ToString(), Width = 380, Top = 20, Left = 10 };
                        inputCtrl = txt;
                        // 定义如何获取值
                        _valueGetters[prop.PropertyName] = () => txt.Text;
                        break;

                    case "Bool":
                        var chk = new CheckBox { Text = "启用", Checked = (bool)prop.Value, Top = 20, Left = 10, AutoSize = true };
                        inputCtrl = chk;
                        _valueGetters[prop.PropertyName] = () => chk.Checked;
                        break;

                    case "Enum":
                        var cmb = new ComboBox { Width = 380, Top = 20, Left = 10, DropDownStyle = ComboBoxStyle.DropDownList };
                        cmb.Items.AddRange(prop.EnumOptions);
                        cmb.SelectedItem = prop.Value?.ToString();
                        inputCtrl = cmb;
                        _valueGetters[prop.PropertyName] = () => cmb.SelectedItem;
                        break;

                    case "Color":
                        var btnColor = new Button { Text = $"选择颜色 ({prop.Value})", Width = 380, Top = 20, Left = 10 };
                        string hexValue = prop.Value?.ToString(); // 保存当前的 Hex
                        btnColor.Click += (s, e) => {
                            // 简单的颜色选择逻辑，此处略，可弹出 ColorDialog
                        };
                        var txtColor = new TextBox { Text = hexValue, Visible = false }; // 隐式存储
                        inputCtrl = btnColor;
                        _valueGetters[prop.PropertyName] = () => hexValue; // 暂时只回传旧值，你需要对接 ColorDialog
                        break;
                }

                if (inputCtrl != null)
                {
                    group.Controls.Add(inputCtrl);
                    _panel.Controls.Add(group);
                }
            }
        }

        private void ApplyChanges()
        {
            if (_targetEvent == null) return;

            // 收集所有修改
            var updates = new Dictionary<string, object>();
            foreach (var kvp in _valueGetters)
            {
                updates[kvp.Key] = kvp.Value();
            }

            // 发送回 Unity
            UnityDispatcher.Instance.Enqueue(() =>
            {
                if (scnEditor.instance == null) return;

                // 再次确认对象还存在（防止删除后点击应用）
                // 这里我们做一个简化的假设：_targetEvent 引用仍然有效。
                // 实际上应该通过 ID 查找更安全，但对于 LevelEvent_Base 引用通常足够。

                ApplyToEvent(_targetEvent, updates);
            });
        }

        // --- 运行在 Unity 主线程 ---
        private void ApplyToEvent(LevelEvent_Base ev, Dictionary<string, object> updates)
        {
            // 重新获取 info 以确保类型安全
            var info = ev.info ?? new LevelEventInfo(ev.GetType());

            foreach (var update in updates)
            {
                var propInfo = info.propertiesInfo.FirstOrDefault(p => p.propertyInfo.Name == update.Key);
                if (propInfo != null)
                {
                    try
                    {
                        object valToSet = null;
                        string strVal = update.Value.ToString();

                        // 类型转换
                        if (propInfo is IntPropertyInfo) valToSet = int.Parse(strVal);
                        else if (propInfo is FloatPropertyInfo) valToSet = float.Parse(strVal);
                        else if (propInfo is BoolPropertyInfo) valToSet = (bool)update.Value;
                        else if (propInfo is StringPropertyInfo) valToSet = strVal;
                        else if (propInfo is EnumPropertyInfo enumProp) valToSet = Enum.Parse(enumProp.enumType, strVal);

                        if (valToSet != null)
                        {
                            propInfo.propertyInfo.SetValue(ev, valToSet);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"属性 {update.Key} 转换失败: {ex.Message}");
                    }
                }
            }

            // 刷新编辑器 UI
            if (scnEditor.instance.selectedControl != null && scnEditor.instance.selectedControl.levelEvent == ev)
            {
                scnEditor.instance.selectedControl.UpdateUI();
                scnEditor.instance.inspectorPanelManager.GetCurrent()?.UpdateUI(ev);
            }
            Debug.Log("属性已更新");
        }
    }
}