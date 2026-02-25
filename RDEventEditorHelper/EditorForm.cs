using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        public string methodName;  // Button 类型专用：要调用的方法名
        public bool itsASong;      // SoundData 类型专用：区分歌曲/音效
        public bool isNullable;    // 是否为可空类型
        public string[] soundOptions;   // SoundData 类型专用：预设音效选项列表
        public bool allowCustomFile;    // SoundData 类型专用：是否允许浏览外部文件
        public string customName;       // Character 类型专用：自定义角色名称
        public bool isVisible = true;   // NEW: 该属性是否应该显示（来自Mod的enableIf判断结果）
    }

    // NEW: Helper → Mod 请求数据类
    public class PropertyUpdateRequest
    {
        public string token;                   // 关联原有的session token
        public string action = "validateVisibility";
        public Dictionary<string, string> updates;  // 修改的属性名 → 新值
        public PropertyData[] currentProperties;    // 当前的完整属性列表（含所有值）
    }

    // NEW: Mod → Helper 响应数据类
    public class PropertyUpdateResponse
    {
        public string token;
        public Dictionary<string, bool> visibilityChanges;  // 属性名 → 是否应该显示
    }

    // NEW: Helper IPC通信助手
    public static class FileIPC
    {
        private static readonly string TempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");

        /// <summary>
        /// 向Mod发送属性更新请求并等待响应
        /// </summary>
        public static PropertyUpdateResponse SendPropertyUpdateRequest(PropertyUpdateRequest request)
        {
            string requestPath = Path.Combine(TempDir, "validateVisibility.json");

            // 确保temp目录存在
            if (!Directory.Exists(TempDir))
                Directory.CreateDirectory(TempDir);

            try
            {
                // 写入请求文件
                var options = new JsonSerializerOptions { WriteIndented = false, IncludeFields = true };
                string json = JsonSerializer.Serialize(request, options);
                File.WriteAllText(requestPath, json);

                // 轮询响应（带超时）
                var stopwatch = Stopwatch.StartNew();
                int timeoutMs = 5000;  // 5秒超时

                while (stopwatch.ElapsedMilliseconds < timeoutMs)
                {
                    string responsePath = Path.Combine(TempDir, "validateVisibilityResponse.json");
                    if (File.Exists(responsePath))
                    {
                        try
                        {
                            string responseJson = File.ReadAllText(responsePath);
                            var response = JsonSerializer.Deserialize<PropertyUpdateResponse>(responseJson, options);

                            // 删除响应文件
                            try { File.Delete(responsePath); } catch { }

                            return response;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[FileIPC] Failed to parse response: {ex.Message}");
                        }
                    }

                    Thread.Sleep(50);  // 轮询间隔
                }

                throw new TimeoutException("Visibility validation request timed out");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileIPC] SendPropertyUpdateRequest failed: {ex.Message}");
                throw;
            }
            finally
            {
                // 清理请求文件
                try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
            }
        }
    }

    public class EditorForm : Form
    {
        private FlowLayoutPanel _panel;
        private Button _btnOK, _btnCancel;
        private string _eventType;
        private PropertyData[] _properties;
        private Dictionary<string, Control> _controls = new Dictionary<string, Control>();
        private bool _isClosingByButton = false;
        private string _pendingExecuteMethod = null;  // 点击操作按钮时要执行的方法名
        private string _token = Guid.NewGuid().ToString();  // NEW: IPC session token

        public event Action<Dictionary<string, string>> OnOK;
        public event Action OnCancel;
        public event Action<string> OnExecute;  // 新增：执行操作按钮事件

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
            _btnOK = new Button { Text = "确定(&O)", Width = 100, Height = 35 };

            _btnOK.Click += (s, e) =>
            {
                _isClosingByButton = true;
                OnOK?.Invoke(GetCurrentUpdates());
                this.Close();
            };
            _btnCancel.Click += (s, e) =>
            {
                _isClosingByButton = true;
                OnCancel?.Invoke();
                this.Close();
            };

            btnPanel.Controls.Add(_btnOK);
            btnPanel.Controls.Add(_btnCancel);
            this.Controls.Add(btnPanel);

            this.CancelButton = _btnCancel;
            this.AcceptButton = _btnOK;

            this.FormClosing += (s, e) =>
            {
                if (_isClosingByButton) return;
                e.Cancel = true;
                _isClosingByButton = true;
                OnCancel?.Invoke();
                this.Close();
            };
        }

        public void SetData(string eventType, PropertyData[] properties, string title = null)
        {
            _eventType = eventType;
            _properties = properties;
            this.Text = title ?? $"编辑事件: {eventType}";
            BuildUI();

            // NEW: 获取初始可见性（确保与Mod的enableIf状态一致）
            InitializeVisibility();
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

            // 分离普通属性和操作按钮
            var normalProps = new List<PropertyData>();
            var buttonProps = new List<PropertyData>();

            foreach (var prop in _properties)
            {
                if (prop.type == "Button")
                    buttonProps.Add(prop);
                else
                    normalProps.Add(prop);
            }

            // 渲染普通属性
            foreach (var prop in normalProps)
            {
                string displayName = prop.displayName ?? prop.name;

                var group = new GroupBox
                {
                    Text = displayName,
                    Width = 440,
                    Height = 55,
                    Padding = new Padding(5),
                    AccessibleName = displayName
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
                            Left = 10,
                            AccessibleName = displayName
                        };
                        // NEW: 附加值改变事件处理
                        txt.TextChanged += (s, e) =>
                        {
                            prop.value = txt.Text;
                            RequestVisibilityUpdate(prop.name, txt.Text);
                        };
                        inputCtrl = txt;
                        break;

                    case "Bool":
                        var chk = new CheckBox
                        {
                            Text = displayName,
                            Checked = prop.value == "true",
                            Top = 20,
                            Left = 10,
                            AutoSize = true,
                            AccessibleName = displayName
                        };
                        // NEW: 附加值改变事件处理
                        chk.CheckedChanged += (s, e) =>
                        {
                            string newValue = chk.Checked ? "true" : "false";
                            prop.value = newValue;
                            RequestVisibilityUpdate(prop.name, newValue);
                        };
                        inputCtrl = chk;
                        break;

                    case "Enum":
                        var cmb = new ComboBox
                        {
                            Width = 400,
                            Top = 20,
                            Left = 10,
                            DropDownStyle = ComboBoxStyle.DropDownList,
                            AccessibleName = displayName
                        };
                        if (prop.options != null)
                            cmb.Items.AddRange(prop.options);
                        if (!string.IsNullOrEmpty(prop.value))
                            cmb.SelectedItem = prop.value;
                        else if (cmb.Items.Count > 0)
                            cmb.SelectedIndex = 0;
                        // NEW: 附加值改变事件处理
                        cmb.SelectedValueChanged += (s, e) =>
                        {
                            string newValue = cmb.SelectedItem?.ToString() ?? "";
                            prop.value = newValue;
                            RequestVisibilityUpdate(prop.name, newValue);
                        };
                        inputCtrl = cmb;
                        break;

                    case "Vector2":
                    case "Float2":
                        // 解析 "x,y" 格式
                        var parts2 = (prop.value ?? "0,0").Split(',');
                        string xVal = parts2.Length > 0 ? parts2[0].Trim() : "0";
                        string yVal = parts2.Length > 1 ? parts2[1].Trim() : "0";
                        
                        var vecPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            Width = 420,
                            Height = 30,
                            Top = 20,
                            Left = 10,
                            Margin = new Padding(0)
                        };
                        
                        var lblX = new Label { Text = "X:", Width = 20, Top = 3 };
                        var txtX = new TextBox { Text = xVal, Width = 180, Name = "X" };
                        var lblY = new Label { Text = "Y:", Width = 20, Top = 3 };
                        var txtY = new TextBox { Text = yVal, Width = 180, Name = "Y" };
                        
                        vecPanel.Controls.AddRange(new Control[] { lblX, txtX, lblY, txtY });
                        inputCtrl = vecPanel;
                        break;

                    case "FloatExpression":
                        var exprTxt = new TextBox
                        {
                            Text = prop.value ?? "",
                            Width = 400,
                            Top = 20,
                            Left = 10,
                            AccessibleName = displayName
                        };
                        inputCtrl = exprTxt;
                        break;

                    case "FloatExpression2":
                        // 解析 "x,y" 格式的表达式
                        var exprParts = (prop.value ?? ",").Split(',');
                        string exprX = exprParts.Length > 0 ? exprParts[0].Trim() : "";
                        string exprY = exprParts.Length > 1 ? exprParts[1].Trim() : "";
                        
                        var exprPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            Width = 420,
                            Height = 30,
                            Top = 20,
                            Left = 10,
                            Margin = new Padding(0)
                        };
                        
                        var lblExpr1 = new Label { Text = "X:", Width = 20, Top = 3 };
                        var txtExpr1 = new TextBox { Text = exprX, Width = 180, Name = "X" };
                        var lblExpr2 = new Label { Text = "Y:", Width = 20, Top = 3 };
                        var txtExpr2 = new TextBox { Text = exprY, Width = 180, Name = "Y" };
                        
                        exprPanel.Controls.AddRange(new Control[] { lblExpr1, txtExpr1, lblExpr2, txtExpr2 });
                        inputCtrl = exprPanel;
                        break;

                    case "Color":
                        var colorPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            Width = 420,
                            Height = 30,
                            Top = 20,
                            Left = 10,
                            Margin = new Padding(0)
                        };
                        
                        var colorTxt = new TextBox 
                        { 
                            Text = prop.value ?? "#FFFFFF", 
                            Width = 300,
                            Name = "ColorText"
                        };
                        
                        var colorPreview = new Panel
                        {
                            Width = 30,
                            Height = 20,
                            BackColor = ParseColor(prop.value ?? "#FFFFFF")
                        };
                        
                        var btnPickColor = new Button
                        {
                            Text = "选择",
                            Width = 60,
                            Height = 23
                        };
                        
                        btnPickColor.Click += (s, e) =>
                        {
                            using (var colorDialog = new ColorDialog())
                            {
                                colorDialog.Color = colorPreview.BackColor;
                                if (colorDialog.ShowDialog() == DialogResult.OK)
                                {
                                    colorPreview.BackColor = colorDialog.Color;
                                    colorTxt.Text = $"#{colorDialog.Color.R:X2}{colorDialog.Color.G:X2}{colorDialog.Color.B:X2}";
                                }
                            }
                        };
                        
                        colorTxt.TextChanged += (s, e) =>
                        {
                            try
                            {
                                colorPreview.BackColor = ParseColor(colorTxt.Text);
                            }
                            catch { }
                        };
                        
                        colorPanel.Controls.AddRange(new Control[] { colorTxt, colorPreview, btnPickColor });
                        inputCtrl = colorPanel;
                        break;

                    case "SoundData":
                        // 解析 "filename|volume|pitch|pan|offset" 格式
                        var soundParts = (prop.value ?? "|||").Split('|');
                        string soundFilename = soundParts.Length > 0 ? soundParts[0] : "";
                        string soundVolume = soundParts.Length > 1 ? soundParts[1] : "100";
                        string soundPitch = soundParts.Length > 2 ? soundParts[2] : "100";
                        string soundPan = soundParts.Length > 3 ? soundParts[3] : "0";
                        string soundOffset = soundParts.Length > 4 ? soundParts[4] : "0";
                        
                        bool hasSoundOptions = prop.soundOptions != null && prop.soundOptions.Length > 0;
                        bool canBrowseFile = prop.allowCustomFile;
                        
                        group.Height = 240;  // 需要更多空间给 ListView
                        
                        var soundPanel = new Panel
                        {
                            Width = 420,
                            Height = 210,
                            Top = 20,
                            Left = 10
                        };
                        
                        // 第一行：搜索框 + 浏览按钮
                        var lblSearch = new Label { Text = "搜索:", Width = 45, Top = 5, Left = 0 };
                        var txtSearch = new TextBox { Width = hasSoundOptions ? 200 : 340, Top = 3, Left = 45, Name = "SearchBox" };
                        
                        // 隐藏的文件名存储（用于保存时获取值）
                        var txtHiddenFilename = new TextBox { Text = soundFilename, Width = 1, Top = 0, Left = 0, Name = "Filename", Visible = false };
                        soundPanel.Controls.Add(txtHiddenFilename);
                        
                        // 保存原始值作为后备（确保用户不修改时保留原值）
                        var txtOriginalFilename = new TextBox { Text = soundFilename, Width = 1, Top = 0, Left = 0, Name = "OriginalFilename", Visible = false };
                        soundPanel.Controls.Add(txtOriginalFilename);
                        
                        if (canBrowseFile)
                        {
                            var btnBrowse = new Button
                            {
                                Text = "浏览文件...",
                                Width = 100,
                                Top = 2,
                                Left = 260
                            };
                            btnBrowse.Click += (s, e) =>
                            {
                                using (var ofd = new OpenFileDialog())
                                {
                                    ofd.Filter = "音频文件|*.wav;*.ogg;*.mp3|所有文件|*.*";
                                    ofd.Title = prop.itsASong ? "选择歌曲文件" : "选择音效文件";
                                    if (ofd.ShowDialog() == DialogResult.OK)
                                    {
                                        string fileName = System.IO.Path.GetFileName(ofd.FileName);
                                        txtHiddenFilename.Text = fileName;
                                        
                                        // 如果有 ListView，添加并选中
                                        var lv = soundPanel.Controls.Find("SoundListView", false).FirstOrDefault() as ListView;
                                        if (lv != null)
                                        {
                                            // 检查是否已存在
                                            foreach (ListViewItem item in lv.Items)
                                            {
                                                if (item.Tag as string == fileName)
                                                {
                                                    item.Selected = true;
                                                    lv.Focus();
                                                    return;
                                                }
                                            }
                                            
                                            // 添加新项
                                            var newItem = new ListViewItem(fileName);
                                            newItem.SubItems.Add("(外部)");
                                            newItem.Tag = fileName;
                                            lv.Items.Add(newItem);
                                            newItem.Selected = true;
                                            lv.Focus();
                                        }
                                    }
                                }
                            };
                            soundPanel.Controls.Add(btnBrowse);
                        }
                        
                        soundPanel.Controls.Add(lblSearch);
                        soundPanel.Controls.Add(txtSearch);
                        
                        // 第二行：ListView
                        var listView = new ListView
                        {
                            Width = 405,
                            Height = 120,
                            Top = 30,
                            Left = 5,
                            View = View.Details,
                            FullRowSelect = true,
                            HideSelection = false,
                            Name = "SoundListView",
                            TabIndex = 0
                        };
                        listView.Columns.Add("音效名称", 280);
                        listView.Columns.Add("类型", 100);
                        
                        // 填充预设选项
                        if (hasSoundOptions)
                        {
                            // 只有当 SoundData 可空时，才添加"轨道默认"选项
                            if (prop.isNullable)
                            {
                                // 添加"轨道默认"选项（第一项）
                                var defaultItem = new ListViewItem("(使用轨道默认)");
                                defaultItem.SubItems.Add("(默认)");
                                defaultItem.Tag = "__track_default__";  // 特殊标记
                                listView.Items.Add(defaultItem);
                                
                                // 如果当前没有音效（使用轨道默认），默认选中第一项
                                if (string.IsNullOrEmpty(soundFilename))
                                {
                                    defaultItem.Selected = true;
                                }
                            }
                            
                            foreach (var opt in prop.soundOptions)
                            {
                                var item = new ListViewItem(opt);
                                item.SubItems.Add("(内置)");
                                item.Tag = opt;
                                listView.Items.Add(item);
                                if (opt == soundFilename) item.Selected = true;
                            }
                        }
                        
                        // 如果当前值不在列表中，添加为外部文件
                        if (!string.IsNullOrEmpty(soundFilename))
                        {
                            bool found = false;
                            foreach (ListViewItem item in listView.Items)
                            {
                                if (item.Tag as string == soundFilename)
                                {
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                var extItem = new ListViewItem(soundFilename);
                                extItem.SubItems.Add("(外部)");
                                extItem.Tag = soundFilename;
                                listView.Items.Add(extItem);
                                extItem.Selected = true;
                            }
                        }
                        
                        // 选中项变化时更新隐藏的文件名
                        listView.SelectedIndexChanged += (s, e) =>
                        {
                            if (listView.SelectedItems.Count > 0)
                            {
                                txtHiddenFilename.Text = listView.SelectedItems[0].Tag as string ?? listView.SelectedItems[0].Text;
                            }
                        };
                        
                        // 双击确认
                        listView.DoubleClick += (s, e) =>
                        {
                            _isClosingByButton = true;
                            OnOK?.Invoke(GetCurrentUpdates());
                            this.Close();
                        };
                        
                        // 搜索过滤
                        txtSearch.TextChanged += (s, e) =>
                        {
                            var keyword = txtSearch.Text.ToLower();
                            foreach (ListViewItem item in listView.Items)
                            {
                                bool match = string.IsNullOrEmpty(keyword) || 
                                             item.Text.ToLower().Contains(keyword);
                                // ListView 没有 Hidden 属性，需要移除/添加或使用其他方式
                                // 简化处理：使用 BackColor 模拟
                                item.BackColor = match ? SystemColors.Window : SystemColors.ControlDark;
                                item.ForeColor = match ? SystemColors.WindowText : SystemColors.GrayText;
                            }
                        };
                        
                        soundPanel.Controls.Add(listView);
                        
                        // 确保选中状态生效
                        listView.Refresh();
                        if (listView.SelectedItems.Count > 0)
                        {
                            listView.Focus();
                        }
                        
                        // 第三行：音量
                        var lblVolume = new Label { Text = "音量:", Width = 45, Top = 155, Left = 0 };
                        var txtVolume = new TextBox { Text = soundVolume, Width = 60, Top = 153, Left = 45, Name = "Volume" };
                        var lblVolumeHint = new Label { Text = "(0-300)", Width = 60, Top = 155, Left = 110 };
                        soundPanel.Controls.Add(lblVolume);
                        soundPanel.Controls.Add(txtVolume);
                        soundPanel.Controls.Add(lblVolumeHint);
                        
                        // 音调
                        var lblPitch = new Label { Text = "音调:", Width = 45, Top = 155, Left = 175 };
                        var txtPitch = new TextBox { Text = soundPitch, Width = 60, Top = 153, Left = 220, Name = "Pitch" };
                        var lblPitchHint = new Label { Text = "(0-300)", Width = 60, Top = 155, Left = 285 };
                        soundPanel.Controls.Add(lblPitch);
                        soundPanel.Controls.Add(txtPitch);
                        soundPanel.Controls.Add(lblPitchHint);
                        
                        // 第四行：声道和偏移
                        var lblPan = new Label { Text = "声道:", Width = 45, Top = 180, Left = 0 };
                        var txtPan = new TextBox { Text = soundPan, Width = 60, Top = 178, Left = 45, Name = "Pan" };
                        var lblPanHint = new Label { Text = "(-100~100)", Width = 65, Top = 180, Left = 110 };
                        
                        var lblOffset = new Label { Text = "偏移:", Width = 45, Top = 180, Left = 175 };
                        var txtOffset = new TextBox { Text = soundOffset, Width = 60, Top = 178, Left = 220, Name = "Offset" };
                        var lblOffsetHint = new Label { Text = "毫秒", Width = 40, Top = 180, Left = 285 };
                        
                        soundPanel.Controls.Add(lblPan);
                        soundPanel.Controls.Add(txtPan);
                        soundPanel.Controls.Add(lblPanHint);
                        soundPanel.Controls.Add(lblOffset);
                        soundPanel.Controls.Add(txtOffset);
                        soundPanel.Controls.Add(lblOffsetHint);
                        
                        inputCtrl = soundPanel;
                        break;

                    case "Character":
                        // 角色选择：ListView + 搜索框
                        group.Height = 240;
                        
                        var charPanel = new Panel
                        {
                            Width = 420,
                            Height = 210,
                            Top = 20,
                            Left = 10
                        };
                        
                        // 第一行：搜索框
                        var lblCharSearch = new Label { Text = "搜索:", Width = 45, Top = 5, Left = 0 };
                        var txtCharSearch = new TextBox { Width = 360, Top = 3, Left = 45, Name = "CharSearchBox" };
                        charPanel.Controls.Add(lblCharSearch);
                        charPanel.Controls.Add(txtCharSearch);
                        
                        // 隐藏的角色名存储
                        var txtHiddenChar = new TextBox { Text = prop.value ?? "", Width = 1, Top = 0, Left = 0, Name = "CharacterValue", Visible = false };
                        var txtHiddenCustomName = new TextBox { Text = prop.customName ?? "", Width = 1, Top = 0, Left = 0, Name = "CustomCharacterName", Visible = false };
                        charPanel.Controls.Add(txtHiddenChar);
                        charPanel.Controls.Add(txtHiddenCustomName);
                        
                        // 第二行：ListView
                        var charListView = new ListView
                        {
                            Width = 405,
                            Height = 170,
                            Top = 30,
                            Left = 5,
                            View = View.Details,
                            FullRowSelect = true,
                            HideSelection = false,
                            Name = "CharacterListView",
                            TabIndex = 0
                        };
                        charListView.Columns.Add("角色名称", 380);
                        
                        // 填充角色列表
                        if (prop.options != null)
                        {
                            foreach (var charName in prop.options)
                            {
                                var item = new ListViewItem(charName);
                                item.Tag = charName;
                                charListView.Items.Add(item);
                                if (charName == prop.value) item.Selected = true;
                            }
                        }
                        
                        // 选中项变化时更新隐藏文本框
                        charListView.SelectedIndexChanged += (s, e) =>
                        {
                            if (charListView.SelectedItems.Count > 0)
                            {
                                txtHiddenChar.Text = charListView.SelectedItems[0].Tag as string ?? charListView.SelectedItems[0].Text;
                            }
                        };
                        
                        // 双击确认
                        charListView.DoubleClick += (s, e) =>
                        {
                            _isClosingByButton = true;
                            OnOK?.Invoke(GetCurrentUpdates());
                            this.Close();
                        };
                        
                        // 搜索过滤
                        txtCharSearch.TextChanged += (s, e) =>
                        {
                            var keyword = txtCharSearch.Text.ToLower();
                            foreach (ListViewItem item in charListView.Items)
                            {
                                bool match = string.IsNullOrEmpty(keyword) || 
                                             item.Text.ToLower().Contains(keyword);
                                item.BackColor = match ? SystemColors.Window : SystemColors.ControlDark;
                                item.ForeColor = match ? SystemColors.WindowText : SystemColors.GrayText;
                            }
                        };
                        
                        charPanel.Controls.Add(charListView);
                        
                        // 确保选中状态生效
                        charListView.Refresh();
                        if (charListView.SelectedItems.Count > 0)
                        {
                            charListView.Focus();
                        }
                        
                        inputCtrl = charPanel;
                        break;

                    default:
                        var lbl = new Label
                        {
                            Text = $"不支持的类型: {prop.type}",
                            Width = 400,
                            Top = 20,
                            Left = 10,
                            AccessibleName = displayName
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

            // 渲染操作按钮（放在单独的分组中）
            if (buttonProps.Count > 0)
            {
                var actionGroup = new GroupBox
                {
                    Text = "操作",
                    Width = 440,
                    Height = 50 + buttonProps.Count * 40,
                    Padding = new Padding(10),
                    Margin = new Padding(3, 10, 3, 3)
                };

                int btnTop = 20;
                foreach (var btnProp in buttonProps)
                {
                    string displayName = btnProp.displayName ?? btnProp.name;
                    string methodName = btnProp.methodName;

                    var actionBtn = new Button
                    {
                        Text = displayName,
                        Width = 400,
                        Height = 35,
                        Top = btnTop,
                        Left = 10,
                        AccessibleName = displayName,
                        Tag = methodName  // 存储方法名
                    };

                    actionBtn.Click += (s, e) =>
                    {
                        var btn = s as Button;
                        string method = btn?.Tag as string;
                        if (!string.IsNullOrEmpty(method))
                        {
                            _pendingExecuteMethod = method;
                            _isClosingByButton = true;
                            OnExecute?.Invoke(method);
                            this.Close();
                        }
                    };

                    actionGroup.Controls.Add(actionBtn);
                    btnTop += 40;
                }

                _panel.Controls.Add(actionGroup);
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
                else if (ctrl is FlowLayoutPanel panel)
                {
                    // 处理 Vector2, Float2, FloatExpression2, Color
                    var txtX = panel.Controls.Find("X", false).FirstOrDefault() as TextBox;
                    var txtY = panel.Controls.Find("Y", false).FirstOrDefault() as TextBox;
                    var colorTxt = panel.Controls.Find("ColorText", false).FirstOrDefault() as TextBox;
                    
                    if (txtX != null && txtY != null)
                    {
                        // Vector2, Float2, FloatExpression2
                        value = $"{txtX.Text},{txtY.Text}";
                    }
                    else if (colorTxt != null)
                    {
                        // Color
                        value = colorTxt.Text;
                    }
                }
                else if (ctrl is Panel soundPanel)
                {
                    // 检查是否是 Character 类型的 Panel
                    var charValue = soundPanel.Controls.Find("CharacterValue", false).FirstOrDefault() as TextBox;
                    if (charValue != null)
                    {
                        // Character 类型
                        value = charValue.Text;
                        
                        // 同时获取自定义角色名称
                        var customNameCtrl = soundPanel.Controls.Find("CustomCharacterName", false).FirstOrDefault() as TextBox;
                        if (customNameCtrl != null && !string.IsNullOrEmpty(customNameCtrl.Text))
                        {
                            // 如果有自定义名称，需要额外存储
                            // 这里我们用特殊格式：CharacterName|CustomName
                            // 但实际上 customCharacterName 是单独的字段，需要在 updates 中单独处理
                        }
                    }
                    else
                    {
                        // SoundData 类型
                        var txtFilename = soundPanel.Controls.Find("Filename", false).FirstOrDefault() as TextBox;
                        var txtVolume = soundPanel.Controls.Find("Volume", false).FirstOrDefault() as TextBox;
                        var txtPitch = soundPanel.Controls.Find("Pitch", false).FirstOrDefault() as TextBox;
                        var txtPan = soundPanel.Controls.Find("Pan", false).FirstOrDefault() as TextBox;
                        var txtOffset = soundPanel.Controls.Find("Offset", false).FirstOrDefault() as TextBox;
                        
                        string filename = txtFilename?.Text ?? "";
                        
                        // 检查是否选中了"轨道默认"选项
                        var listView = soundPanel.Controls.Find("SoundListView", false).FirstOrDefault() as ListView;
                        if (listView != null && listView.SelectedItems.Count > 0)
                        {
                            string selectedTag = listView.SelectedItems[0].Tag as string;
                            if (selectedTag == "__track_default__")
                            {
                                // 选中"轨道默认"，返回空字符串让游戏使用轨道默认音效
                                value = "";
                            }
                            else if (!string.IsNullOrEmpty(filename))
                            {
                                // 正常音效
                                string volume = txtVolume?.Text ?? "100";
                                string pitch = txtPitch?.Text ?? "100";
                                string pan = txtPan?.Text ?? "0";
                                string offset = txtOffset?.Text ?? "0";
                                value = $"{filename}|{volume}|{pitch}|{pan}|{offset}";
                            }
                            else
                            {
                                // 用户没有选择任何项，使用原始值作为后备
                                var txtOriginalFilename = soundPanel.Controls.Find("OriginalFilename", false).FirstOrDefault() as TextBox;
                                if (txtOriginalFilename != null && !string.IsNullOrEmpty(txtOriginalFilename.Text))
                                {
                                    string originalFilename = txtOriginalFilename.Text;
                                    string volume = txtVolume?.Text ?? "100";
                                    string pitch = txtPitch?.Text ?? "100";
                                    string pan = txtPan?.Text ?? "0";
                                    string offset = txtOffset?.Text ?? "0";
                                    value = $"{originalFilename}|{volume}|{pitch}|{pan}|{offset}";
                                }
                            }
                        }
                    }
                }

                if (value != null)
                    updates[propName] = value;
            }

            return updates;
        }

        // ===== NEW: 动态UI可见性处理方法 =====

        private void InitializeVisibility()
        {
            var request = new PropertyUpdateRequest
            {
                token = _token,
                action = "validateVisibility",
                updates = new Dictionary<string, string>(),  // 空，仅查询
                currentProperties = _properties
            };

            try
            {
                var response = FileIPC.SendPropertyUpdateRequest(request);
                if (response?.visibilityChanges != null)
                {
                    foreach (var kvp in response.visibilityChanges)
                    {
                        UpdatePropertyVisibility(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize visibility: {ex.Message}");
                // 继续，使用初始值
            }
        }

        private void RequestVisibilityUpdate(string changedPropertyName, string newValue)
        {
            // 1. 收集当前的完整PropertyData（包括最新的值）
            var request = new PropertyUpdateRequest
            {
                token = _token,
                action = "validateVisibility",
                updates = new Dictionary<string, string> { { changedPropertyName, newValue } },
                currentProperties = _properties  // 发送完整状态给Mod
            };

            // 2. 通过FileIPC异步发送
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var response = FileIPC.SendPropertyUpdateRequest(request);
                    OnPropertyVisibilityUpdated(response);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to update visibility: {ex.Message}");
                }
            });
        }

        private void OnPropertyVisibilityUpdated(PropertyUpdateResponse response)
        {
            if (response?.visibilityChanges == null) return;

            // 在UI线程上执行
            this.Invoke(() =>
            {
                foreach (var kvp in response.visibilityChanges)
                {
                    string propName = kvp.Key;
                    bool shouldShow = kvp.Value;

                    UpdatePropertyVisibility(propName, shouldShow);

                    // 屏幕阅读器通知
                    AnnounceVisibilityChange(propName, shouldShow);
                }
            });
        }

        private void UpdatePropertyVisibility(string propertyName, bool shouldBeVisible)
        {
            if (!_controls.TryGetValue(propertyName, out var control))
                return;

            // 查找这个控件的GroupBox容器
            var groupBox = control.Parent as GroupBox;
            if (groupBox != null)
            {
                // 仅改变Visible，不改变任何其他属性
                // 这样屏幕阅读器焦点不会丢失
                groupBox.Visible = shouldBeVisible;
            }
            else
            {
                control.Visible = shouldBeVisible;
            }

            // 更新PropertyData记录
            var prop = _properties.FirstOrDefault(p => p.name == propertyName);
            if (prop != null)
            {
                prop.isVisible = shouldBeVisible;
            }
        }

        private void AnnounceVisibilityChange(string propertyName, bool shouldShow)
        {
            // 通知屏幕阅读器属性的可见性变化
            // 避免打断当前编辑的流程
            string message = shouldShow
                ? $"属性{propertyName}已显示"
                : $"属性{propertyName}已隐藏";

            // 使用较低优先级的通知（不打断用户当前操作）
            // 注：具体实现需要根据项目的屏幕阅读器支持库来完成
            System.Diagnostics.Debug.WriteLine($"[Accessibility] {message}");
        }

        private Color ParseColor(string colorStr)
        {
            try
            {
                if (string.IsNullOrEmpty(colorStr))
                    return Color.White;

                // 支持 #RRGGBB 格式
                if (colorStr.StartsWith("#") && colorStr.Length == 7)
                {
                    int r = int.Parse(colorStr.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
                    int g = int.Parse(colorStr.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
                    int b = int.Parse(colorStr.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
                    return Color.FromArgb(r, g, b);
                }

                // 尝试直接解析颜色名称
                return Color.FromName(colorStr);
            }
            catch
            {
                return Color.White;
            }
        }
    }
}
