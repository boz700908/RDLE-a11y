using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RDEventEditorHelper
{
    public class PropertyData
    {
        public string name;
        public string displayName;
        public string value;
        public string type;
        public string[] options;
    }

    public class EditorForm : Form
    {
        private FlowLayoutPanel _panel;
        private Button _btnOK, _btnCancel, _btnApply;
        private string _eventType;
        private PropertyData[] _properties;
        private Dictionary<string, Control> _controls = new Dictionary<string, Control>();

        public event Action<Dictionary<string, string>> OnApply;
        public event Action<Dictionary<string, string>> OnOK;
        public event Action OnCancel;

        public EditorForm()
        {
            InitializeComponent();
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

            _panel = new FlowLayoutPanel();
            _panel.Dock = DockStyle.Top;
            _panel.Height = 520;
            _panel.AutoScroll = true;
            _panel.FlowDirection = FlowDirection.TopDown;
            _panel.WrapContents = false;
            _panel.Padding = new Padding(10);
            this.Controls.Add(_panel);

            var btnPanel = new FlowLayoutPanel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = 60;
            btnPanel.Padding = new Padding(10);

            _btnCancel = new Button { Text = "取消(&C)", Width = 100, Height = 35 };
            _btnApply = new Button { Text = "应用(&A)", Width = 100, Height = 35 };
            _btnOK = new Button { Text = "确定(&O)", Width = 100, Height = 35 };

            _btnOK.Click += (s, e) => OnOK?.Invoke(GetCurrentUpdates());
            _btnApply.Click += (s, e) => OnApply?.Invoke(GetCurrentUpdates());
            _btnCancel.Click += (s, e) => OnCancel?.Invoke();

            btnPanel.Controls.Add(_btnCancel);
            btnPanel.Controls.Add(_btnApply);
            btnPanel.Controls.Add(_btnOK);
            this.Controls.Add(btnPanel);

            this.CancelButton = _btnCancel;
            this.AcceptButton = _btnOK;

            this.FormClosing += (s, e) =>
            {
                e.Cancel = true;
                OnCancel?.Invoke();
            };
        }

        public void SetData(string eventType, PropertyData[] properties)
        {
            _eventType = eventType;
            _properties = properties;
            this.Text = $"编辑事件: {eventType}";
            BuildUI();
        }

        private void BuildUI()
        {
            _panel.Controls.Clear();
            _controls.Clear();

            if (_properties == null || _properties.Length == 0)
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
                    Text = prop.displayName ?? prop.name,
                    Width = 440,
                    Height = 55,
                    Padding = new Padding(5)
                };

                Control inputCtrl = null;

                switch (prop.type)
                {
                    case "Int":
                    case "Float":
                    case "String":
                        var txt = new TextBox
                        {
                            Text = prop.value ?? "",
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
                            Checked = prop.value == "true",
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
                        if (prop.options != null)
                            cmb.Items.AddRange(prop.options);
                        if (!string.IsNullOrEmpty(prop.value))
                            cmb.SelectedItem = prop.value;
                        else if (cmb.Items.Count > 0)
                            cmb.SelectedIndex = 0;
                        inputCtrl = cmb;
                        break;

                    default:
                        var lbl = new Label
                        {
                            Text = $"不支持的类型: {prop.type}",
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
                    _controls[prop.name] = inputCtrl;
                    _panel.Controls.Add(group);
                }
            }
        }

        private Dictionary<string, string> GetCurrentUpdates()
        {
            var updates = new Dictionary<string, string>();

            foreach (var kvp in _controls)
            {
                string propName = kvp.Key;
                Control ctrl = kvp.Value;
                string value = null;

                if (ctrl is TextBox txt)
                    value = txt.Text;
                else if (ctrl is CheckBox chk)
                    value = chk.Checked ? "true" : "false";
                else if (ctrl is ComboBox cmb)
                    value = cmb.SelectedItem?.ToString();

                if (value != null)
                    updates[propName] = value;
            }

            return updates;
        }
    }
}
