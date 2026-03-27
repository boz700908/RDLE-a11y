using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace RDEventEditorHelper
{
    public class PropertyData
    {
        public string name;
        public string displayName;
        public string value;
        public string type;
        public string[] options;
        public string[] localizedOptions; // 本地化显示名，null 时直接用 options
        public string methodName;  // Button 类型专用：要调用的方法名
        public bool itsASong;      // SoundData 类型专用：区分歌曲/音效
        public bool isNullable;    // 是否为可空类型
        public string[] soundOptions;   // SoundData 类型专用：预设音效选项列表
        public string[] localizedSoundOptions;  // SoundData 类型专用：预设音效的本地化名称
        public bool allowCustomFile;    // SoundData 类型专用：是否允许浏览外部文件
        public string customName;       // Character 类型专用：自定义角色名称
        public bool isVisible = true;   // NEW: 该属性是否应该显示（来自Mod的enableIf判断结果）
        public int arrayLength;         // Array 类型专用：元素个数
        public int roomCount;           // Rooms 类型专用：房间总数
        public string roomsUsage;       // Rooms 类型专用：使用模式
        public string[] rowNames;       // EnumArray 专用：轨道显示名称列表
        public int rowCount;            // EnumArray 专用：实际显示的行数
        public MethodSuggestion[] autocompleteSuggestions;  // 自动完成建议列表
        public bool hasBPMCalculator;  // 是否带有 BPMCalculator 属性
        public string[] tabLabels;     // SoundDataArray 专用：各标签页的本地化名称
    }

    public class MethodSuggestion
    {
        public string scope;       // "level" / "vfx" / "room"
        public string name;        // 方法名
        public string signature;   // 完整签名
        public string description; // 方法描述（可为 null）
        public string fullText;    // 选中后填充的完整文本
    }

    // 自动完成列表项包装类
    internal class AutocompleteItem
    {
        public string Display;
        public MethodSuggestion Suggestion;
        public override string ToString() => Display;
    }

    // 自定义 ListBox：暴露辅助功能通知方法
    internal class AccessibleListBox : ListBox
    {
        public void NotifyFocus(int index)
        {
            AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        }
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
        private static string _currentToken = null;  // 当前会话的 token

        /// <summary>
        /// 设置当前会话的 token（从 EditorForm 传入）
        /// </summary>
        public static void SetCurrentToken(string token)
        {
            _currentToken = token;
        }

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
                string json = JsonConvert.SerializeObject(request);
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
                            var response = JsonConvert.DeserializeObject<PropertyUpdateResponse>(responseJson);

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

        /// <summary>
        /// 向 Mod 发送播放声音请求（单向通信，无需等待响应）
        /// </summary>
        public static void SendPlaySoundRequest(string soundName, int volume, int pitch, int pan, bool itsASong)
        {
            if (string.IsNullOrEmpty(_currentToken))
            {
                Debug.WriteLine("[FileIPC] Cannot send play sound request: token not set");
                return;
            }

            // 先停止之前的声音
            SendStopSoundRequest();
            System.Threading.Thread.Sleep(50);  // 等待停止请求被处理

            string requestPath = Path.Combine(TempDir, "playSoundRequest.json");

            // 确保temp目录存在
            if (!Directory.Exists(TempDir))
                Directory.CreateDirectory(TempDir);

            try
            {
                var request = new
                {
                    token = _currentToken,
                    soundName = soundName,
                    volume = volume,
                    pitch = pitch,
                    pan = pan,
                    itsASong = itsASong
                };

                string json = JsonConvert.SerializeObject(request, Formatting.Indented);
                File.WriteAllText(requestPath, json);

                Debug.WriteLine($"[FileIPC] Sent play sound request: {soundName} (itsASong: {itsASong})");
            }
            catch (Exception ex)
            {
                // 静默失败，不影响主流程
                Debug.WriteLine($"[FileIPC] SendPlaySoundRequest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 向 Mod 发送停止声音请求（单向通信，无需等待响应）
        /// </summary>
        public static void SendStopSoundRequest()
        {
            if (string.IsNullOrEmpty(_currentToken))
            {
                Debug.WriteLine("[FileIPC] Cannot send stop sound request: token not set");
                return;
            }

            string requestPath = Path.Combine(TempDir, "stopSoundRequest.json");

            // 确保temp目录存在
            if (!Directory.Exists(TempDir))
                Directory.CreateDirectory(TempDir);

            try
            {
                var request = new
                {
                    token = _currentToken
                };

                string json = JsonConvert.SerializeObject(request, Formatting.Indented);
                File.WriteAllText(requestPath, json);

                Debug.WriteLine("[FileIPC] Sent stop sound request");
            }
            catch (Exception ex)
            {
                // 静默失败，不影响主流程
                Debug.WriteLine($"[FileIPC] SendStopSoundRequest failed: {ex.Message}");
            }
        }
    }

    public class EditorForm : Form
    {
        private FlowLayoutPanel _panel;
        private Button _btnOK, _btnCancel;
        private string _eventType;
        private PropertyData[] _properties;
        private string[] _levelAudioFiles;
        private string[] _localizedLevelAudioFiles;  // 本地化的音频文件显示名称
        private string _levelDirectory;
        private static Dictionary<string, string> _internalSongs;  // 内置音乐列表
        private Dictionary<string, Control> _controls = new Dictionary<string, Control>();
        private bool _isClosingByButton = false;
        private string _pendingExecuteMethod = null;  // 点击操作按钮时要执行的方法名
        private string _token = Guid.NewGuid().ToString();  // NEW: IPC session token

        public event Action<Dictionary<string, string>> OnOK;
        public event Action OnCancel;
        public event Action<string> OnExecute;  // 新增：执行操作按钮事件
        public event Action<Dictionary<string, string>> OnBPMCalculator;  // BPM计算器：带更新的动作

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

            _btnCancel = new Button { Text = "取消 (Cancel)", Width = 120, Height = 35 };
            _btnOK = new Button { Text = "确定 (OK)", Width = 120, Height = 35 };

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

        public void SetData(string eventType, PropertyData[] properties, string title = null, string[] levelAudioFiles = null, string levelDirectory = null, string[] localizedLevelAudioFiles = null, string token = null, Dictionary<string, string> internalSongs = null)
        {
            _eventType = eventType;
            _properties = properties;
            _levelAudioFiles = levelAudioFiles;
            _localizedLevelAudioFiles = localizedLevelAudioFiles ?? levelAudioFiles;  // 如果没有本地化，使用原始名称
            _levelDirectory = levelDirectory;
            _internalSongs = internalSongs;
            this.Text = title ?? $"编辑事件 (Edit Event): {eventType}";

            // 使用传入的 token（如果提供），否则使用自己生成的
            if (!string.IsNullOrEmpty(token))
            {
                _token = token;
            }

            // 设置当前会话的 token 到 FileIPC
            FileIPC.SetCurrentToken(_token);

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
                    Text = "该事件没有可编辑的属性 (No editable properties)",
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
                        {
                            var txt = new TextBox
                            {
                                Text = prop.value ?? "",
                                Width = 400,
                                Top = 20,
                                Left = 10,
                                AccessibleName = displayName
                            };
                            txt.TextChanged += (s, e) =>
                            {
                                prop.value = txt.Text;
                                RequestVisibilityUpdate(prop.name, txt.Text);
                            };
                            inputCtrl = txt;

                            // 为带有 BPMCalculator 属性的 Float 添加计算器按钮
                            if (prop.type == "Float" && prop.hasBPMCalculator)
                            {
                                var bpmBtn = new Button
                                {
                                    Text = "BPM 计算器(BPM Calculator)",
                                    Width = 400,
                                    Height = 30,
                                    Top = txt.Top + txt.Height + 5,
                                    Left = 10,
                                    AccessibleName = "BPM 计算器(BPM Calculator)"
                                };
                                bpmBtn.Click += (s, e) =>
                                {
                                    _isClosingByButton = true;
                                    OnBPMCalculator?.Invoke(GetCurrentUpdates());
                                    this.Close();
                                };
                                group.Controls.Add(bpmBtn);
                                group.Height += bpmBtn.Height + 5;
                            }
                        }
                        break;

                    case "String":
                        if (prop.autocompleteSuggestions != null && prop.autocompleteSuggestions.Length > 0)
                        {
                            // 自动完成模式：TextBox + 无边框悬浮窗口
                            var acTextBox = new TextBox
                            {
                                Name = "AutocompleteTextBox",
                                Text = prop.value ?? "",
                                Width = 400,
                                Top = 20,
                                Left = 10
                            };

                            var allSuggestions = prop.autocompleteSuggestions;

                            // 悬浮建议列表（无边框 Form，避免读屏朗读容器角色）
                            var popupListBox = new AccessibleListBox
                            {
                                Dock = DockStyle.Fill,
                                BorderStyle = BorderStyle.FixedSingle,
                                IntegralHeight = false,
                                SelectionMode = SelectionMode.One
                            };

                            var popupForm = new Form
                            {
                                FormBorderStyle = FormBorderStyle.None,
                                ShowInTaskbar = false,
                                StartPosition = FormStartPosition.Manual,
                                TopMost = true,
                                ControlBox = false,
                                MaximizeBox = false,
                                MinimizeBox = false
                            };
                            popupForm.Controls.Add(popupListBox);

                            bool popupVisible = false;
                            int selectedIndex = -1;

                            // 通知读屏朗读当前选中项
                            Action announceSelection = () =>
                            {
                                if (selectedIndex >= 0 && selectedIndex < popupListBox.Items.Count)
                                {
                                    popupListBox.NotifyFocus(selectedIndex);
                                }
                            };

                            Action showPopup = () =>
                            {
                                if (popupListBox.Items.Count == 0)
                                {
                                    if (popupVisible) { popupForm.Hide(); popupVisible = false; this.CancelButton = _btnCancel; }
                                    return;
                                }
                                // 确保默认选中第一项
                                if (selectedIndex < 0 || selectedIndex >= popupListBox.Items.Count)
                                {
                                    selectedIndex = 0;
                                    popupListBox.SelectedIndex = 0;
                                }
                                int itemCount = Math.Min(popupListBox.Items.Count, 8);
                                int h = itemCount * popupListBox.ItemHeight + 4;
                                popupForm.Size = new Size(400, h);
                                if (!popupVisible)
                                {
                                    var pt = acTextBox.PointToScreen(new Point(0, acTextBox.Height));
                                    popupForm.Location = pt;
                                    popupForm.Show(this);
                                    popupVisible = true;
                                    this.CancelButton = null;
                                    acTextBox.Focus();
                                }
                                // 延迟通知读屏，确保弹窗渲染完成
                                var announceTimer = new System.Windows.Forms.Timer { Interval = 50 };
                                announceTimer.Tick += (s2, e2) =>
                                {
                                    announceTimer.Stop();
                                    announceTimer.Dispose();
                                    announceSelection();
                                };
                                announceTimer.Start();
                            };

                            Action hidePopup = () =>
                            {
                                if (popupVisible)
                                {
                                    popupForm.Hide();
                                    popupVisible = false;
                                    this.CancelButton = _btnCancel;
                                }
                            };

                            // 过滤建议列表
                            Action<string> filterSuggestions = (input) =>
                            {
                                popupListBox.BeginUpdate();
                                popupListBox.Items.Clear();

                                string searchScope = null;
                                string searchName = input ?? "";

                                int parenIdx = searchName.IndexOf('(');
                                if (parenIdx >= 0) searchName = searchName.Substring(0, parenIdx);

                                var dotParts = searchName.Split('.');
                                if (dotParts.Length == 2)
                                {
                                    searchScope = dotParts[0];
                                    searchName = dotParts[1];
                                }

                                foreach (var sg in allSuggestions)
                                {
                                    if (searchScope != null && sg.scope.IndexOf(searchScope, StringComparison.OrdinalIgnoreCase) < 0)
                                        continue;

                                    bool scopeMatch = searchScope == null && searchName.Length > 0 &&
                                        sg.scope.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0;
                                    bool nameMatch = searchName.Length == 0 ||
                                        sg.name.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0;

                                    if (scopeMatch || nameMatch)
                                    {
                                        string display = sg.name;
                                        if (!string.IsNullOrEmpty(sg.signature) && sg.signature != sg.name + "()")
                                            display += " - " + sg.signature;
                                        if (!string.IsNullOrEmpty(sg.description))
                                            display += " - " + sg.description;

                                        popupListBox.Items.Add(new AutocompleteItem { Display = display, Suggestion = sg });
                                    }
                                }

                                popupListBox.EndUpdate();

                                bool isExactMatch = popupListBox.Items.Count == 1 &&
                                    popupListBox.Items[0] is AutocompleteItem single &&
                                    single.Suggestion.fullText == (input ?? "");

                                if (popupListBox.Items.Count > 0 && !isExactMatch)
                                {
                                    selectedIndex = 0;
                                    popupListBox.SelectedIndex = 0;
                                    showPopup();
                                }
                                else
                                {
                                    selectedIndex = -1;
                                    hidePopup();
                                }
                            };

                            bool suppressFilter = false;

                            acTextBox.TextChanged += (s, e) =>
                            {
                                prop.value = acTextBox.Text;
                                if (!suppressFilter)
                                    filterSuggestions(acTextBox.Text);
                                RequestVisibilityUpdate(prop.name, acTextBox.Text);
                            };

                            Action acceptSuggestion = () =>
                            {
                                if (selectedIndex >= 0 && selectedIndex < popupListBox.Items.Count &&
                                    popupListBox.Items[selectedIndex] is AutocompleteItem item)
                                {
                                    suppressFilter = true;
                                    acTextBox.Text = item.Suggestion.fullText;
                                    acTextBox.SelectionStart = acTextBox.Text.Length;
                                    suppressFilter = false;
                                    hidePopup();
                                }
                            };

                            acTextBox.PreviewKeyDown += (s, e) =>
                            {
                                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                                    e.IsInputKey = true;
                                if (e.KeyCode == Keys.Tab && popupVisible && popupListBox.Items.Count > 0)
                                    e.IsInputKey = true;
                            };

                            acTextBox.KeyDown += (s, e) =>
                            {
                                if (!popupVisible || popupListBox.Items.Count == 0)
                                    return;

                                if (e.KeyCode == Keys.Down)
                                {
                                    selectedIndex = Math.Min(selectedIndex + 1, popupListBox.Items.Count - 1);
                                    popupListBox.SelectedIndex = selectedIndex;
                                    announceSelection();
                                    e.Handled = true;
                                    e.SuppressKeyPress = true;
                                }
                                else if (e.KeyCode == Keys.Up)
                                {
                                    selectedIndex = Math.Max(selectedIndex - 1, 0);
                                    popupListBox.SelectedIndex = selectedIndex;
                                    announceSelection();
                                    e.Handled = true;
                                    e.SuppressKeyPress = true;
                                }
                                else if (e.KeyCode == Keys.Tab)
                                {
                                    acceptSuggestion();
                                    e.Handled = true;
                                    e.SuppressKeyPress = true;
                                }
                                else if (e.KeyCode == Keys.Escape)
                                {
                                    hidePopup();
                                    e.Handled = true;
                                    e.SuppressKeyPress = true;
                                }
                            };

                            popupListBox.Click += (s, e) =>
                            {
                                if (popupListBox.SelectedItem is AutocompleteItem item)
                                {
                                    selectedIndex = popupListBox.SelectedIndex;
                                    suppressFilter = true;
                                    acTextBox.Text = item.Suggestion.fullText;
                                    acTextBox.SelectionStart = acTextBox.Text.Length;
                                    suppressFilter = false;
                                    hidePopup();
                                    acTextBox.Focus();
                                }
                            };

                            acTextBox.LostFocus += (s, e) =>
                            {
                                var timer = new System.Windows.Forms.Timer { Interval = 200 };
                                timer.Tick += (s2, e2) =>
                                {
                                    timer.Stop();
                                    timer.Dispose();
                                    if (!acTextBox.Focused)
                                        hidePopup();
                                };
                                timer.Start();
                            };

                            this.FormClosing += (s, e) => { hidePopup(); popupForm.Dispose(); };

                            inputCtrl = acTextBox;
                        }
                        else
                        {
                            // 普通文本框
                            var txt = new TextBox
                            {
                                Text = prop.value ?? "",
                                Width = 400,
                                Top = 20,
                                Left = 10,
                                AccessibleName = displayName
                            };
                            txt.TextChanged += (s, e) =>
                            {
                                prop.value = txt.Text;
                                RequestVisibilityUpdate(prop.name, txt.Text);
                            };
                            inputCtrl = txt;
                        }
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

                    case "Row":
                        goto case "Enum";

                    case "Enum":
                        var rawOptions = prop.options;
                        var displayOptions = prop.localizedOptions ?? rawOptions;
                        var cmb = new ComboBox
                        {
                            Width = 400,
                            Top = 20,
                            Left = 10,
                            DropDownStyle = ComboBoxStyle.DropDownList,
                            AccessibleName = displayName
                        };
                        if (displayOptions != null)
                            cmb.Items.AddRange(displayOptions);
                        // 用 rawOptions 索引匹配初始值，避免显示名与 value 不匹配
                        if (rawOptions != null && !string.IsNullOrEmpty(prop.value))
                        {
                            int idx = Array.IndexOf(rawOptions, prop.value);
                            if (idx >= 0 && idx < cmb.Items.Count) cmb.SelectedIndex = idx;
                            else if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
                        }
                        else if (cmb.Items.Count > 0)
                            cmb.SelectedIndex = 0;
                        // 值改变时返回原始枚举名
                        cmb.SelectedValueChanged += (s, e) =>
                        {
                            int idx = cmb.SelectedIndex;
                            string rawValue = (rawOptions != null && idx >= 0 && idx < rawOptions.Length)
                                ? rawOptions[idx]
                                : cmb.SelectedItem?.ToString() ?? "";
                            prop.value = rawValue;
                            RequestVisibilityUpdate(prop.name, rawValue);
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
                        // 增大 GroupBox 高度以容纳两行控件
                        group.Height = 85;

                        var colorPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.TopDown,
                            Width = 420,
                            Height = 65,
                            Top = 15,
                            Left = 10,
                            Margin = new Padding(0)
                        };

                        // === 第一行：R / G / B 数值输入 ===
                        var rgbRow = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            Width = 420,
                            Height = 28,
                            Margin = new Padding(0)
                        };

                        var initialColor = ParseColor(prop.value ?? "#FFFFFF");

                        var lblR = new Label { Text = "R:", AutoSize = true, Margin = new Padding(0, 5, 2, 0) };
                        var nudR = new NumericUpDown
                        {
                            Minimum = 0, Maximum = 255,
                            Value = initialColor.R,
                            Width = 60,
                            AccessibleName = displayName + " R",
                            Name = "ColorR"
                        };

                        var lblG = new Label { Text = "G:", AutoSize = true, Margin = new Padding(8, 5, 2, 0) };
                        var nudG = new NumericUpDown
                        {
                            Minimum = 0, Maximum = 255,
                            Value = initialColor.G,
                            Width = 60,
                            AccessibleName = displayName + " G",
                            Name = "ColorG"
                        };

                        var lblB = new Label { Text = "B:", AutoSize = true, Margin = new Padding(8, 5, 2, 0) };
                        var nudB = new NumericUpDown
                        {
                            Minimum = 0, Maximum = 255,
                            Value = initialColor.B,
                            Width = 60,
                            AccessibleName = displayName + " B",
                            Name = "ColorB"
                        };

                        rgbRow.Controls.AddRange(new Control[] { lblR, nudR, lblG, nudG, lblB, nudB });

                        // === 第二行：十六进制 + 预览 + 选择按钮 ===
                        var hexRow = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            Width = 420,
                            Height = 28,
                            Margin = new Padding(0)
                        };

                        var colorTxt = new TextBox
                        {
                            Text = prop.value ?? "#FFFFFF",
                            Width = 300,
                            Name = "ColorText",
                            AccessibleName = displayName + " Hex"
                        };

                        var colorPreview = new Panel
                        {
                            Width = 30,
                            Height = 20,
                            BackColor = initialColor,
                            AccessibleName = displayName + " Preview",
                            AccessibleRole = AccessibleRole.Graphic
                        };

                        var btnPickColor = new Button
                        {
                            Text = "选择 (Select)",
                            Width = 60,
                            Height = 23,
                            AccessibleName = displayName + " 选择颜色"
                        };

                        hexRow.Controls.AddRange(new Control[] { colorTxt, colorPreview, btnPickColor });

                        // === 同步逻辑 ===
                        bool isSyncing = false;

                        // RGB → Hex + 预览
                        EventHandler rgbChanged = (s, e) =>
                        {
                            if (isSyncing) return;
                            isSyncing = true;
                            var c = Color.FromArgb((int)nudR.Value, (int)nudG.Value, (int)nudB.Value);
                            colorTxt.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                            colorPreview.BackColor = c;
                            isSyncing = false;
                        };
                        nudR.ValueChanged += rgbChanged;
                        nudG.ValueChanged += rgbChanged;
                        nudB.ValueChanged += rgbChanged;

                        // Hex → RGB + 预览
                        colorTxt.TextChanged += (s, e) =>
                        {
                            if (isSyncing) return;
                            isSyncing = true;
                            try
                            {
                                var c = ParseColor(colorTxt.Text);
                                nudR.Value = c.R;
                                nudG.Value = c.G;
                                nudB.Value = c.B;
                                colorPreview.BackColor = c;
                            }
                            catch { }
                            isSyncing = false;
                        };

                        // ColorDialog → 全部更新
                        btnPickColor.Click += (s, e) =>
                        {
                            using (var colorDialog = new ColorDialog())
                            {
                                colorDialog.Color = colorPreview.BackColor;
                                if (colorDialog.ShowDialog() == DialogResult.OK)
                                {
                                    isSyncing = true;
                                    var c = colorDialog.Color;
                                    nudR.Value = c.R;
                                    nudG.Value = c.G;
                                    nudB.Value = c.B;
                                    colorTxt.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                                    colorPreview.BackColor = c;
                                    isSyncing = false;
                                }
                            }
                        };

                        colorPanel.Controls.AddRange(new Control[] { rgbRow, hexRow });
                        inputCtrl = colorPanel;
                        break;

                    case "SoundData":
                    {
                        group.Height = 240;
                        inputCtrl = CreateSoundDataPanelFromValue(prop, prop.value);
                        break;
                    }

                    case "SoundDataArray":
                    {
                        var elements = (prop.value ?? "").Split(new[]{';'}, StringSplitOptions.RemoveEmptyEntries);
                        bool hasTabLabels = prop.tabLabels != null && prop.tabLabels.Length > 0;

                        if (hasTabLabels)
                        {
                            // 组类型：按 tabLabels 数量创建选项卡，显示本地化子类型名
                            int tabCount = prop.tabLabels.Length;
                            var tabCtrl = new TabControl { Width = 440, Height = 260, Name = "SoundDataArrayTabs" };
                            for (int i = 0; i < tabCount; i++)
                            {
                                string tabText = prop.tabLabels[i];
                                string soundValue = (i < elements.Length) ? elements[i] : "";
                                var page = new TabPage(tabText) { AccessibleName = tabText };
                                page.Controls.Add(CreateSoundDataPanelFromValue(prop, soundValue));
                                tabCtrl.TabPages.Add(page);
                            }
                            group.Height = 290;
                            inputCtrl = tabCtrl;
                        }
                        else
                        {
                            // 非组类型：不显示选项卡头，直接显示声音面板
                            group.Height = 240;
                            inputCtrl = CreateSoundDataPanelFromValue(prop, elements.Length > 0 ? elements[0] : "");
                        }
                        break;
                    }


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
                        var lblCharSearch = new Label { Text = "搜索 (Search):", Width = 80, Top = 5, Left = 0 };
                        var txtCharSearch = new TextBox { Width = 325, Top = 3, Left = 85, Name = "CharSearchBox" };
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
                        charListView.Columns.Add("角色名称 (Character)", 380);
                        
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
                        
                        // 确保选中状态生效，屏幕阅读器焦点跳到选中项
                        charListView.Refresh();
                        if (charListView.SelectedItems.Count > 0)
                        {
                            int selectedIdx = charListView.SelectedIndices[0];
                            charListView.Items[selectedIdx].Focused = true;
                            charListView.EnsureVisible(selectedIdx);
                            charListView.Focus();
                        }
                        
                        inputCtrl = charPanel;
                        break;

                    case "IntArray":
                    case "FloatArray":
                    case "StringArray":
                    {
                        var vals = (prop.value ?? "").Split(',').Select(s => s.Trim()).ToArray();
                        var arrPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.TopDown,
                            AutoSize = true,
                            WrapContents = false,
                            Padding = new Padding(0)
                        };
                        for (int i = 0; i < vals.Length; i++)
                        {
                            var row = new FlowLayoutPanel
                            {
                                FlowDirection = FlowDirection.LeftToRight,
                                AutoSize = true,
                                WrapContents = false,
                                Margin = new Padding(0, 2, 0, 2)
                            };
                            var lbl2 = new Label
                            {
                                Text = $"{i + 1}:",
                                Width = 30,
                                TextAlign = ContentAlignment.MiddleRight
                            };
                            var txt2 = new TextBox
                            {
                                Text = vals[i],
                                Width = 80,
                                Name = $"ArrayElement_{i}",
                                AccessibleName = $"{displayName} [{i + 1}]"
                            };
                            row.Controls.Add(lbl2);
                            row.Controls.Add(txt2);
                            arrPanel.Controls.Add(row);
                        }
                        inputCtrl = arrPanel;
                        break;
                    }

                    case "BoolArray":
                    {
                        var vals = (prop.value ?? "").Split(',').Select(s => s.Trim()).ToArray();
                        var arrPanel = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.TopDown,
                            AutoSize = true,
                            WrapContents = false,
                            Padding = new Padding(0)
                        };
                        for (int i = 0; i < vals.Length; i++)
                        {
                            var chk2 = new CheckBox
                            {
                                Text = $"{i + 1}",
                                Checked = vals[i] == "true",
                                Name = $"ArrayElement_{i}",
                                AccessibleName = $"{displayName} [{i + 1}]"
                            };
                            arrPanel.Controls.Add(chk2);
                        }
                        inputCtrl = arrPanel;
                        break;
                    }

                    case "EnumArray":
                    {
                        var vals = (prop.value ?? "").Split(',').Select(s => s.Trim()).ToArray();
                        int visibleCount = prop.rowCount > 0 ? prop.rowCount : prop.arrayLength;
                        var rawOpts = prop.options ?? new string[0];
                        var displayOpts = prop.localizedOptions ?? prop.options ?? new string[0];
                        var arrPanel2 = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.TopDown,
                            AutoSize = true,
                            WrapContents = false,
                            Padding = new Padding(0),
                            Tag = prop.value  // 存储原始完整值，供尾部补全
                        };
                        for (int i = 0; i < visibleCount; i++)
                        {
                            string rowLabel = (prop.rowNames != null && i < prop.rowNames.Length && !string.IsNullOrEmpty(prop.rowNames[i]))
                                ? prop.rowNames[i] : $"{i + 1}:";
                            var rowPanel2 = new FlowLayoutPanel
                            {
                                FlowDirection = FlowDirection.LeftToRight,
                                AutoSize = true,
                                WrapContents = false,
                                Margin = new Padding(0, 2, 0, 2)
                            };
                            var lbl2b = new Label
                            {
                                Text = rowLabel,
                                Width = 80,
                                TextAlign = ContentAlignment.MiddleRight
                            };
                            var combo2 = new ComboBox
                            {
                                DropDownStyle = ComboBoxStyle.DropDownList,
                                Width = 120,
                                Name = $"ArrayElement_{i}",
                                AccessibleName = $"{displayName} [{rowLabel}]",
                                Tag = rawOpts
                            };
                            combo2.Items.AddRange(displayOpts);
                            string curVal = i < vals.Length ? vals[i] : (rawOpts.Length > 0 ? rawOpts[0] : "");
                            int selIdx = Array.IndexOf(rawOpts, curVal);
                            combo2.SelectedIndex = selIdx >= 0 ? selIdx : 0;
                            rowPanel2.Controls.Add(lbl2b);
                            rowPanel2.Controls.Add(combo2);
                            arrPanel2.Controls.Add(rowPanel2);
                        }
                        inputCtrl = arrPanel2;
                        break;
                    }

                    case "Rooms":
                    {
                        var selectedRooms = (prop.value ?? "0").Split(',')
                            .Select(s => int.TryParse(s.Trim(), out int r) ? r : 0).ToHashSet();
                        int rc = prop.roomCount > 0 ? prop.roomCount : 4;
                        bool multiSelect = prop.roomsUsage == "ManyRooms" || prop.roomsUsage == "ManyRoomsAndOnTop";
                        if (multiSelect)
                        {
                            var roomPanel = new FlowLayoutPanel
                            {
                                FlowDirection = FlowDirection.TopDown,
                                AutoSize = true,
                                WrapContents = false,
                                Padding = new Padding(0)
                            };
                            for (int i = 0; i < rc; i++)
                            {
                                var roomChk = new CheckBox
                                {
                                    Text = (prop.localizedOptions != null && i < prop.localizedOptions.Length)
                                        ? prop.localizedOptions[i] : $"{i + 1}",
                                    Checked = selectedRooms.Contains(i),
                                    Name = $"RoomCheck_{i}",
                                    AccessibleName = $"{displayName} {((prop.localizedOptions != null && i < prop.localizedOptions.Length) ? prop.localizedOptions[i] : (i + 1).ToString())}"
                                };
                                roomPanel.Controls.Add(roomChk);
                            }
                            inputCtrl = roomPanel;
                        }
                        else
                        {
                            var combo = new ComboBox
                            {
                                DropDownStyle = ComboBoxStyle.DropDownList,
                                Width = 200,
                                Name = "RoomsCombo",
                                AccessibleName = displayName
                            };
                            for (int i = 0; i < rc; i++)
                                combo.Items.Add((prop.localizedOptions != null && i < prop.localizedOptions.Length)
                                        ? prop.localizedOptions[i] : $"{i + 1}");
                            combo.SelectedIndex = selectedRooms.Count > 0 ? Math.Min(selectedRooms.First(), rc - 1) : 0;
                            inputCtrl = combo;
                        }
                        break;
                    }

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
                    Text = "操作 (Actions)",
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
                {
                    var prop = _properties.FirstOrDefault(p => p.name == propName);
                    if (prop != null && (prop.type == "Enum" || prop.type == "Row"))
                        value = prop.value;
                    else if (prop != null && prop.type == "Rooms")
                        value = cmb.SelectedIndex.ToString();
                    else
                        value = cmb.SelectedItem?.ToString();
                }
                else if (ctrl is FlowLayoutPanel panel)
                {
                    // 处理 Rooms 多选（RoomCheck_N）
                    var roomIndices = new List<string>();
                    int ri = 0;
                    while (true)
                    {
                        var rc = panel.Controls.Find($"RoomCheck_{ri}", true).FirstOrDefault() as CheckBox;
                        if (rc == null) break;
                        if (rc.Checked) roomIndices.Add(ri.ToString());
                        ri++;
                    }
                    if (roomIndices.Count > 0)
                    {
                        value = string.Join(",", roomIndices);
                    }
                    else if (ri > 0) // Rooms panel but nothing checked
                    {
                        value = "";
                    }

                    // 处理数组类型（IntArray / FloatArray / BoolArray）
                    var arrayElems = new List<string>();
                    int ai = 0;
                    while (true)
                    {
                        var ec = panel.Controls.Find($"ArrayElement_{ai}", true).FirstOrDefault();
                        if (ec == null) break;
                        if (ec is TextBox et2) arrayElems.Add(et2.Text);
                        else if (ec is CheckBox ec2) arrayElems.Add(ec2.Checked ? "true" : "false");
                        else if (ec is ComboBox ec3 && ec3.Tag is string[] rawOpts2 && ec3.SelectedIndex >= 0)
                            arrayElems.Add(rawOpts2[ec3.SelectedIndex]);
                        ai++;
                    }
                    if (arrayElems.Count > 0)
                    {
                        // EnumArray：补全尾部未显示的元素（保留原始值）
                        if (panel.Tag is string originalFull)
                        {
                            var tail = originalFull.Split(',').Select(s => s.Trim()).ToArray();
                            for (int ti = arrayElems.Count; ti < tail.Length; ti++) arrayElems.Add(tail[ti]);
                        }
                        value = string.Join(",", arrayElems);
                    }
                    else
                    {
                        // 处理 Vector2, Float2, FloatExpression2, Color
                        var txtX = panel.Controls.Find("X", false).FirstOrDefault() as TextBox;
                        var txtY = panel.Controls.Find("Y", false).FirstOrDefault() as TextBox;
                        var colorTxt = panel.Controls.Find("ColorText", true).FirstOrDefault() as TextBox;

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
                }
                else if (ctrl is Panel ctrlPanel)
                {
                    // 检查是否是 Character 类型的 Panel
                    if (ctrlPanel.Controls.Find("CharacterValue", false).FirstOrDefault() is TextBox charValue)
                    {
                        // Character 类型
                        value = charValue.Text;
                        
                        // 同时获取自定义角色名称
                        var customNameCtrl = ctrlPanel.Controls.Find("CustomCharacterName", false).FirstOrDefault() as TextBox;
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
                        value = GetSoundDataPanelValue(ctrlPanel);
                    }
                }
                else if (ctrl is TabControl tabCtrl2)
                {
                    // SoundDataArray：逐 Tab 取值，以 ; 拼接
                    value = string.Join(";", tabCtrl2.TabPages.Cast<TabPage>()
                        .Select(tp => GetSoundDataPanelValue(
                            tp.Controls.OfType<Panel>().FirstOrDefault())));
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

        /// <summary>
        /// 从管道分隔的值字符串创建 SoundData 面板（用于数组元素）
        /// </summary>
        private Panel CreateSoundDataPanelFromValue(PropertyData prop, string value)
        {
            var parts = (value ?? "|||").Split('|');
            return CreateSoundDataPanel(prop,
                parts.Length > 0 ? parts[0] : "",
                parts.Length > 1 ? parts[1] : "100",
                parts.Length > 2 ? parts[2] : "100",
                parts.Length > 3 ? parts[3] : "0",
                parts.Length > 4 ? parts[4] : "0");
        }

        private Panel CreateSoundDataPanel(PropertyData prop, string filename, string volume, string pitch, string pan, string offset)
        {
            bool hasSoundOptions = prop.soundOptions != null && prop.soundOptions.Length > 0;
            bool canBrowseFile = prop.allowCustomFile;
            var soundPanel = new Panel { Width = 420, Height = 210, Top = 20, Left = 10 };
            var txtHiddenFilename = new TextBox { Text = filename, Width = 1, Top = 0, Left = 0, Name = "Filename", Visible = false };
            var txtOriginalFilename = new TextBox { Text = filename, Width = 1, Top = 0, Left = 0, Name = "OriginalFilename", Visible = false };
            soundPanel.Controls.Add(txtHiddenFilename);
            soundPanel.Controls.Add(txtOriginalFilename);
            var lblSearch = new Label { Text = "搜索 (Search):", Width = 65, Top = 5, Left = 0 };
            var txtSearch = new TextBox { Width = hasSoundOptions ? 200 : 320, Top = 3, Left = 70, Name = "SearchBox", AccessibleName = "搜索 (Search)" };
            if (canBrowseFile)
            {
                var btnBrowse = new Button { Text = "浏览文件... (Browse)", Width = 100, Top = 2, Left = 260, AccessibleName = "浏览文件 (Browse File)" };
                btnBrowse.Click += (s, e) =>
                {
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Filter = "音频文件 (Audio)|*.wav;*.ogg;*.mp3;*.aiff;*.aif|所有文件|*.*";
                        ofd.Title = prop.itsASong ? "选择歌曲文件 (Select Song File)" : "选择音效文件 (Select Sound File)";
                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            string selectedFile = ofd.FileName;
                            string fileName = System.IO.Path.GetFileName(selectedFile);
                            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                            var supportedExts = new[] { ".mp3", ".wav", ".ogg", ".aiff", ".aif" };
                            if (!supportedExts.Contains(ext))
                            {
                                MessageBox.Show($"不支持的音频格式: {ext}\n支持的格式: .mp3, .wav, .ogg, .aiff, .aif\n\nUnsupported audio format: {ext}\nSupported formats: .mp3, .wav, .ogg, .aiff, .aif", "格式错误 (Format Error)", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return;
                            }
                            if (!string.IsNullOrEmpty(_levelDirectory))
                            {
                                try
                                {
                                    string destPath = System.IO.Path.Combine(_levelDirectory, fileName);
                                    bool isSameFile = System.IO.Path.GetFullPath(selectedFile).Equals(System.IO.Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);
                                    if (!isSameFile)
                                    {
                                        if (System.IO.File.Exists(destPath))
                                        {
                                            var res = MessageBox.Show($"文件 '{fileName}' 已存在。是否覆盖？\n\nFile '{fileName}' already exists. Overwrite?", "文件已存在 (File Exists)", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                                            if (res == DialogResult.No) return;
                                        }
                                        System.IO.File.Copy(selectedFile, destPath, overwrite: true);
                                    }
                                }
                                catch (UnauthorizedAccessException) { MessageBox.Show("权限不足，无法复制文件\n\nInsufficient permissions to copy file", "权限错误 (Permission Error)", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                                catch (System.IO.IOException ex) { MessageBox.Show($"文件复制失败: {ex.Message}\n\nFile copy failed: {ex.Message}", "复制失败 (Copy Failed)", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                                catch (Exception ex) { MessageBox.Show($"未知错误: {ex.Message}\n\nUnknown error: {ex.Message}", "错误 (Error)", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                            }
                            txtHiddenFilename.Text = fileName;
                            var lv = soundPanel.Controls.Find("SoundListView", false).FirstOrDefault() as ListView;
                            if (lv != null)
                            {
                                foreach (ListViewItem item in lv.Items)
                                {
                                    if (item.Tag as string == fileName) { item.Selected = true; item.Focused = true; lv.EnsureVisible(lv.Items.IndexOf(item)); lv.Focus(); return; }
                                }
                                var newItem = new ListViewItem(fileName) { Tag = fileName };
                                lv.Items.Add(newItem);
                                newItem.Selected = true; newItem.Focused = true;
                                lv.EnsureVisible(lv.Items.Count - 1); lv.Focus();
                            }
                        }
                    }
                };
                soundPanel.Controls.Add(btnBrowse);
            }
            soundPanel.Controls.Add(lblSearch);
            soundPanel.Controls.Add(txtSearch);
            var listView = new ListView { Width = 405, Height = 120, Top = 30, Left = 5, View = View.Details, FullRowSelect = true, HideSelection = false, Name = "SoundListView", TabIndex = 0, AccessibleName = prop.displayName };
            listView.Columns.Add("音效名称 (Sound Name)", 400);
            if (hasSoundOptions)
            {
                if (prop.isNullable)
                {
                    var defaultItem = new ListViewItem("(使用轨道默认 / Track Default)") { Tag = "__track_default__" };
                    listView.Items.Add(defaultItem);
                    if (string.IsNullOrEmpty(filename)) defaultItem.Selected = true;
                }
                var rawSoundOptions = prop.soundOptions;
                var localizedSoundOptions = prop.localizedSoundOptions ?? rawSoundOptions;
                for (int i = 0; i < rawSoundOptions.Length; i++)
                {
                    var item = new ListViewItem(localizedSoundOptions[i]) { Tag = rawSoundOptions[i] };
                    listView.Items.Add(item);
                    if (rawSoundOptions[i] == filename) item.Selected = true;
                }
            }
            if (prop.itsASong && _internalSongs != null && _internalSongs.Count > 0)
            {
                foreach (var kvp in _internalSongs.OrderBy(x => x.Value))
                {
                    string songFn = kvp.Key; string songDn = kvp.Value;
                    bool already = listView.Items.Cast<ListViewItem>().Any(it => string.Equals(it.Tag as string, songFn, StringComparison.OrdinalIgnoreCase));
                    if (already) continue;
                    var lvItem = new ListViewItem(songDn) { Tag = songFn };
                    listView.Items.Add(lvItem);
                    if (songFn == filename) lvItem.Selected = true;
                }
            }
            if (_levelAudioFiles != null)
            {
                for (int i = 0; i < _levelAudioFiles.Length; i++)
                {
                    string af = _levelAudioFiles[i];
                    string adn = (_localizedLevelAudioFiles != null && i < _localizedLevelAudioFiles.Length) ? _localizedLevelAudioFiles[i] : af;
                    bool already = listView.Items.Cast<ListViewItem>().Any(it => string.Equals(it.Tag as string, af, StringComparison.OrdinalIgnoreCase));
                    if (already) continue;
                    var lvItem = new ListViewItem(adn) { Tag = af };
                    listView.Items.Add(lvItem);
                    if (af == filename) lvItem.Selected = true;
                }
            }
            if (!string.IsNullOrEmpty(filename))
            {
                bool found = listView.Items.Cast<ListViewItem>().Any(it => it.Tag as string == filename);
                if (!found) { var extItem = new ListViewItem(filename) { Tag = filename }; listView.Items.Add(extItem); extItem.Selected = true; }
            }
            listView.SelectedIndexChanged += (s, e) => { if (listView.SelectedItems.Count > 0) txtHiddenFilename.Text = listView.SelectedItems[0].Tag as string ?? listView.SelectedItems[0].Text; };
            listView.DoubleClick += (s, e) => { _isClosingByButton = true; OnOK?.Invoke(GetCurrentUpdates()); this.Close(); };
            listView.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space && listView.SelectedItems.Count > 0)
                {
                    e.Handled = true;
                    string rawSoundName = listView.SelectedItems[0].Tag as string;
                    if (rawSoundName == "__track_default__") return;
                    var volTxt = soundPanel.Controls.Find("Volume", false).FirstOrDefault() as TextBox;
                    var pitchTxt = soundPanel.Controls.Find("Pitch", false).FirstOrDefault() as TextBox;
                    var panTxt = soundPanel.Controls.Find("Pan", false).FirstOrDefault() as TextBox;
                    int volVal = 100, pitVal = 100, panVal = 0;
                    if (volTxt != null && int.TryParse(volTxt.Text, out int vv)) volVal = vv;
                    if (pitchTxt != null && int.TryParse(pitchTxt.Text, out int pv)) pitVal = pv;
                    if (panTxt != null && int.TryParse(panTxt.Text, out int pnv)) panVal = pnv;
                    FileIPC.SendPlaySoundRequest(rawSoundName, volVal, pitVal, panVal, prop.itsASong);
                }
            };
            var allSoundItems = listView.Items.Cast<ListViewItem>().ToList();
            txtSearch.TextChanged += (s, e) =>
            {
                var keyword = txtSearch.Text.ToLower();
                listView.BeginUpdate(); listView.Items.Clear();
                foreach (var item in allSoundItems) { if (string.IsNullOrEmpty(keyword) || item.Text.ToLower().Contains(keyword)) listView.Items.Add(item); }
                listView.EndUpdate();
                string cur = txtHiddenFilename.Text;
                foreach (ListViewItem item in listView.Items) { if (item.Tag as string == cur) { item.Selected = true; item.Focused = true; listView.EnsureVisible(listView.Items.IndexOf(item)); break; } }
            };
            soundPanel.Controls.Add(listView);
            listView.Refresh();
            if (listView.SelectedItems.Count > 0) { int si = listView.SelectedIndices[0]; listView.Items[si].Focused = true; listView.EnsureVisible(si); listView.Focus(); }
            soundPanel.Controls.Add(new Label { Text = "音量 (Volume):", Width = 80, Top = 155, Left = 0 });
            soundPanel.Controls.Add(new TextBox { Text = volume, Width = 60, Top = 153, Left = 85, Name = "Volume", AccessibleName = "音量 (Volume)" });
            soundPanel.Controls.Add(new Label { Text = "(0-300)", Width = 60, Top = 155, Left = 150 });
            soundPanel.Controls.Add(new Label { Text = "音调 (Pitch):", Width = 80, Top = 155, Left = 215 });
            soundPanel.Controls.Add(new TextBox { Text = pitch, Width = 60, Top = 153, Left = 295, Name = "Pitch", AccessibleName = "音调 (Pitch)" });
            soundPanel.Controls.Add(new Label { Text = "(0-300)", Width = 60, Top = 155, Left = 285 });
            soundPanel.Controls.Add(new Label { Text = "声道 (Pan):", Width = 65, Top = 180, Left = 0 });
            soundPanel.Controls.Add(new TextBox { Text = pan, Width = 60, Top = 178, Left = 70, Name = "Pan", AccessibleName = "声道 (Pan)" });
            soundPanel.Controls.Add(new Label { Text = "(-100~100)", Width = 65, Top = 180, Left = 135 });
            soundPanel.Controls.Add(new Label { Text = "偏移 (Offset):", Width = 75, Top = 180, Left = 205 });
            soundPanel.Controls.Add(new TextBox { Text = offset, Width = 60, Top = 178, Left = 285, Name = "Offset", AccessibleName = "偏移 (Offset)" });
            soundPanel.Controls.Add(new Label { Text = "毫秒 (ms)", Width = 55, Top = 180, Left = 350 });
            return soundPanel;
        }

        private string GetSoundDataPanelValue(Panel panel)
        {
            if (panel == null) return "";
            var txtFilename = panel.Controls.Find("Filename", false).FirstOrDefault() as TextBox;
            var txtVolume = panel.Controls.Find("Volume", false).FirstOrDefault() as TextBox;
            var txtPitch = panel.Controls.Find("Pitch", false).FirstOrDefault() as TextBox;
            var txtPan = panel.Controls.Find("Pan", false).FirstOrDefault() as TextBox;
            var txtOffset = panel.Controls.Find("Offset", false).FirstOrDefault() as TextBox;
            string fn = txtFilename?.Text ?? "";
            var lv = panel.Controls.Find("SoundListView", false).FirstOrDefault() as ListView;
            if (lv != null && lv.SelectedItems.Count > 0)
            {
                string tag = lv.SelectedItems[0].Tag as string;
                if (tag == "__track_default__") return "";
                if (!string.IsNullOrEmpty(fn))
                    return $"{fn}|{txtVolume?.Text ?? "100"}|{txtPitch?.Text ?? "100"}|{txtPan?.Text ?? "0"}|{txtOffset?.Text ?? "0"}";
                var txtOrig = panel.Controls.Find("OriginalFilename", false).FirstOrDefault() as TextBox;
                if (txtOrig != null && !string.IsNullOrEmpty(txtOrig.Text))
                    return $"{txtOrig.Text}|{txtVolume?.Text ?? "100"}|{txtPitch?.Text ?? "100"}|{txtPan?.Text ?? "0"}|{txtOffset?.Text ?? "0"}";
            }
            return "";
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
