using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RDEventEditorHelper.IPC;

namespace RDEventEditorHelper
{
    // ===================================================================================
    // 属性编辑器窗口
    // ===================================================================================
    public class EditorForm : Form
    {
        private FlowLayoutPanel _panel;
        private Button _btnOk, _btnCancel, _btnApply;
        private string _currentEventType;
        private List<PropertyData> _properties;
        private Dictionary<string, Control> _controls = new Dictionary<string, Control>();

        public event Action<Dictionary<string, object>> OnApplyChanges;
        public event Action OnCloseRequested;

        public EditorForm()
        {
            InitializeComponent();
            this.Visible = false; // 初始隐藏
        }

        private void InitializeComponent()
        {
            this.Text = "事件属性编辑器";
            this.Size = new Size(500, 650);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = true;
            this.TopMost = true;

            // 布局容器
            _panel = new FlowLayoutPanel();
            _panel.Dock = DockStyle.Top;
            _panel.Height = 520;
            _panel.AutoScroll = true;
            _panel.FlowDirection = FlowDirection.TopDown;
            _panel.WrapContents = false;
            _panel.Padding = new Padding(10);
            this.Controls.Add(_panel);

            // 按钮面板
            var btnPanel = new FlowLayoutPanel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = 60;
            btnPanel.Padding = new Padding(10);

            _btnCancel = new Button { Text = "取消(&C)", Width = 100, Height = 35 };
            _btnApply = new Button { Text = "应用(&A)", Width = 100, Height = 35 };
            _btnOk = new Button { Text = "确定(&O)", Width = 100, Height = 35 };

            _btnOk.Click += (s, e) => { ApplyChanges(); HideEditor(); };
            _btnApply.Click += (s, e) => ApplyChanges();
            _btnCancel.Click += (s, e) => HideEditor();

            btnPanel.Controls.Add(_btnCancel);
            btnPanel.Controls.Add(_btnApply);
            btnPanel.Controls.Add(_btnOk);
            this.Controls.Add(btnPanel);

            // ESC 关闭
            this.CancelButton = _btnCancel;
            this.AcceptButton = _btnOk;

            // 关闭时隐藏而非销毁
            this.FormClosing += (s, e) =>
            {
                e.Cancel = true;
                HideEditor();
            };
        }

        public void ShowEditor(string eventType, List<PropertyData> properties)
        {
            _currentEventType = eventType;
            _properties = properties;
            BuildUI();

            this.Text = $"编辑事件: {eventType}";
            this.Visible = true;
            this.BringToFront();
            this.Activate();

            // 聚焦第一个控件
            if (_panel.Controls.Count > 0)
                _panel.Controls[0].Focus();
        }

        public void HideEditor()
        {
            this.Visible = false;
            _controls.Clear();
            OnCloseRequested?.Invoke();
        }

        private void BuildUI()
        {
            _panel.Controls.Clear();
            _controls.Clear();

            if (_properties == null || _properties.Count == 0)
            {
                var lbl = new Label
                {
                    Text = "该事件没有可编辑的属性",
                    AutoSize = true,
                    Padding = new Padding(10)
                };
                _panel.Controls.Add(lbl);
                return;
            }

            foreach (var prop in _properties)
            {
                var group = new GroupBox
                {
                    Text = prop.DisplayName ?? prop.Name,
                    Width = 440,
                    Height = 55,
                    Padding = new Padding(5)
                };

                Control inputCtrl = null;

                switch (prop.Type)
                {
                    case "Int":
                    case "Float":
                    case "String":
                        var txt = new TextBox
                        {
                            Text = prop.Value?.ToString(),
                            Width = 400,
                            Top = 20,
                            Left = 10
                        };
                        inputCtrl = txt;
                        break;

                    case "Bool":
                        var chk = new CheckBox
                        {
                            Text = "启用",
                            Checked = prop.Value is bool b && b,
                            Top = 20,
                            Left = 10,
                            AutoSize = true
                        };
                        inputCtrl = chk;
                        break;

                    case "Enum":
                        var cmb = new ComboBox
                        {
                            Width = 400,
                            Top = 20,
                            Left = 10,
                            DropDownStyle = ComboBoxStyle.DropDownList
                        };
                        if (prop.Options != null)
                            cmb.Items.AddRange(prop.Options);
                        if (prop.Value != null)
                            cmb.SelectedItem = prop.Value.ToString();
                        else if (cmb.Items.Count > 0)
                            cmb.SelectedIndex = 0;
                        inputCtrl = cmb;
                        break;

                    default:
                        var lbl = new Label
                        {
                            Text = $"不支持的类型: {prop.Type}",
                            Width = 400,
                            Top = 20,
                            Left = 10
                        };
                        inputCtrl = lbl;
                        break;
                }

                if (inputCtrl != null)
                {
                    group.Controls.Add(inputCtrl);
                    _controls[prop.Name] = inputCtrl;
                    _panel.Controls.Add(group);
                }
            }
        }

        private void ApplyChanges()
        {
            var updates = new Dictionary<string, object>();

            foreach (var kvp in _controls)
            {
                string propName = kvp.Key;
                Control ctrl = kvp.Value;

                object value = null;

                if (ctrl is TextBox txt)
                    value = txt.Text;
                else if (ctrl is CheckBox chk)
                    value = chk.Checked;
                else if (ctrl is ComboBox cmb)
                    value = cmb.SelectedItem?.ToString();

                if (value != null)
                    updates[propName] = value;
            }

            if (updates.Count > 0)
            {
                OnApplyChanges?.Invoke(updates);
                Console.WriteLine($"[EditorForm] 已应用 {updates.Count} 个更改");
            }
        }
    }
}
