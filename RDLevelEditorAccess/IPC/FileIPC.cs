using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RDLevelEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using Process = System.Diagnostics.Process;

namespace RDLevelEditorAccess.IPC
{
    public class FileIPC
    {
        private const string TempDirName = "temp";
        private const string SourceFileName = "source.json";
        private const string ResultFileName = "result.json";
        private const string HelperExeName = "RDEventEditorHelper.exe";

        private string _tempPath;
        private string _sourcePath;
        private string _resultPath;
        private LevelEvent_Base _currentEvent;
        private LevelEvent_MakeRow _currentRow;  // 当前编辑的轨道
        private int _currentRowIndex;  // 当前编辑的轨道索引
        private bool _isPolling;
        private Process _helperProcess;  // 当前运行的 Helper 进程（用于紧急终止）
        private string _sessionToken;  // 会话特征码
        private string _currentEditType = "event";  // 当前编辑类型: "event"、"row"、"condition" 等
        private MonoBehaviour _owner;  // 用于启动协程
        private bool _isPlayingSoundCoroutineRunning;  // 防止重入
        // 条件编辑专用
        private string _conditionalEditMode;  // "create" 或 "edit"
        private int _editingConditionalId;    // edit 模式时的目标条件 ID
        private LevelEvent_Base _conditionalTargetEvent;  // 新建时自动附加的目标事件

        /// <summary>
        /// 是否正在等待 Helper 返回结果
        /// </summary>
        public bool IsPolling => _isPolling;

        /// <summary>
        /// 条件新建/编辑完成后的回调，参数为条件 ID
        /// </summary>
        public Action<int> OnConditionalSaved;

        public void Initialize(MonoBehaviour owner)
        {
            _owner = owner;
            string gameDir = AppDomain.CurrentDomain.BaseDirectory;
            _tempPath = Path.Combine(gameDir, TempDirName);
            _sourcePath = Path.Combine(_tempPath, SourceFileName);
            _resultPath = Path.Combine(_tempPath, ResultFileName);

            if (!Directory.Exists(_tempPath))
            {
                Directory.CreateDirectory(_tempPath);
            }

            Debug.Log($"[FileIPC] 初始化完成，临时目录: {_tempPath}");
        }

        public void StartEditing(LevelEvent_Base levelEvent)
        {
            if (levelEvent == null) return;

            _currentEvent = levelEvent;
            _currentRow = null;         // 清除轨道引用，防止状态污染
            _currentEditType = "event"; // 明确设置为事件模式

            // 生成新的会话特征码
            _sessionToken = System.Guid.NewGuid().ToString();
            Debug.Log($"[FileIPC] 生成会话特征码: {_sessionToken}");

            Debug.Log($"[FileIPC] 开始编辑事件: {levelEvent.type}");

            var properties = ExtractProperties(levelEvent);

            var sourceData = new SourceData
            {
                eventType = levelEvent.type.ToString(),
                token = _sessionToken,
                properties = properties,
                levelAudioFiles = GetLevelAudioFiles(),
                levelDirectory = GetLevelDirectory()
            };

            // 为 levelAudioFiles 生成本地化名称（移除扩展名）
            if (sourceData.levelAudioFiles != null && sourceData.levelAudioFiles.Length > 0)
            {
                sourceData.localizedLevelAudioFiles = sourceData.levelAudioFiles.Select(filename =>
                    System.IO.Path.GetFileNameWithoutExtension(filename)
                ).ToArray();
            }

            // 如果有音乐属性（itsASong = true），添加内置音乐列表
            bool hasMusic = properties.Any(p => p.type == "SoundData" && p.itsASong);
            if (hasMusic)
            {
                sourceData.internalSongs = GetInternalSongs();
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                string json = JsonSerializer.Serialize(sourceData, options);
                File.WriteAllText(_sourcePath, json);
                Debug.Log($"[FileIPC] 已写入 source.json: {json.Length} 字符");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}");
                return;
            }

            LaunchHelper();

            LockKeyboard();

            _isPolling = true;
        }

        /// <summary>
        /// 开始编辑轨道
        /// </summary>
        public void StartRowEditing(LevelEvent_MakeRow rowData, int rowIndex)
        {
            if (rowData == null) return;

            _currentRow = rowData;
            _currentRowIndex = rowIndex;
            _currentEvent = null;  // 清除事件引用
            _currentEditType = "row";
            
            // 生成新的会话特征码
            _sessionToken = System.Guid.NewGuid().ToString();
            Debug.Log($"[FileIPC] 生成会话特征码: {_sessionToken}");

            Debug.Log($"[FileIPC] 开始编辑轨道: 索引 {rowIndex}, 角色 {rowData.character}");

            var properties = ExtractProperties(rowData);

            var sourceData = new SourceData
            {
                editType = "row",
                eventType = "MakeRow",
                token = _sessionToken,
                properties = properties,
                levelAudioFiles = GetLevelAudioFiles(),
                levelDirectory = GetLevelDirectory()
            };

            // 为 levelAudioFiles 生成本地化名称（移除扩展名）
            if (sourceData.levelAudioFiles != null && sourceData.levelAudioFiles.Length > 0)
            {
                sourceData.localizedLevelAudioFiles = sourceData.levelAudioFiles.Select(filename =>
                    System.IO.Path.GetFileNameWithoutExtension(filename)
                ).ToArray();
            }

            // 如果有音乐属性（itsASong = true），添加内置音乐列表
            bool hasMusic = properties.Any(p => p.type == "SoundData" && p.itsASong);
            if (hasMusic)
            {
                sourceData.internalSongs = GetInternalSongs();
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                string json = JsonSerializer.Serialize(sourceData, options);
                File.WriteAllText(_sourcePath, json);
                Debug.Log($"[FileIPC] 已写入 source.json (轨道编辑): {json.Length} 字符");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}");
                return;
            }

            LaunchHelper();
            LockKeyboard();
            _isPolling = true;
        }

        private void LockKeyboard()
        {
            try
            {
                var editor = scnEditor.instance;
                if (editor == null || editor.eventSystem == null) return;

                var go = new GameObject("RDMods_LockInput");
                var inputField = go.AddComponent<UnityEngine.UI.InputField>();
                go.transform.SetParent(editor.transform);
                
                editor.eventSystem.SetSelectedGameObject(go);
                
                Debug.Log("[FileIPC] 已锁定键盘 (创建隐藏 InputField)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 锁定键盘失败: {ex.Message}");
            }
        }

        private void UnlockKeyboard()
        {
            try
            {
                var editor = scnEditor.instance;
                if (editor == null || editor.eventSystem == null) return;

                var selected = editor.eventSystem.currentSelectedGameObject;
                if (selected != null && selected.name == "RDMods_LockInput")
                {
                    UnityEngine.Object.Destroy(selected);
                }
                
                Debug.Log("[FileIPC] 已解锁键盘");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 解锁键盘失败: {ex.Message}");
            }
        }

        public void Update()
        {
            if (!_isPolling) return;

            // NEW: 处理validateVisibility请求（与result.json处理独立，不中断轮询和键盘锁定）
            PollPropertyValidationRequests();

            // NEW: 处理停止声音请求（单向通信，不影响主流程）
            PollStopSoundRequests();

            // NEW: 处理播放声音请求（单向通信，不影响主流程）- 改为协程
            string playSoundRequestPath = Path.Combine(_tempPath, "playSoundRequest.json");
            if (File.Exists(playSoundRequestPath) && _owner != null && !_isPlayingSoundCoroutineRunning)
            {
                _owner.StartCoroutine(PollPlaySoundRequestsCoroutine());
            }

            if (File.Exists(_resultPath))
            {
                try
                {
                    string json = File.ReadAllText(_resultPath);
                    Debug.Log($"[FileIPC] 已读取 result.json");
                    
                    // 先解析验证特征码
                    var options = new JsonSerializerOptions { IncludeFields = true };
                    var resultData = JsonSerializer.Deserialize<ResultData>(json, options);
                    
                    // 特征码验证
                    if (resultData?.token != _sessionToken)
                    {
                        Debug.LogWarning($"[FileIPC] 特征码不匹配，期望: {_sessionToken}，实际: {resultData?.token}，删除文件继续轮询");
                        File.Delete(_resultPath);
                        return; // 继续轮询，不停止不解锁
                    }
                    
                    Debug.Log($"[FileIPC] 特征码验证通过: {_sessionToken}");
                    File.Delete(_resultPath);

                    ProcessResult(json);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FileIPC] 读取 result.json 失败: {ex.Message}");
                }
                
                // 只有在验证成功或处理完成后才停止轮询和解锁
                _isPolling = false;
                UnlockKeyboard();
            }
        }

        private void ProcessResult(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            {
                Debug.Log("[FileIPC] 用户取消编辑");
                return;
            }

            Debug.Log($"[FileIPC] 解析 result.json: {json}");

            try
            {
                var options = new JsonSerializerOptions { IncludeFields = true };
                var resultData = JsonSerializer.Deserialize<ResultData>(json, options);
                Debug.Log($"[FileIPC] 解析结果: action={resultData?.action}, updates={(resultData?.updates != null ? resultData.updates.Count : 0)}项");

                if (resultData?.action == "cancel")
                {
                    Debug.Log("[FileIPC] 用户取消编辑");
                    return;
                }

                // 处理操作按钮执行
                if (resultData?.action == "execute" && !string.IsNullOrEmpty(resultData.methodName))
                {
                    ExecuteButtonAction(_currentEvent, resultData.methodName);
                    return;
                }

                // 处理 BPM 计算器请求：先应用更改，再触发原生 BPM 计算器
                if (resultData?.action == "bpmCalculator")
                {
                    if (resultData.updates != null && _currentEvent != null)
                    {
                        ApplyUpdates(_currentEvent, resultData.updates);
                        Debug.Log("[FileIPC] 已应用事件更改（BPM计算器前）");
                    }
                    TriggerBPMCalculator();
                    return;
                }

                if (_currentEditType == "condition")
                {
                    ApplyConditionalResult(resultData);
                }
                else if (resultData.updates != null)
                {
                    // 根据编辑类型选择处理方式
                    if (_currentEditType == "row" && _currentRow != null)
                    {
                        ApplyUpdates(_currentRow, resultData.updates);
                        Debug.Log("[FileIPC] 已应用轨道更改");
                    }
                    else if (_currentEditType == "settings")
                    {
                        ApplySettingsUpdates(resultData.updates);
                    }
                    else if (_currentEditType == "jump")
                    {
                        ApplyJumpToCursorUpdates(resultData.updates);
                    }
                    else if (_currentEditType == "chainName")
                    {
                        ApplyChainNameResult(resultData.updates);
                    }
                    else if (_currentEvent != null)
                    {
                        ApplyUpdates(_currentEvent, resultData.updates);
                        Debug.Log("[FileIPC] 已应用事件更改");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 解析 result.json 失败: {ex.Message}");
            }
        }

        // NEW: 处理Helper的属性验证请求
        private void PollPropertyValidationRequests()
        {
            string validationPath = Path.Combine(_tempPath, "validateVisibility.json");
            if (!File.Exists(validationPath)) return;

            try
            {
                string json = File.ReadAllText(validationPath);
                var options = new JsonSerializerOptions { IncludeFields = true };
                var request = JsonSerializer.Deserialize<PropertyUpdateRequest>(json, options);

                // 获取当前正被编辑的事件（使用selectedControl）
                var currentEvent = scnEditor.instance?.selectedControl?.levelEvent;
                if (currentEvent == null)
                {
                    // 编辑轨道/元数据时没有选中事件，返回空响应
                    // 不能只删请求不写响应，否则 Helper 会等待 5 秒超时
                    var emptyResponse = new PropertyUpdateResponse
                    {
                        token = request?.token ?? "",
                        visibilityChanges = new Dictionary<string, bool>()
                    };
                    string emptyResponsePath = Path.Combine(_tempPath, "validateVisibilityResponse.json");
                    File.WriteAllText(emptyResponsePath, JsonSerializer.Serialize(emptyResponse, options));
                    File.Delete(validationPath);
                    return;
                }

                var response = HandlePropertyUpdateRequest(request, currentEvent);

                string responsePath = Path.Combine(_tempPath, "validateVisibilityResponse.json");
                string responseJson = JsonSerializer.Serialize(response, options);
                File.WriteAllText(responsePath, responseJson);

                File.Delete(validationPath);  // 处理完删除请求
                Debug.Log($"[FileIPC] 已处理enableIf验证请求");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] Failed to process visibility validation: {ex.Message}");
                // 错误时也删除文件，避免反复尝试
                try { File.Delete(validationPath); } catch { }
            }
        }

        /// <summary>
        /// 检查文件名是否包含音频文件扩展名
        /// </summary>
        private static bool HasAudioFileExtension(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return false;

            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext == ".ogg" || ext == ".wav" || ext == ".mp3" || ext == ".aiff";
        }

        /// <summary>
        /// 处理 Helper 的播放声音请求（协程版本，支持外部音频）
        /// </summary>
        private System.Collections.IEnumerator PollPlaySoundRequestsCoroutine()
        {
            _isPlayingSoundCoroutineRunning = true;
            string requestPath = Path.Combine(_tempPath, "playSoundRequest.json");

            if (!File.Exists(requestPath))
            {
                _isPlayingSoundCoroutineRunning = false;
                yield break;
            }

            PlaySoundRequest request = null;
            string json = null;

            // 读取和解析请求（不能在 try-catch 中 yield）
            try
            {
                json = File.ReadAllText(requestPath);
                var options = new JsonSerializerOptions { IncludeFields = true };
                request = JsonSerializer.Deserialize<PlaySoundRequest>(json, options);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 读取播放声音请求失败: {ex.Message}");
                try { File.Delete(requestPath); } catch { }
                _isPlayingSoundCoroutineRunning = false;
                yield break;
            }

            // 验证 token
            if (request?.token != _sessionToken)
            {
                Debug.LogWarning($"[FileIPC] 播放声音请求 token 不匹配，忽略");
                try { File.Delete(requestPath); } catch { }
                _isPlayingSoundCoroutineRunning = false;
                yield break;
            }

            // 播放声音
            float volume = request.volume / 100f;
            float pitch = request.pitch / 100f;
            float pan = request.pan / 100f;

            // 检查是否为外部音频文件（关卡目录）
            bool isExternalAudio = HasAudioFileExtension(request.soundName);

            if (isExternalAudio)
            {
                // 外部音频处理流程 - 使用辅助协程
                yield return LoadAndPlayExternalAudioCoroutine(request.soundName, volume, pitch, pan);
            }
            else
            {
                // 内置音频处理流程（保持原有逻辑）
                PlayInternalAudio(request.soundName, request.itsASong, volume, pitch, pan);
            }

            // 删除请求文件
            try { File.Delete(requestPath); } catch { }
            _isPlayingSoundCoroutineRunning = false;
        }

        /// <summary>
        /// 加载并播放外部音频（协程）
        /// </summary>
        private System.Collections.IEnumerator LoadAndPlayExternalAudioCoroutine(string soundName, float volume, float pitch, float pan)
        {
            Debug.Log($"[FileIPC] 检测到外部音频文件: {soundName}");

            // 获取关卡目录路径
            var editorUtilsType = Type.GetType("RDLevelEditor.RDEditorUtils, Assembly-CSharp");
            if (editorUtilsType == null)
            {
                Debug.LogError("[FileIPC] 未找到 RDEditorUtils 类型");
                yield break;
            }

            var getLevelDirMethod = editorUtilsType.GetMethod("GetCurrentLevelFolderPath",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (getLevelDirMethod == null)
            {
                Debug.LogError("[FileIPC] 未找到 GetCurrentLevelFolderPath 方法");
                yield break;
            }

            string levelDir = (string)getLevelDirMethod.Invoke(null, null);
            if (string.IsNullOrEmpty(levelDir))
            {
                Debug.LogWarning("[FileIPC] 关卡目录路径为空");
                yield break;
            }

            string fullPath = Path.Combine(levelDir, soundName);
            Debug.Log($"[FileIPC] 外部音频完整路径: {fullPath}");

            // 检查文件是否存在
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[FileIPC] 音频文件不存在: {fullPath}");
                yield break;
            }

            // 异步加载外部音频
            Debug.Log($"[FileIPC] 正在加载外部音频: {soundName}");

            // 获取 Singleton<AudioManager> 类型
            var singletonType = Type.GetType("Singleton`1, Assembly-CSharp");
            if (singletonType == null)
            {
                Debug.LogError("[FileIPC] 未找到 Singleton 类型");
                yield break;
            }

            var audioManagerType = Type.GetType("AudioManager, Assembly-CSharp");
            if (audioManagerType == null)
            {
                Debug.LogError("[FileIPC] 未找到 AudioManager 类型");
                yield break;
            }

            // 构造 Singleton<AudioManager> 类型
            var singletonAudioManagerType = singletonType.MakeGenericType(audioManagerType);
            var instanceProp = singletonAudioManagerType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (instanceProp == null)
            {
                Debug.LogError("[FileIPC] 未找到 Singleton<AudioManager>.Instance 属性");
                yield break;
            }

            var audioManagerInstance = instanceProp.GetValue(null);
            if (audioManagerInstance == null)
            {
                Debug.LogError("[FileIPC] AudioManager.Instance 为 null");
                yield break;
            }

            var loadMethod = audioManagerType.GetMethod("FindOrLoadAudioClipExternal",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (loadMethod == null)
            {
                Debug.LogError("[FileIPC] 未找到 FindOrLoadAudioClipExternal 方法");
                yield break;
            }

            var loadCoroutine = (System.Collections.IEnumerator)loadMethod.Invoke(audioManagerInstance, new object[] { fullPath });
            yield return loadCoroutine;

            // 获取加载结果
            var resultType = Type.GetType("RDAudioLoadResult, Assembly-CSharp");
            if (resultType == null)
            {
                Debug.LogError("[FileIPC] 未找到 RDAudioLoadResult 类型");
                yield break;
            }

            var result = loadCoroutine.Current;
            var typeField = resultType.GetField("type");
            var loadTypeEnum = typeField.GetValue(result);

            // 检查加载是否成功 (RDAudioLoadType.SuccessExternalClipLoaded = 0)
            if ((int)loadTypeEnum != 0)
            {
                Debug.LogError($"[FileIPC] 外部音频加载失败，错误类型: {loadTypeEnum}");
                yield break;
            }

            Debug.Log($"[FileIPC] 外部音频加载成功: {soundName}");

            // 使用缓存的 clip 名称播放
            string cachedClipName = Path.GetFileName(soundName) + "*external";
            Debug.Log($"[FileIPC] 播放外部音频，缓存名称: {cachedClipName}, 音量={volume}, 音调={pitch}, 声像={pan}");

            var playImmediatelyMethod = audioManagerType.GetMethod("PlayImmediately",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (playImmediatelyMethod != null)
            {
                // PlayImmediately(string snd, float volume, AudioMixerGroup group, float pitch, float pan, bool ignoreListenerPause, bool levelEditor, bool dontActuallyPlayIt)
                playImmediatelyMethod.Invoke(null, new object[] { cachedClipName, volume, null, pitch, pan, true, true, false });
            }
            else
            {
                Debug.LogWarning("[FileIPC] 未找到 AudioManager.PlayImmediately 方法");
            }
        }

        /// <summary>
        /// 播放内置音频（非协程）
        /// </summary>
        private void PlayInternalAudio(string soundName, bool itsASong, float volume, float pitch, float pan)
        {
            // 如果是音效（不是音乐），需要添加 "snd" 前缀（游戏内部约定）
            if (!itsASong && !soundName.StartsWith("snd") && !soundName.Contains("."))
            {
                soundName = "snd" + soundName;
            }

            Debug.Log($"[FileIPC] 播放内置声音: {soundName}, 音量={volume}, 音调={pitch}, 声像={pan}");

            // 使用反射调用 scrConductor.PlayImmediatelyLevelEditor
            var conductorType = Type.GetType("scrConductor, Assembly-CSharp");
            if (conductorType != null)
            {
                var playMethod = conductorType.GetMethod("PlayImmediatelyLevelEditor",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (playMethod != null)
                {
                    playMethod.Invoke(null, new object[] { soundName, null, volume, pitch });
                }
                else
                {
                    Debug.LogWarning("[FileIPC] 未找到 PlayImmediatelyLevelEditor 方法");
                }
            }
            else
            {
                Debug.LogWarning("[FileIPC] 未找到 scrConductor 类型");
            }
        }

        /// <summary>
        /// 轮询停止声音请求
        /// </summary>
        private void PollStopSoundRequests()
        {
            string requestPath = Path.Combine(_tempPath, "stopSoundRequest.json");
            if (!File.Exists(requestPath)) return;

            try
            {
                string json = File.ReadAllText(requestPath);
                var options = new JsonSerializerOptions { IncludeFields = true };
                var request = JsonSerializer.Deserialize<StopSoundRequest>(json, options);

                // 验证 token
                if (request?.token != _sessionToken)
                {
                    Debug.LogWarning($"[FileIPC] 停止声音请求 token 不匹配，忽略");
                    File.Delete(requestPath);
                    return;
                }

                Debug.Log($"[FileIPC] 停止所有预览声音");

                // 使用反射访问 AudioManager.liveSources 列表并停止所有声音
                // 不能使用 StopAllSounds 方法，因为它在遍历时删除元素有 bug
                var audioManagerType = Type.GetType("AudioManager, Assembly-CSharp");
                if (audioManagerType != null)
                {
                    var singletonType = typeof(Singleton<>).MakeGenericType(audioManagerType);
                    var instanceProp = singletonType.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (instanceProp != null)
                    {
                        var instance = instanceProp.GetValue(null);
                        if (instance != null)
                        {
                            // 获取 liveSources 字段（public 字段）
                            var liveSourcesField = audioManagerType.GetField("liveSources",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (liveSourcesField != null)
                            {
                                var liveSources = liveSourcesField.GetValue(instance) as System.Collections.IList;
                                if (liveSources != null)
                                {
                                    Debug.Log($"[FileIPC] 找到 {liveSources.Count} 个 AudioSource");

                                    // 先收集所有要停止的 AudioSource（避免在遍历时修改列表）
                                    var sourcesToStop = new System.Collections.Generic.List<AudioSource>();
                                    foreach (var source in liveSources)
                                    {
                                        if (source is AudioSource audioSource && audioSource != null)
                                        {
                                            sourcesToStop.Add(audioSource);
                                        }
                                    }

                                    // 停止并销毁所有 AudioSource
                                    foreach (var audioSource in sourcesToStop)
                                    {
                                        audioSource.Stop();
                                        UnityEngine.Object.Destroy(audioSource.gameObject);
                                    }

                                    // 清空列表
                                    liveSources.Clear();
                                }
                                else
                                {
                                    Debug.LogWarning("[FileIPC] liveSources 为 null");
                                }
                            }
                            else
                            {
                                Debug.LogWarning("[FileIPC] 未找到 liveSources 字段");
                            }
                        }
                        else
                        {
                            Debug.LogWarning("[FileIPC] AudioManager 实例为 null");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[FileIPC] 未找到 Singleton.Instance 属性");
                    }
                }
                else
                {
                    Debug.LogWarning("[FileIPC] 未找到 AudioManager 类型");
                }

                // 删除请求文件
                File.Delete(requestPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 停止声音请求失败: {ex.Message}");
                // 错误时也删除文件，避免反复尝试
                try { File.Delete(requestPath); } catch { }
            }
        }

        // NEW: 处理属性更新请求，执行enableIf判断
        private PropertyUpdateResponse HandlePropertyUpdateRequest(PropertyUpdateRequest request, LevelEvent_Base currentEvent)
        {
            if (currentEvent == null)
                return new PropertyUpdateResponse { token = request.token, visibilityChanges = new Dictionary<string, bool>() };

            var visibilityChanges = new Dictionary<string, bool>();

            // 应用Helper发来的值更新到event对象（临时，不保存）
            foreach (var kvp in request.updates)
            {
                string propName = kvp.Key;
                string newValue = kvp.Value;

                try
                {
                    var prop = currentEvent.GetType().GetProperty(propName);
                    if (prop != null)
                    {
                        var convertedValue = ConvertStringToPropertyValue(newValue, prop.PropertyType);
                        prop.SetValue(currentEvent, convertedValue);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FileIPC] Failed to apply property {propName}: {ex.Message}");
                }
            }

            // 对所有属性重新评估enableIf条件
            if (currentEvent.info != null && currentEvent.info.propertiesInfo != null)
            {
                foreach (var property in currentEvent.info.propertiesInfo)
                {
                    if (property != null && property.enableIf != null)
                    {
                        try
                        {
                            bool shouldShow = property.enableIf(currentEvent);

                            // 仅返回**状态发生变化**的属性（优化网络消息）
                            var existingProp = request.currentProperties
                                .FirstOrDefault(p => p.name == property.propertyInfo.Name);
                            if (existingProp != null && existingProp.isVisible != shouldShow)
                            {
                                visibilityChanges[property.propertyInfo.Name] = shouldShow;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[FileIPC] Failed to evaluate enableIf for {property.propertyInfo.Name}: {ex.Message}");
                        }
                    }
                }
            }

            // SetGameSound 专用：soundType 变化时返回 sounds 属性的新标签页名称
            var tabLabelsChanges = new Dictionary<string, string[]>();
            if (request.updates.ContainsKey("soundType") && currentEvent is LevelEvent_SetGameSound sgEvent2)
            {
                var soundType2 = sgEvent2.soundType;
                string[] newTabLabels = null;
                if (RDEditorUtils.SoundTypeIsGroup(soundType2))
                {
                    var groupKey2 = soundType2 switch
                    {
                        GameSoundType.PulseSoundHoldP2 => GameSoundType.PulseSoundHold,
                        GameSoundType.ClapSoundHoldP2 => GameSoundType.ClapSoundHold,
                        _ => soundType2,
                    };
                    var groups2 = RDEditorConstants.gameSoundGroups;
                    if (groups2.ContainsKey(groupKey2))
                    {
                        newTabLabels = groups2[groupKey2].Select(st =>
                        {
                            string localized = RDString.GetEnumValue(st);
                            return !string.IsNullOrEmpty(localized) ? StripRichTextTags(localized) : st.ToString();
                        }).ToArray();
                    }
                }
                tabLabelsChanges["sounds"] = newTabLabels;
            }

            // 重算硬编码面板按钮的可见性
            if (HardcodedButtons.TryGetValue(currentEvent.type, out var hardDefs))
            {
                foreach (var def in hardDefs)
                {
                    try
                    {
                        bool shouldShow = def.isVisible(currentEvent);
                        var existingProp = request.currentProperties
                            ?.FirstOrDefault(p => p.name == def.methodName);
                        if (existingProp != null && existingProp.isVisible != shouldShow)
                            visibilityChanges[def.methodName] = shouldShow;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FileIPC] 硬编码按钮可见性重算失败 {def.methodName}: {ex.Message}");
                    }
                }
            }

            return new PropertyUpdateResponse
            {
                token = request.token,
                visibilityChanges = visibilityChanges,
                tabLabelsChanges = tabLabelsChanges.Count > 0 ? tabLabelsChanges : null
            };
        }

        // NEW: 将字符串值转换为目标类型
        private object ConvertStringToPropertyValue(string value, Type targetType)
        {
            if (string.IsNullOrEmpty(value)) return null;

            try
            {
                if (targetType == typeof(string)) return value;
                if (targetType == typeof(bool)) return value == "true";
                if (targetType == typeof(int)) return int.Parse(value);
                if (targetType == typeof(float)) return float.Parse(value);
                if (targetType == typeof(double)) return double.Parse(value);
                if (targetType.IsEnum) return Enum.Parse(targetType, value);
            }
            catch { }

            return value;
        }

        private void ExecuteButtonAction(LevelEvent_Base ev, string methodName)
        {
            if (ev == null || string.IsNullOrEmpty(methodName))
            {
                Debug.LogWarning("[FileIPC] 无法执行操作：事件或方法名为空");
                return;
            }

            try
            {
                // 优先在 LevelEvent 上查找（反射型 ButtonAttribute 按钮）
                var method = ev.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    Debug.Log($"[FileIPC] 执行事件操作: {methodName}");
                    method.Invoke(ev, null);
                    Debug.Log($"[FileIPC] 操作执行完成: {methodName}");
                    return;
                }

                // 回退到当前 InspectorPanel（硬编码按钮）
                var panel = scnEditor.instance?.inspectorPanelManager?.GetCurrent();
                if (panel == null)
                {
                    Debug.LogError($"[FileIPC] 找不到方法 '{methodName}'（事件上不存在），且当前面板为空");
                    return;
                }

                var panelMethod = panel.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (panelMethod == null)
                {
                    Debug.LogError($"[FileIPC] 找不到方法 '{methodName}'（在事件和面板上均未找到）");
                    return;
                }

                Debug.Log($"[FileIPC] 执行面板操作: {methodName} on {panel.GetType().Name}");
                panelMethod.Invoke(panel, null);
                Debug.Log($"[FileIPC] 面板操作执行完成: {methodName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 执行操作 {methodName} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发原生 BPM 计算器
        /// </summary>
        private void TriggerBPMCalculator()
        {
            try
            {
                if (scnEditor.instance == null)
                {
                    Debug.LogError("[FileIPC] 无法触发BPM计算器：编辑器实例为空");
                    return;
                }

                var currentPanel = scnEditor.instance.inspectorPanelManager.GetCurrent();
                if (currentPanel?.properties == null)
                {
                    Debug.LogError("[FileIPC] 无法触发BPM计算器：当前面板或属性列表为空");
                    return;
                }

                foreach (var property in currentPanel.properties)
                {
                    if (property.control is PropertyControl_BPMCalculator bpmCtl)
                    {
                        if (bpmCtl.bpmCalculator.currentInspectorPanel == null)
                            bpmCtl.bpmCalculator.currentInspectorPanel = currentPanel;

                        bpmCtl.bpmCalculator.Initialize();
                        Debug.Log("[FileIPC] 已触发原生BPM计算器");
                        _owner.StartCoroutine(DelayedBPMCalcHint());
                        return;
                    }
                }

                Debug.LogWarning("[FileIPC] 未找到BPM计算器控件");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 触发BPM计算器失败: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator DelayedBPMCalcHint()
        {
            yield return new UnityEngine.WaitForSeconds(0.5f);
            Narration.Say(RDString.Get("eam.bpmcalc.hint"), NarrationCategory.Navigation);
        }

        private void ApplyUpdates(LevelEvent_Base ev, Dictionary<string, string> updates)
        {
            if (ev == null || updates == null) return;

            var info = ev.info;
            if (info == null) return;

            // ★ 关键修复：将SaveStateScope移到属性修改之前
            // 这样SaveState()会在修改前被调用，记录undo点，修改过程中changingState > 0
            // 修改完成后状态准确，防止属性被还原
            if (scnEditor.instance != null)
            {
                try
                {
                    using (new SaveStateScope())
                    {
                        Debug.Log("[FileIPC] 进入SaveStateScope保存事件修改");

                        foreach (var update in updates)
                        {
                            string key = update.Key;
                            string strVal = update.Value;

                            try
                            {
                                // 首先尝试应用基础属性（bar, beat, y, row, active, tag, tagRunNormally）
                                if (ApplyBaseProperty(ev, key, strVal))
                                {
                                    continue; // 基础属性已处理，跳过
                                }

                                // 处理事件特有属性
                                var propInfo = info.propertiesInfo.FirstOrDefault(p => p.propertyInfo.Name == key);
                                if (propInfo == null)
                                {
                                    // 特殊处理：MakeRow 的字段（不在 propertiesInfo 中）
                                    if (ev is LevelEvent_MakeRow row)
                                    {
                                        if (ApplyMakeRowSpecialField(row, key, strVal))
                                        {
                                            continue; // 已处理特殊字段
                                        }
                                    }

                                    Debug.LogWarning($"[FileIPC] 未找到属性: {key}");
                                    continue;
                                }

                                Debug.Log($"[FileIPC] 处理属性 {key}: propInfo类型={propInfo.GetType().Name}, 值={strVal?.Substring(0, Math.Min(50, strVal?.Length ?? 0)) ?? "null"}");

                                object valToSet = null;

                                if (propInfo is IntPropertyInfo) valToSet = int.Parse(strVal);
                                else if (propInfo is FloatPropertyInfo) valToSet = float.Parse(strVal);
                                else if (propInfo is BoolPropertyInfo) valToSet = strVal == "true";
                                else if (propInfo is StringPropertyInfo) valToSet = strVal;
                                else if (propInfo is EnumPropertyInfo enumProp) valToSet = Enum.Parse(enumProp.enumType, strVal);
                                else if (propInfo is Vector2PropertyInfo)
                                {
                                    // 解析 "x,y" 格式
                                    var parts = strVal.Split(',');
                                    if (parts.Length == 2 &&
                                        float.TryParse(parts[0], out float vx) &&
                                        float.TryParse(parts[1], out float vy))
                                    {
                                        valToSet = new UnityEngine.Vector2(vx, vy);
                                    }
                                }
                                else if (propInfo is ColorPropertyInfo)
                                {
                                    // 使用 ColorOrPalette.FromString 解析颜色
                                    var colorType = Type.GetType("RDLevelEditor.ColorOrPalette");
                                    if (colorType != null)
                                    {
                                        var fromStringMethod = colorType.GetMethod("FromString", new[] { typeof(string) });
                                        if (fromStringMethod != null)
                                        {
                                            valToSet = fromStringMethod.Invoke(null, new object[] { strVal });
                                        }
                                    }
                                }
                                else if (propInfo is Float2PropertyInfo)
                                {
                                    // 解析 "x,y" 格式
                                    var parts = strVal.Split(',');
                                    if (parts.Length == 2 &&
                                        float.TryParse(parts[0], out float fx) &&
                                        float.TryParse(parts[1], out float fy))
                                    {
                                        var float2Type = Type.GetType("RDLevelEditor.Float2");
                                        if (float2Type != null)
                                        {
                                            valToSet = Activator.CreateInstance(float2Type, fx, fy);
                                        }
                                    }
                                }
                                else if (propInfo is FloatExpressionPropertyInfo)
                                {
                                    // 使用 RDEditorUtils.DecodeFloatExpression 解析表达式
                                    valToSet = ParseFloatExpression(strVal);
                                }
                                else if (propInfo is FloatExpression2PropertyInfo)
                                {
                                    // 解析 "x,y" 格式的表达式
                                    var parts = strVal.Split(',');
                                    string xExpr = parts.Length > 0 ? parts[0].Trim() : "";
                                    string yExpr = parts.Length > 1 ? parts[1].Trim() : "";

                                    var xVal = ParseFloatExpression(xExpr);
                                    var yVal = ParseFloatExpression(yExpr);

                                    var floatExpr2Type = Type.GetType("RDLevelEditor.FloatExpression2");
                                    if (floatExpr2Type != null && xVal != null && yVal != null)
                                    {
                                        valToSet = Activator.CreateInstance(floatExpr2Type, xVal, yVal);
                                    }
                                }
                                else if (propInfo is SoundDataPropertyInfo ||
                                         (propInfo is NullablePropertyInfo nullableProp &&
                                          nullableProp.underlyingPropertyInfo is SoundDataPropertyInfo))
                                {
                                    Debug.Log($"[FileIPC] ★ 接收 SoundData: key={key}, strVal=\"{strVal}\" , 是否可空: {propInfo is NullablePropertyInfo}");

                                    // 处理空字符串 -> 设置为 null（如果是可空类型）
                                    if (string.IsNullOrEmpty(strVal) && propInfo is NullablePropertyInfo)
                                    {
                                        Debug.Log($"[FileIPC] ★ 设置为 null (strVal为空)");
                                        valToSet = null;
                                    }
                                    else
                                    {
                                        // 解析 "filename|volume|pitch|pan|offset" 格式
                                        var parts = strVal.Split('|');
                                        string filename = parts.Length > 0 ? parts[0] : "";
                                        int volume = parts.Length > 1 && int.TryParse(parts[1], out int v) ? v : 100;
                                        int pitch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 100;
                                        int pan = parts.Length > 3 && int.TryParse(parts[3], out int pn) ? pn : 0;
                                        int offset = parts.Length > 4 && int.TryParse(parts[4], out int o) ? o : 0;

                                        Debug.Log($"[FileIPC] ★ 创建 SoundDataStruct: filename={filename}, volume={volume}, pitch={pitch}, pan={pan}, offset={offset}");

                                        // 使用 typeof 直接获取类型，避免 Type.GetType 失败
                                        valToSet = new SoundDataStruct(filename, volume, pitch, pan, offset);
                                    }
                                }
                                else if (propInfo is NullablePropertyInfo nullableProp2)
                                {
                                    // 处理其他可空类型
                                    if (string.IsNullOrEmpty(strVal))
                                    {
                                        valToSet = null;
                                    }
                                    else if (nullableProp2.underlyingPropertyInfo is IntPropertyInfo)
                                    {
                                        valToSet = int.Parse(strVal);
                                    }
                                    else if (nullableProp2.underlyingPropertyInfo is FloatPropertyInfo)
                                    {
                                        valToSet = float.Parse(strVal);
                                    }
                                    else
                                    {
                                        valToSet = strVal;
                                    }
                                }
                                else if (propInfo.GetType() is var pit
                                    && pit.IsGenericType
                                    && pit.Name == "ArrayPropertyInfo`1")
                                {
                                    Type et = pit.GetGenericArguments()[0];
                                    var parts = (strVal ?? "").Split(',').Select(s => s.Trim()).ToArray();
                                    if (et == typeof(int))
                                        valToSet = parts.Select(p => int.TryParse(p, out int vi) ? vi : 0).ToArray();
                                    else if (et == typeof(float))
                                        valToSet = parts.Select(p => float.TryParse(p, out float vf) ? vf : 0f).ToArray();
                                    else if (et == typeof(bool))
                                        valToSet = parts.Select(p => p == "true").ToArray();
                                    else if (et.IsEnum)
                                    {
                                        var typedArr = Array.CreateInstance(et, parts.Length);
                                        for (int i = 0; i < parts.Length; i++)
                                        {
                                            try { typedArr.SetValue(Enum.Parse(et, parts[i]), i); }
                                            catch { typedArr.SetValue(Enum.GetValues(et).GetValue(0), i); }
                                        }
                                        valToSet = typedArr;
                                    }
                                    else if (et == typeof(SoundDataStruct))
                                    {
                                        var soundTypePropInfo = ev.GetType().GetProperty("soundType");
                                        var soundType = soundTypePropInfo != null ? (GameSoundType)soundTypePropInfo.GetValue(ev) : GameSoundType.SmallMistake;
                                        var groups = RDEditorConstants.gameSoundGroups;
                                        bool isGroup = groups.ContainsKey(soundType);

                                        var itemList = (strVal ?? "").Split(';');
                                        var resultArr = new SoundDataStruct[itemList.Length];
                                        for (int idx = 0; idx < itemList.Length; idx++)
                                        {
                                            var parts2 = itemList[idx].Split('|');
                                            string fn2  = parts2.Length > 0 ? parts2[0] : "";
                                            int vol2 = parts2.Length > 1 && int.TryParse(parts2[1], out int sv)  ? sv  : 100;
                                            int pit2 = parts2.Length > 2 && int.TryParse(parts2[2], out int sp) ? sp : 100;
                                            int pan2 = parts2.Length > 3 && int.TryParse(parts2[3], out int spn) ? spn : 0;
                                            int off2 = parts2.Length > 4 && int.TryParse(parts2[4], out int so)  ? so  : 0;
                                            if (string.IsNullOrEmpty(fn2) && isGroup && idx < groups[soundType].Length)
                                            {
                                                GameSoundType subType = groups[soundType][idx];
                                                if (RDGameSounds.defaultSounds.ContainsKey(subType))
                                                    fn2 = RDGameSounds.defaultSounds[subType].filename;
                                            }
                                            resultArr[idx] = new SoundDataStruct(fn2, vol2, pit2, pan2, off2);
                                        }
                                        valToSet = resultArr;
                                    }
                                    else if (et == typeof(string))
                                    {
                                        var trimmed = strVal?.Trim();
                                        if (trimmed != null && trimmed.StartsWith("["))
                                            valToSet = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed);
                                        else
                                            valToSet = (strVal ?? "").Split(',').Select(s => s.Trim()).ToArray();
                                    }
                                    else
                                        valToSet = strVal;
                                }
                                else valToSet = strVal;

                                // 设置值（null 也是合法值，用于可空类型）
                                propInfo.propertyInfo.SetValue(ev, valToSet);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"[FileIPC] 属性 {key} 转换失败: {ex.Message}");
                            }
                        }

                        Debug.Log("[FileIPC] 事件属性修改完成，SaveStateScope即将结束");

                        // 刷新 SoundData 偏移保护的基准值，防止用户编辑被误判为跑偏
                        AccessLogic.Instance?.RefreshSoundDataBaseline(ev);
                    } // SaveStateScope.Dispose() 减少changingState
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FileIPC] SaveStateScope调用失败: {ex.Message}");
                    return;
                }
            }

            // 更新 UI（在SaveStateScope外，状态已稳定）
            if (scnEditor.instance?.selectedControl != null &&
                scnEditor.instance.selectedControl.levelEvent == ev)
            {
                scnEditor.instance.selectedControl.UpdateUI();
                scnEditor.instance.inspectorPanelManager.GetCurrent()?.UpdateUI(ev);
            }
        }

        private bool ApplyBaseProperty(LevelEvent_Base ev, string key, string value)
        {
            try
            {
                switch (key)
                {
                    case "bar":
                        ev.bar = int.Parse(value);
                        return true;
                    case "beat":
                        ev.beat = float.Parse(value);
                        return true;
                    case "y":
                        ev.y = int.Parse(value);
                        return true;
                    case "row":
                        ev.row = int.Parse(value);
                        return true;
                    case "active":
                        ev.active = value == "true";
                        return true;
                    case "tag":
                        ev.tag = string.IsNullOrEmpty(value) ? null : value;
                        return true;
                    case "tagRunNormally":
                        ev.tagRunNormally = value == "true";
                        return true;
                    // PlaySong 特有属性
                    case "beatsPerMinute":
                    case "bpm":
                        // 使用反射设置，因为属性名可能是 beatsPerMinute 或 bpm
                        var bpmProp = ev.GetType().GetProperty("beatsPerMinute");
                        if (bpmProp != null && bpmProp.PropertyType == typeof(float))
                        {
                            bpmProp.SetValue(ev, float.Parse(value));
                            Debug.Log($"[FileIPC] 设置 beatsPerMinute = {value}");
                            return true;
                        }
                        return false;
                    case "loop":
                        var loopProp = ev.GetType().GetProperty("loop");
                        if (loopProp != null && loopProp.PropertyType == typeof(bool))
                        {
                            loopProp.SetValue(ev, value == "true");
                            Debug.Log($"[FileIPC] 设置 loop = {value}");
                            return true;
                        }
                        return false;
                    case "rooms":
                        if (!string.IsNullOrEmpty(value))
                            ev.rooms = value.Split(',')
                                .Select(s => int.TryParse(s.Trim(), out int r) ? r : 0)
                                .ToArray();
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 基础属性 {key} 设置失败: {ex.Message}");
                return true; // 标记为已处理（虽然是失败的）
            }
        }

        private object ParseFloatExpression(string expr)
        {
            try
            {
                // 尝试解析为简单浮点数
                if (float.TryParse(expr, out float simpleVal))
                {
                    var floatExprType = Type.GetType("RDLevelEditor.FloatExpression");
                    if (floatExprType != null)
                    {
                        return Activator.CreateInstance(floatExprType, simpleVal);
                    }
                }

                // 使用 RDEditorUtils.DecodeFloatExpression 解析复杂表达式
                var decodeMethod = Type.GetType("RDLevelEditor.RDEditorUtils")?.GetMethod("DecodeFloatExpression", new[] { typeof(object) });
                if (decodeMethod != null)
                {
                    return decodeMethod.Invoke(null, new object[] { expr });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 解析表达式 '{expr}' 失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 应用 MakeRow 的特殊字段（不在 propertiesInfo 中的字段）
        /// </summary>
        private bool ApplyMakeRowSpecialField(LevelEvent_MakeRow row, string key, string strVal)
        {
            try
            {
                switch (key)
                {
                    case "pulseSound":
                        // 解析 SoundData 格式: "filename|volume|pitch|pan|offset"
                        var parts = strVal.Split('|');
                        if (row.pulseSound != null)
                        {
                            row.pulseSound.filename = parts.Length > 0 ? parts[0] : "Shaker";
                            row.pulseSound.volume = parts.Length > 1 && int.TryParse(parts[1], out int v) ? v : 100;
                            row.pulseSound.pitch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 100;
                            row.pulseSound.pan = parts.Length > 3 && int.TryParse(parts[3], out int pn) ? pn : 0;
                            row.pulseSound.offset = parts.Length > 4 && int.TryParse(parts[4], out int o) ? o : 0;
                            Debug.Log($"[FileIPC] 已更新 pulseSound: {row.pulseSound.filename}");
                        }
                        return true;

                    case "mimicsRow":
                        row.mimicsRow = strVal == "true";
                        Debug.Log($"[FileIPC] 已更新 mimicsRow: {row.mimicsRow}");
                        return true;

                    case "customCharacterName":
                        row.customCharacterName = strVal;
                        Debug.Log($"[FileIPC] 已更新 customCharacterName: {strVal}");
                        return true;

                    default:
                        return false; // 不是特殊字段
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 应用 MakeRow 特殊字段 {key} 失败: {ex.Message}");
                return false;
            }
        }

        private void LaunchHelper()
        {
            string helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HelperExeName);

            if (!File.Exists(helperPath))
            {
                Debug.LogWarning($"[FileIPC] 找不到 Helper: {helperPath}");
                Narration.Say(RDString.Get("eam.error.helperNotFound"), NarrationCategory.Notification);
                return;
            }

            try
            {
                _helperProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = true
                });
                Debug.Log("[FileIPC] 已启动 RDEventEditorHelper.exe");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 启动 Helper 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 紧急强制取消：终止轮询、杀死 Helper 进程、解锁键盘
        /// </summary>
        public void ForceCancel()
        {
            Debug.LogWarning("[FileIPC] 紧急强制取消：终止 Helper 轮询");
            _isPolling = false;

            try
            {
                if (_helperProcess != null && !_helperProcess.HasExited)
                {
                    _helperProcess.Kill();
                    Debug.LogWarning("[FileIPC] 已强制终止 Helper 进程");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 终止 Helper 进程失败: {ex.Message}");
            }
            finally
            {
                _helperProcess = null;
            }

            UnlockKeyboard();
        }

        private List<PropertyData> ExtractProperties(LevelEvent_Base ev)
        {
            var list = new List<PropertyData>();

            LevelEventInfo info = ev.info;
            if (info == null) return list;

            // 添加基础属性（位置、行、房间等）
            AddBaseProperties(ev, list);

            foreach (var prop in info.propertiesInfo)
            {
                // 检查是否为 Button 类型（通过 controlAttribute 判断）
                bool isButton = prop.controlAttribute is ButtonAttribute;

                // 跳过仅用于 UI 的非 Button 属性（如 Description）
                // Button 类型需要保留，作为操作按钮显示
                if (prop.onlyUI && !isButton) continue;

                // 计算初始的可见性状态，但不跳过任何属性
                // 所有属性都应该被发送到Helper，由Helper动态控制可见性
                bool shouldBeVisible = prop.enableIf == null || prop.enableIf(ev);

                // 获取本地化的显示名称
                string localizedName = GetLocalizedPropertyName(ev, prop);

                // 处理 Button 类型
                if (isButton)
                {
                    var buttonAttr = prop.controlAttribute as ButtonAttribute;
                    list.Add(new PropertyData
                    {
                        name = prop.propertyInfo.Name,
                        displayName = localizedName,
                        type = "Button",
                        methodName = buttonAttr?.methodName,
                        isVisible = shouldBeVisible  // 初始可见性
                    });
                    continue;
                }

                var rawValue = prop.propertyInfo.GetValue(ev);

                var dto = new PropertyData
                {
                    name = prop.propertyInfo.Name,
                    displayName = localizedName,
                    value = ConvertPropertyValue(rawValue),
                    isVisible = shouldBeVisible  // 初始可见性
                };

                if (prop is IntPropertyInfo intProp && intProp.controlAttribute is RowAttribute rowAttr2)
                {
                    dto.type = "Row";
                    var (rOpts, rLocalOpts) = BuildRowOptions(rowAttr2.includeAll);
                    dto.options = rOpts;
                    dto.localizedOptions = rLocalOpts;
                }
                else if (prop is IntPropertyInfo) dto.type = "Int";
                else if (prop is FloatPropertyInfo)
                {
                    dto.type = "Float";
                    if (prop.controlAttribute is BPMCalculatorAttribute)
                        dto.hasBPMCalculator = true;
                }
                else if (prop is BoolPropertyInfo) dto.type = "Bool";
                else if (prop is StringPropertyInfo)
                {
                    dto.type = "String";
                    // 检查是否需要自动完成
                    if (prop.controlAttribute is InputFieldAttribute inputAttr && inputAttr.autocomplete)
                    {
                        try
                        {
                            dto.autocompleteSuggestions = CollectCustomMethods();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[FileIPC] 收集自动完成方法失败: {ex.Message}");
                        }
                    }
                }
                else if (prop is EnumPropertyInfo enumProp)
                {
                    dto.type = "Enum";
                    // 如果属性有 DropdownAttribute 且指定了 options，只提取允许的值
                    var dropdownAttr = prop.controlAttribute as DropdownAttribute;
                    if (dropdownAttr?.options != null && dropdownAttr.options.Length > 0)
                    {
                        dto.options = dropdownAttr.options;
                    }
                    else
                    {
                        dto.options = Enum.GetNames(enumProp.enumType);
                    }
                    // 尝试从游戏本地化获取枚举选项显示名，找不到则保留原名
                    dto.localizedOptions = dto.options.Select(name =>
                    {
                        string localized = RDString.GetWithCheck(
                            $"enum.{enumProp.enumType.Name}.{name}", out bool exists);
                        return exists ? StripRichTextTags(localized) : name;
                    }).ToArray();
                }
                else if (prop is ColorPropertyInfo) dto.type = "Color";
                else if (prop is Vector2PropertyInfo) dto.type = "Vector2";
                else if (prop is Float2PropertyInfo) dto.type = "Float2";
                else if (prop is FloatExpressionPropertyInfo) dto.type = "FloatExpression";
                else if (prop is FloatExpression2PropertyInfo) dto.type = "FloatExpression2";
                else if (prop is SoundDataPropertyInfo soundProp)
                {
                    dto.type = "SoundData";
                    dto.itsASong = ev is LevelEvent_PlaySong;

                    // 提取 SoundAttribute 配置
                    ExtractSoundAttributeConfig(prop, ev, dto);
                }
                else if (prop is NullablePropertyInfo nullableProp)
                {
                    // 检查底层类型
                    var underlying = nullableProp.underlyingPropertyInfo;
                    if (underlying is SoundDataPropertyInfo underlyingSoundProp)
                    {
                        dto.type = "SoundData";
                        dto.itsASong = ev is LevelEvent_PlaySong;
                        dto.isNullable = true;
                        
                        // 提取 SoundAttribute 配置（从 NullablePropertyInfo 获取）
                        ExtractSoundAttributeConfig(prop, ev, dto);
                    }
                    else if (underlying is IntPropertyInfo)
                    {
                        dto.type = "Int";
                        dto.isNullable = true;
                    }
                    else if (underlying is FloatPropertyInfo)
                    {
                        dto.type = "Float";
                        dto.isNullable = true;
                    }
                    else
                    {
                        dto.type = "String";
                        dto.isNullable = true;
                    }
                }
                else if (prop.GetType() is var pt
                    && pt.IsGenericType
                    && pt.Name == "ArrayPropertyInfo`1")
                {
                    Type elemType = pt.GetGenericArguments()[0];
                    if      (elemType == typeof(int))   dto.type = "IntArray";
                    else if (elemType == typeof(float)) dto.type = "FloatArray";
                    else if (elemType == typeof(bool))  dto.type = "BoolArray";
                    else if (elemType.IsEnum)           dto.type = "EnumArray";
                    else if (elemType == typeof(SoundDataStruct))
                    {
                        dto.type = "SoundDataArray";
                        ExtractSoundAttributeConfig(prop, ev, dto);

                        // SetGameSound 专用：填充子类型标签页名称和初始可见性
                        if (ev is LevelEvent_SetGameSound sgEvent)
                        {
                            var soundType = sgEvent.soundType;
                            if (RDEditorUtils.SoundTypeIsGroup(soundType))
                            {
                                // 组类型：获取子类型数组并生成本地化标签
                                var groups = RDEditorConstants.gameSoundGroups;
                                // 处理 P2 变体映射到基础组
                                var groupKey = soundType switch
                                {
                                    GameSoundType.PulseSoundHoldP2 => GameSoundType.PulseSoundHold,
                                    GameSoundType.ClapSoundHoldP2 => GameSoundType.ClapSoundHold,
                                    _ => soundType,
                                };
                                if (groups.ContainsKey(groupKey))
                                {
                                    var subTypes = groups[groupKey];
                                    dto.tabLabels = subTypes.Select(st =>
                                    {
                                        string localized = RDString.GetEnumValue(st);
                                        return !string.IsNullOrEmpty(localized) ? StripRichTextTags(localized) : st.ToString();
                                    }).ToArray();
                                }
                            }
                            // 非组类型：tabLabels 为 null，Helper 端不显示选项卡头
                        }
                    }
                    else if (elemType == typeof(string)) dto.type = "StringArray";
                    else                                dto.type = "String";

                    if (rawValue is Array arr2) dto.arrayLength = arr2.Length;

                    if (elemType.IsEnum)
                    {
                        dto.options = Enum.GetNames(elemType);
                        dto.localizedOptions = Enum.GetNames(elemType).Select(n =>
                        {
                            string k = $"enum.{elemType.Name}.{n}";
                            string v = RDString.GetWithCheck(k, out bool ok);
                            return ok ? v : n;
                        }).ToArray();

                        // 确保 value 使用枚举名称（而非整数），与 options 格式一致
                        if (rawValue is Array enumArr)
                        {
                            dto.value = string.Join(",", enumArr.Cast<object>().Select(o =>
                                Enum.GetName(elemType, o) ?? o?.ToString() ?? ""));
                        }
                    }
                }
                else dto.type = "String";

                // 特殊处理：ChangePlayersRows 的 players/cpuMarkers 补充行上下文
                if (ev.type == LevelEventType.ChangePlayersRows &&
                    (prop.propertyInfo.Name == "players" || prop.propertyInfo.Name == "cpuMarkers"))
                {
                    var rowCtrls = scnEditor.instance?.eventControls_rows;
                    if (rowCtrls != null)
                    {
                        dto.rowCount = rowCtrls.Count;
                        dto.rowNames = rowCtrls
                            .Select(r => (r?.FirstOrDefault()?.levelEvent as LevelEvent_MakeRow)?.GetRowString(shortName: true) ?? "")
                            .ToArray();
                    }
                    if (prop.propertyInfo.Name == "cpuMarkers")
                    {
                        dto.options = RDEditorConstants.AvailableCPUCharacters.Select(c => c.ToString()).ToArray();
                        dto.localizedOptions = RDEditorConstants.AvailableCPUCharacters.Select(c =>
                        {
                            string k = $"enum.Character.{c}";
                            string v = RDString.GetWithCheck(k, out bool ok);
                            return ok ? v : c.ToString();
                        }).ToArray();
                    }
                }

                list.Add(dto);
            }

            // 特殊处理：MakeRow 的字段（不是属性，没有 JsonProperty 标记）
            if (ev is LevelEvent_MakeRow row)
            {
                // pulseSound 字段
                if (row.pulseSound != null)
                {
                    string pulseSoundValue = $"{row.pulseSound.filename ?? "Shaker"}|{row.pulseSound.volume}|{row.pulseSound.pitch}|{row.pulseSound.pan}|{row.pulseSound.offset}";
                    var soundOptions = RDEditorConstants.BeatSounds.Select(s => s.ToString()).ToArray();

                    // 获取本地化名称
                    string displayName = RDString.GetWithCheck("editor.MakeRow.pulseSound", out bool exists);
                    if (!exists)
                    {
                        displayName = "pulseSound";
                    }

                    list.Add(new PropertyData
                    {
                        name = "pulseSound",
                        displayName = displayName,
                        type = "SoundData",
                        value = pulseSoundValue,
                        soundOptions = soundOptions,
                        localizedSoundOptions = soundOptions.Select(name =>
                        {
                            string key = $"enum.SoundEffect.{name}";
                            string localized = RDString.GetWithCheck(key, out bool localExists);
                            return localExists ? localized : name;
                        }).ToArray(),
                        allowCustomFile = true,
                        itsASong = false,
                        isVisible = true
                    });
                }

                // mimicsRow 字段（控制 rowToMimic 的可见性）
                string mimicsRowDisplayName = RDString.GetWithCheck("editor.MakeRow.mimicsRow", out bool mimicsRowExists);
                if (!mimicsRowExists)
                {
                    mimicsRowDisplayName = "mimicsRow";
                }

                list.Add(new PropertyData
                {
                    name = "mimicsRow",
                    displayName = mimicsRowDisplayName,
                    type = "Bool",
                    value = row.mimicsRow ? "true" : "false",
                    isVisible = true
                });

                // customCharacterName 字段（仅当 character == Custom 时可见）
                if (row.character == Character.Custom)
                {
                    string customCharDisplayName = RDString.GetWithCheck("editor.MakeRow.customCharacterName", out bool customCharExists);
                    if (!customCharExists)
                    {
                        customCharDisplayName = "customCharacterName";
                    }

                    list.Add(new PropertyData
                    {
                        name = "customCharacterName",
                        displayName = customCharDisplayName,
                        type = "String",
                        value = row.customCharacterName ?? "",
                        isVisible = true
                    });
                }
            }

            // 追加硬编码面板按钮（不在属性系统中，需手动注册）
            if (ev != null && HardcodedButtons.TryGetValue(ev.type, out var hardDefs))
            {
                foreach (var def in hardDefs)
                {
                    string displayName = RDString.GetWithCheck(def.localizationKey, out bool exists);
                    if (!exists) displayName = def.methodName;
                    else displayName = StripRichTextTags(displayName);
                    bool visible = false;
                    try { visible = def.isVisible(ev); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FileIPC] 硬编码按钮可见性检查失败 {def.methodName}: {ex.Message}");
                    }
                    list.Add(new PropertyData
                    {
                        name = def.methodName,
                        displayName = displayName,
                        type = "Button",
                        methodName = def.methodName,
                        isVisible = visible
                    });
                }
            }

            return list;
        }

        // ===================================================================================
        // 硬编码检查器按钮（非属性系统，直接定义在 InspectorPanel 子类上）
        // ===================================================================================

        private struct HardcodedButtonDef
        {
            public string methodName;
            public string localizationKey;
            public Func<LevelEvent_Base, bool> isVisible;
        }

        private static readonly Dictionary<LevelEventType, List<HardcodedButtonDef>> HardcodedButtons =
            new Dictionary<LevelEventType, List<HardcodedButtonDef>>
            {
                [LevelEventType.AddOneshotBeat] = new List<HardcodedButtonDef>
                {
                    new HardcodedButtonDef {
                        methodName = "BreakIntoOneshotBeats",
                        localizationKey = "editor.AddOneshotBeat.break",
                        isVisible = ev => ((LevelEvent_AddOneshotBeat)ev).loops > 0
                    },
                    new HardcodedButtonDef {
                        methodName = "SwitchControlToSetOneshotWave",
                        localizationKey = "editor.AddOneshotBeat.switchToSetWave",
                        isVisible = ev => ((LevelEvent_AddOneshotBeat)ev).loops == 0
                    },
                    new HardcodedButtonDef {
                        methodName = "CreateNurseCue",
                        localizationKey = "editor.AddOneshotBeat.createCue",
                        isVisible = ev => true
                    },
                }
            };

        // ===================================================================================
        // 自动完成方法收集
        // ===================================================================================

        private static readonly BindingFlags MethodScopeFlags =
            BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod;

        private static readonly Dictionary<string, System.Type> MethodScopes = new Dictionary<string, System.Type>
        {
            ["level"] = typeof(LevelBase),
            ["vfx"] = typeof(scrVfxControl),
            ["room"] = typeof(RDRoom)
        };

        private static readonly Dictionary<System.Type, string[]> SupportedArgTypes = new Dictionary<System.Type, string[]>
        {
            [typeof(int)] = new[] { "int", "0" },
            [typeof(float)] = new[] { "float", "0" },
            [typeof(string)] = new[] { "string", "\"\"" },
            [typeof(bool)] = new[] { "bool", "false" }
        };

        /// <summary>
        /// 收集所有可用的自定义方法（镜像 MethodAutocompleteUI 的逻辑）
        /// </summary>
        private MethodSuggestionDto[] CollectCustomMethods()
        {
            var suggestions = new List<MethodSuggestionDto>();

            // 获取自定义关卡类型
            System.Type customLevelType = typeof(LevelBase);
            try
            {
                if (scnEditor.instance?.levelSettings != null &&
                    System.Enum.TryParse<Level>(scnEditor.instance.levelSettings.customClass, out var levelEnum))
                {
                    customLevelType = LevelSelector.GetLevelTypeFromEnum(levelEnum);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 获取自定义关卡类型失败: {ex.Message}");
            }

            foreach (var scope in MethodScopes)
            {
                var typesToScan = new List<System.Type> { scope.Value };
                if (scope.Key == "level" && customLevelType != scope.Value)
                    typesToScan.Insert(0, customLevelType);

                foreach (var type in typesToScan)
                {
                    var methods = type.GetMethods(MethodScopeFlags);
                    foreach (var method in methods)
                    {
                        if (method.ReturnType != typeof(void)) continue;
                        var listedAttr = method.GetCustomAttribute<ListedMethodAttribute>();
                        if (listedAttr == null && !RDBase.isDev) continue;

                        if (!TryStringifyMethod(method, out string sig, out string defaultArgs))
                            continue;

                        bool isClassOnly = scope.Key == "level" &&
                            scope.Value.GetMethod(method.Name, MethodScopeFlags) == null;

                        // 构建 scope 前缀
                        string prefix;
                        if (scope.Key == "level")
                            prefix = isClassOnly ? "level." : "";
                        else if (scope.Key == "room")
                            prefix = "room1.";
                        else
                            prefix = scope.Key + ".";

                        // 获取方法描述
                        string description = null;
                        if (listedAttr != null && listedAttr.showDescription)
                        {
                            string descKey = "customMethod." + scope.Key + "." + method.Name;
                            string descText = RDString.GetWithCheck(descKey, out bool exists);
                            if (exists)
                                description = descText;
                        }

                        suggestions.Add(new MethodSuggestionDto
                        {
                            scope = scope.Key,
                            name = method.Name,
                            signature = sig,
                            description = description,
                            fullText = prefix + method.Name + "(" + defaultArgs + ")"
                        });
                    }
                }
            }

            return suggestions
                .OrderBy(s => s.scope)
                .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryStringifyMethod(MethodInfo method, out string signature, out string defaultArgs)
        {
            signature = null;
            defaultArgs = null;
            var paramParts = new List<string>();
            var argParts = new List<string>();

            foreach (var param in method.GetParameters())
            {
                if (!SupportedArgTypes.TryGetValue(param.ParameterType, out var typeInfo))
                    return false;
                paramParts.Add(typeInfo[0] + " " + param.Name);
                argParts.Add(GetSmartDefault(param.Name, typeInfo[0], typeInfo[1]));
            }

            signature = method.Name + "(" + string.Join(", ", paramParts) + ")";
            defaultArgs = string.Join(", ", argParts);
            return true;
        }

        private static string GetSmartDefault(string name, string type, string defaultVal)
        {
            if (type == "string")
            {
                if (name.StartsWith("ease")) return "\"Linear\"";
                if (name.StartsWith("color") || name.StartsWith("colHex")) return "\"ffffff\"";
            }
            else if (type == "float")
            {
                if (name == "scale" || name == "alpha" || name == "opacity") return "1";
            }
            return defaultVal;
        }
        /// <summary>
        /// 提取 SoundAttribute 配置（选项列表、是否允许自定义文件）
        /// </summary>
        private void ExtractSoundAttributeConfig(BasePropertyInfo prop, LevelEvent_Base ev, PropertyData dto)
        {
            // controlAttribute 在 BasePropertyInfo 上，需要从底层类型获取
            var controlAttr = prop.controlAttribute;
            if (controlAttr == null && prop is NullablePropertyInfo nullableProp)
            {
                controlAttr = nullableProp.underlyingPropertyInfo?.controlAttribute;
            }
            
            if (controlAttr == null)
            {
                dto.allowCustomFile = true;
                Debug.Log($"[FileIPC] SoundAttribute not found, using defaults");
                return;
            }
            
            // 使用反射获取 SoundAttribute 的字段（避免版本兼容问题）
            var attrType = controlAttr.GetType();
            if (attrType.Name != "SoundAttribute")
            {
                dto.allowCustomFile = true;
                Debug.Log($"[FileIPC] ControlAttribute is not SoundAttribute: {attrType.Name}");
                return;
            }
            
            try
            {
                // 获取 customFile 字段
                var customFileField = attrType.GetField("customFile");
                if (customFileField != null)
                {
                    dto.allowCustomFile = (bool)(customFileField.GetValue(controlAttr) ?? true);
                }
                else
                {
                    dto.allowCustomFile = true;
                }
                
                // 获取 optionsMethod 字段
                var optionsMethodField = attrType.GetField("optionsMethod");
                string optionsMethod = optionsMethodField?.GetValue(controlAttr) as string;
                
                // 获取 options 字段
                var optionsField = attrType.GetField("options");
                string[] options = optionsField?.GetValue(controlAttr) as string[];
                
                // 获取选项列表
                if (!string.IsNullOrEmpty(optionsMethod))
                {
                    dto.soundOptions = GetSoundOptions(ev, optionsMethod, prop.propertyInfo.DeclaringType);
                }
                else if (options != null && options.Length > 0)
                {
                    dto.soundOptions = options;
                }

                Debug.Log($"[FileIPC] SoundAttribute: customFile={dto.allowCustomFile}, optionsMethod={optionsMethod ?? "null"}, optionsCount={dto.soundOptions?.Length ?? 0}");

                // 为 soundOptions 生成本地化名称
                if (dto.soundOptions != null && dto.soundOptions.Length > 0)
                {
                    dto.localizedSoundOptions = dto.soundOptions.Select(name =>
                    {
                        // 尝试从本地化系统获取（使用游戏的 enum.SoundEffect 格式）
                        string key = $"enum.SoundEffect.{name}";
                        string localized = RDString.GetWithCheck(key, out bool exists);
                        return exists ? localized : name;  // 如果没有本地化，使用原名
                    }).ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 获取 SoundAttribute 字段失败: {ex.Message}");
                dto.allowCustomFile = true;
            }
        }

        /// <summary>
        /// 调用事件实例上的选项方法获取音效选项列表
        /// </summary>
        private string[] GetSoundOptions(LevelEvent_Base ev, string methodName, Type declaringType)
        {
            try
            {
                var method = declaringType.GetMethod(methodName, 
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.NonPublic | 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.Static);
                    
                if (method == null)
                {
                    Debug.LogWarning($"[FileIPC] 找不到选项方法: {methodName} in {declaringType.Name}");
                    return null;
                }
                
                // 实例方法需要事件实例，静态方法传 null
                object instance = method.IsStatic ? null : ev;
                var result = method.Invoke(instance, new object[0]) as string[];
                
                Debug.Log($"[FileIPC] 获取音效选项: {methodName} -> {(result != null ? result.Length + "项" : "null")}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 获取音效选项失败: {ex.Message}");
                return null;
            }
        }

        private (string[] options, string[] localizedOptions) BuildRowOptions(bool includeAll)
        {
            var rows = scnEditor.instance.rowsData;
            int offset = includeAll ? 1 : 0;
            int count = rows.Count + offset;
            var opts = new string[count];
            var localOpts = new string[count];

            if (includeAll)
            {
                opts[0] = "-1";
                localOpts[0] = RDString.Get("editor.TintRows.rows.allRows");
            }

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string charName = row.character == Character.Custom
                    ? (row.customCharacterName ?? "?")
                    : RDString.Get($"enum.Character.{row.character}.short");
                string roomDisplay = RDString.Get("editor.roomIndex").Replace("[index]", (row.room + 1).ToString());
                opts[i + offset] = i.ToString();
                localOpts[i + offset] = $"{i + 1} {charName} {roomDisplay}";
            }

            return (opts, localOpts);
        }

        // 有序白名单，决定展示顺序；内部/缓存字段不在此列
        private static readonly string[] _settingsFieldOrder = new[]
        {
            "song", "artist", "author", "description", "tags",
            "specialArtistType", "artistPermissionFileName", "artistLinks",
            "difficulty", "seizureWarning",
            "canBePlayedOn", "multiplayerAppearance",
            "previewImageName", "syringeIconName", "previewSongName",
            "previewSongStartTime", "previewSongDuration",
            "songLabelHue", "songLabelGrayscale", "levelVolume",
            "firstBeatBehavior", "separate2PLevelFilename",
            "rankMaxMistakes", "rankDescription",
        };

        private List<PropertyData> BuildSettingsProperties()
        {
            var settings = scnEditor.instance.levelSettings;
            object boxedSettings = settings;
            var settingsType = typeof(RDLevelSettings);
            var list = new List<PropertyData>();

            foreach (var fieldName in _settingsFieldOrder)
            {
                var field = settingsType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    Debug.LogWarning($"[FileIPC] 未找到 RDLevelSettings 字段：{fieldName}");
                    continue;
                }

                var rawValue = field.GetValue(boxedSettings);
                var fieldType = field.FieldType;
                var dto = new PropertyData
                {
                    name = fieldName,
                    displayName = RDString.Get($"eam.settings.{fieldName}"),
                };

                if (fieldType == typeof(string))
                {
                    dto.type = "String";
                    dto.value = (rawValue as string) ?? "";
                }
                else if (fieldType == typeof(int))
                {
                    dto.type = "Int";
                    dto.value = rawValue?.ToString() ?? "0";
                }
                else if (fieldType == typeof(float))
                {
                    dto.type = "Float";
                    dto.value = rawValue?.ToString() ?? "0";
                }
                else if (fieldType == typeof(bool))
                {
                    dto.type = "Bool";
                    dto.value = (bool)rawValue ? "true" : "false";
                }
                else if (fieldType.IsEnum)
                {
                    dto.type = "Enum";
                    dto.value = rawValue?.ToString() ?? "";
                    dto.options = Enum.GetNames(fieldType);
                    dto.localizedOptions = dto.options.Select(n => {
                        string loc = RDString.GetWithCheck($"enum.{fieldType.Name}.{n}", out bool ok);
                        return ok ? StripRichTextTags(loc) : n;
                    }).ToArray();
                }
                else if (fieldType == typeof(int[]))
                {
                    dto.type = "IntArray";
                    var arr = rawValue as int[];
                    dto.value = arr != null ? string.Join(",", arr) : "";
                    dto.arrayLength = arr?.Length ?? 0;
                }
                else if (fieldType == typeof(string[]))
                {
                    dto.type = "StringArray";
                    var arr = rawValue as string[];
                    dto.value = arr != null ? System.Text.Json.JsonSerializer.Serialize(arr) : "[]";
                    dto.arrayLength = arr?.Length ?? 0;
                }
                else
                {
                    Debug.LogWarning($"[FileIPC] 不支持的设置字段类型：{fieldName} ({fieldType.Name})");
                    continue;
                }

                list.Add(dto);
            }

            return list;
        }

        public void StartSettingsEditing()
        {
            _currentEvent = null;
            _currentRow = null;
            _currentEditType = "settings";
            _sessionToken = System.Guid.NewGuid().ToString();

            var sourceData = new SourceData
            {
                editType = "settings",
                eventType = "LevelSettings",
                token = _sessionToken,
                properties = BuildSettingsProperties(),
                levelAudioFiles = GetLevelAudioFiles(),
                levelDirectory = GetLevelDirectory()
            };

            // 为 levelAudioFiles 生成本地化名称（移除扩展名）
            if (sourceData.levelAudioFiles != null && sourceData.levelAudioFiles.Length > 0)
            {
                sourceData.localizedLevelAudioFiles = sourceData.levelAudioFiles.Select(filename =>
                    System.IO.Path.GetFileNameWithoutExtension(filename)
                ).ToArray();
            }

            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(_sourcePath, JsonSerializer.Serialize(sourceData, opts));
                Debug.Log("[FileIPC] 已写入 source.json (元数据编辑)");
            }
            catch (Exception ex) { Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}"); return; }

            LaunchHelper();
            LockKeyboard();
            _isPolling = true;
        }

        public void StartJumpToCursorEdit()
        {
            if (_isPolling)
            {
                Debug.LogWarning("[FileIPC] 已有编辑会话进行中");
                return;
            }

            if (AccessLogic.Instance == null)
            {
                Debug.LogError("[FileIPC] AccessLogic.Instance 为空");
                return;
            }

            _currentEvent = null;
            _currentRow = null;
            _currentEditType = "jump";
            _sessionToken = System.Guid.NewGuid().ToString();

            var cursor = AccessLogic.Instance._editCursor;

            var properties = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "bar",
                    displayName = RDString.Get("eam.cursor.jump.bar"),
                    value = cursor.bar.ToString(),
                    type = "Int"
                },
                new PropertyData
                {
                    name = "beat",
                    displayName = RDString.Get("eam.cursor.jump.beat"),
                    value = cursor.beat.ToString("F2"),
                    type = "Float"
                }
            };

            var sourceData = new SourceData
            {
                editType = "jump",
                eventType = "JumpToCursor",
                token = _sessionToken,
                properties = properties,
                levelDirectory = GetLevelDirectory()
            };

            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(_sourcePath, JsonSerializer.Serialize(sourceData, opts));
                Debug.Log("[FileIPC] 已写入 source.json (跳转光标)");
            }
            catch (Exception ex) { Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}"); return; }

            LaunchHelper();
            LockKeyboard();
            _isPolling = true;
        }

        public void StartChainNameEdit()
        {
            if (_isPolling)
            {
                Debug.LogWarning("[FileIPC] 已有编辑会话进行中");
                return;
            }

            _currentEvent = null;
            _currentRow = null;
            _currentEditType = "chainName";
            _sessionToken = System.Guid.NewGuid().ToString();

            var properties = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "chainName",
                    displayName = RDString.Get("eam.chain.nameLabel"),
                    value = "",
                    type = "String"
                }
            };

            var sourceData = new SourceData
            {
                editType = "chainName",
                eventType = "EventChain",
                token = _sessionToken,
                properties = properties,
                levelDirectory = GetLevelDirectory()
            };

            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(_sourcePath, JsonSerializer.Serialize(sourceData, opts));
                Debug.Log("[FileIPC] 已写入 source.json (事件链命名)");
            }
            catch (Exception ex) { Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}"); return; }

            LaunchHelper();
            LockKeyboard();
            _isPolling = true;
        }

        private void ApplyChainNameResult(Dictionary<string, string> updates)
        {
            if (updates == null || !updates.ContainsKey("chainName")) return;
            if (AccessLogic.Instance == null) return;

            string chainName = updates["chainName"]?.Trim();
            if (string.IsNullOrEmpty(chainName))
            {
                Narration.Say(RDString.Get("eam.chain.invalidName"), NarrationCategory.Navigation);
                return;
            }

            // 检查非法文件名字符
            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (chainName.IndexOfAny(invalidChars) >= 0)
            {
                Narration.Say(RDString.Get("eam.chain.invalidName"), NarrationCategory.Navigation);
                return;
            }

            string pendingData = AccessLogic.Instance._pendingChainData;
            if (string.IsNullOrEmpty(pendingData))
            {
                Narration.Say(string.Format(RDString.Get("eam.chain.saveFailed"), "no data"), NarrationCategory.Navigation);
                return;
            }

            try
            {
                string levelDir = GetLevelDirectory();
                if (string.IsNullOrEmpty(levelDir))
                {
                    Narration.Say(RDString.Get("eam.chain.noLevel"), NarrationCategory.Navigation);
                    return;
                }

                string chainDir = Path.Combine(levelDir, ".RDLEAccess", "EventChains");
                Directory.CreateDirectory(chainDir);

                string filePath = Path.Combine(chainDir, chainName + ".json");
                File.WriteAllText(filePath, pendingData);

                AccessLogic.Instance._pendingChainData = null;

                Narration.Say(string.Format(RDString.Get("eam.chain.saved"), chainName), NarrationCategory.Navigation);
                Debug.Log($"[FileIPC] 事件链已保存: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 保存事件链失败: {ex.Message}");
                Narration.Say(string.Format(RDString.Get("eam.chain.saveFailed"), ex.Message), NarrationCategory.Navigation);
            }
        }

        // ===================================================================================
        // 条件编辑 (Condition Create/Edit)
        // ===================================================================================

        /// <summary>
        /// 打开 Helper 新建条件
        /// </summary>
        public void StartConditionCreate(LevelEvent_Base targetEvent)
        {
            if (_isPolling)
            {
                Debug.LogWarning("[FileIPC] 已有编辑会话进行中");
                return;
            }

            _currentEvent = null;
            _currentRow = null;
            _currentEditType = "condition";
            _conditionalEditMode = "create";
            _editingConditionalId = 0;
            _conditionalTargetEvent = targetEvent;
            _sessionToken = System.Guid.NewGuid().ToString();

            var sourceData = BuildConditionSourceData("create", 0, "Custom", "", "", targetEvent?.conditionalDuration ?? 0f);

            WriteAndLaunch(sourceData, "条件新建");
        }

        /// <summary>
        /// 打开 Helper 编辑已有本地条件
        /// </summary>
        public void StartConditionEdit(int localId)
        {
            if (_isPolling)
            {
                Debug.LogWarning("[FileIPC] 已有编辑会话进行中");
                return;
            }

            var editor = scnEditor.instance;
            var cond = editor?.conditionals?.Find(c => c.id == localId);
            if (cond == null)
            {
                Debug.LogWarning($"[FileIPC] 未找到本地条件 ID={localId}");
                return;
            }

            _currentEvent = null;
            _currentRow = null;
            _currentEditType = "condition";
            _conditionalEditMode = "edit";
            _editingConditionalId = localId;
            _conditionalTargetEvent = null;
            _sessionToken = System.Guid.NewGuid().ToString();

            string typeName = cond.type.ToString();
            // 取第一个使用该条件的事件的 duration 作为预填值
            float existingDuration = 0f;
            if (editor.eventControls != null)
            {
                foreach (var ctrl in editor.eventControls)
                {
                    if (ctrl?.levelEvent?.HasConditional(localId).HasValue == true)
                    {
                        existingDuration = ctrl.levelEvent.conditionalDuration;
                        break;
                    }
                }
            }
            var sourceData = BuildConditionSourceData("edit", localId, typeName, cond.tag ?? "", cond.description ?? "", existingDuration);

            // 预填当前值
            if (sourceData.allTypeProperties != null && sourceData.allTypeProperties.TryGetValue(typeName, out var props))
            {
                var condType = cond.GetType();
                foreach (var prop in props)
                {
                    var pi = condType.GetProperty(prop.name);
                    if (pi != null)
                    {
                        object val = pi.GetValue(cond);
                        prop.value = val?.ToString() ?? "";
                    }
                }
            }

            WriteAndLaunch(sourceData, "条件编辑");
        }

        private SourceData BuildConditionSourceData(string mode, int id, string type, string tag, string description, float duration)
        {
            var allTypeProps = BuildAllTypeProperties();
            var rowNames = BuildConditionRowNames();

            var availableTypes = new[] { "Custom", "LastHit", "TimesExecuted", "Language" };
            var localizedTypes = availableTypes.Select(t =>
            {
                string loc = RDString.GetWithCheck($"enum.ConditionalType.{t}", out bool exists);
                return exists ? loc : t;
            }).ToArray();

            return new SourceData
            {
                editType = "condition",
                token = _sessionToken,
                conditionEditMode = mode,
                conditionalId = id,
                conditionalType = type,
                conditionalTag = tag,
                conditionalDescription = description,
                availableTypes = availableTypes,
                localizedTypes = localizedTypes,
                allTypeProperties = allTypeProps,
                rowNames = rowNames,
                levelDirectory = GetLevelDirectory(),
                conditionTypeLabelLocalized = RDString.Get("editor.Conditionals.type"),
                conditionTagLabelLocalized = RDString.Get("eam.conditional.tagLabel"),
                conditionDescriptionLabelLocalized = RDString.Get("eam.conditional.descriptionLabel"),
                conditionalDuration = duration,
                conditionDurationLabelLocalized = RDString.Get("editor.Conditionals.duration")
            };
        }

        private Dictionary<string, List<PropertyData>> BuildAllTypeProperties()
        {
            var result = new Dictionary<string, List<PropertyData>>();

            // Custom: expression (String)
            result["Custom"] = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "customExpression",
                    displayName = RDString.Get("editor.Conditionals.expression"),
                    value = "",
                    type = "String"
                }
            };

            // TimesExecuted: maxTimes (Int)
            result["TimesExecuted"] = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "maxTimes",
                    displayName = RDString.Get("editor.Conditionals.timesExecutedMaxTimes"),
                    value = "1",
                    type = "Int"
                }
            };

            // LastHit: row (Enum) + resultType (Enum)
            var rowNames = BuildConditionRowNames();
            var rowValues = new string[rowNames.Length];
            rowValues[0] = "-1";
            for (int i = 1; i < rowNames.Length; i++)
                rowValues[i] = (i - 1).ToString();

            var offsetTypes = new[]
            {
                "VeryEarly", "SlightlyEarly", "Perfect", "SlightlyLate", "VeryLate", "Missed", "AnyEarlyOrLate"
            };
            var localizedOffsets = offsetTypes.Select(t =>
            {
                string key = $"enum.OffsetType.{t}";
                string loc = RDString.GetWithCheck(key, out bool exists);
                return exists ? loc : t;
            }).ToArray();

            result["LastHit"] = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "row",
                    displayName = RDString.Get("editor.Conditionals.lastHitRow"),
                    value = "-1",
                    type = "Enum",
                    options = rowValues,
                    localizedOptions = rowNames
                },
                new PropertyData
                {
                    name = "resultType",
                    displayName = RDString.Get("editor.Conditionals.lastHitType"),
                    value = "Perfect",
                    type = "Enum",
                    options = offsetTypes,
                    localizedOptions = localizedOffsets
                }
            };

            // Language: languageName (Enum, Unity SystemLanguage)
            var langValues = Enum.GetValues(typeof(UnityEngine.SystemLanguage))
                                 .Cast<UnityEngine.SystemLanguage>()
                                 .Select(l => l.ToString())
                                 .ToArray();
            var localizedLangs = langValues.Select(l =>
            {
                string key = $"enum.SystemLanguage.{l}";
                string loc = RDString.GetWithCheck(key, out bool exists);
                return exists ? loc : l;
            }).ToArray();

            result["Language"] = new List<PropertyData>
            {
                new PropertyData
                {
                    name = "languageName",
                    displayName = RDString.Get("editor.Conditionals.language"),
                    value = langValues.Length > 0 ? langValues[0] : "",
                    type = "Enum",
                    options = langValues,
                    localizedOptions = localizedLangs
                }
            };

            return result;
        }

        private string[] BuildConditionRowNames()
        {
            var editor = scnEditor.instance;
            var names = new List<string> { RDString.Get("editor.Conditionals.anyRow") };
            if (editor?.rowsData != null)
            {
                for (int i = 0; i < editor.rowsData.Count; i++)
                {
                    var row = editor.rowsData[i];
                    string charName = row.character == Character.Custom
                        ? (row.customCharacterName ?? "?")
                        : RDString.Get($"enum.Character.{row.character}.short");
                    string roomDisplay = RDString.Get("editor.roomIndex").Replace("[index]", (row.room + 1).ToString());
                    names.Add($"{i + 1} {charName} {roomDisplay}");
                }
            }
            return names.ToArray();
        }

        private void WriteAndLaunch(SourceData sourceData, string logLabel)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(_sourcePath, JsonSerializer.Serialize(sourceData, options));
                Debug.Log($"[FileIPC] 已写入 source.json ({logLabel})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 写入 source.json 失败: {ex.Message}");
                return;
            }
            LaunchHelper();
            LockKeyboard();
            _isPolling = true;
        }

        private void ApplyConditionalResult(ResultData resultData)
        {
            if (resultData?.action == "cancel")
            {
                Debug.Log("[FileIPC] 条件编辑已取消");
                return;
            }

            if (resultData?.conditionalType == null)
            {
                Debug.LogError("[FileIPC] 条件编辑结果缺少 conditionalType 字段");
                return;
            }

            var editor = scnEditor.instance;
            if (editor == null) return;

            string typeName = resultData.conditionalType;
            string tag = resultData.conditionalTag ?? "";
            string description = resultData.conditionalDescription ?? "";
            var updates = resultData.updates ?? new Dictionary<string, string>();

            // 用反射创建对应的 Conditional_XXX 实例
            var condType = Type.GetType($"RDLevelEditor.Conditional_{typeName}, Assembly-CSharp");
            if (condType == null)
            {
                Debug.LogError($"[FileIPC] 未找到条件类型: Conditional_{typeName}");
                return;
            }

            int id;
            if (_conditionalEditMode == "edit")
            {
                id = _editingConditionalId;
            }
            else
            {
                // 生成唯一 ID（从 1 起找未被占用的）
                id = 1;
                while (editor.conditionals != null && editor.conditionals.Exists(c => c.id == id))
                    id++;
            }

            Conditional cond;
            try
            {
                cond = (Conditional)Activator.CreateInstance(condType, id);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 创建条件实例失败: {ex.Message}");
                return;
            }

            cond.tag = string.IsNullOrEmpty(tag) ? id.ToString() : tag;
            cond.description = description;

            // 反射赋值各字段
            foreach (var kv in updates)
            {
                var prop = condType.GetProperty(kv.Key);
                if (prop == null || !prop.CanWrite) continue;
                try
                {
                    object converted = ConvertStringToPropertyValue(kv.Value, prop.PropertyType);
                    if (converted != null)
                        prop.SetValue(cond, converted);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FileIPC] 赋值属性 {kv.Key} 失败: {ex.Message}");
                }
            }

            using (new SaveStateScope())
            {
                if (_conditionalEditMode == "edit")
                {
                    int idx = editor.conditionals?.FindIndex(c => c.id == id) ?? -1;
                    if (idx >= 0)
                        editor.conditionals[idx] = cond;
                    else
                        editor.conditionals?.Add(cond);
                    // 将 duration 应用到所有使用该条件的事件
                    if (resultData.conditionalDuration >= 0f && editor.eventControls != null)
                    {
                        foreach (var ctrl in editor.eventControls)
                        {
                            if (ctrl?.levelEvent?.HasConditional(id).HasValue == true)
                                ctrl.levelEvent.conditionalDuration = resultData.conditionalDuration;
                        }
                    }
                    Debug.Log($"[FileIPC] 已更新条件 ID={id}");
                }
                else
                {
                    editor.conditionals?.Add(cond);
                    // 自动附加到目标事件（激活状态），并设置 duration
                    if (_conditionalTargetEvent != null &&
                        editor.eventControls?.Exists(ec => ec.levelEvent == _conditionalTargetEvent) == true)
                    {
                        _conditionalTargetEvent.SetConditional(id, null, false);
                        if (resultData.conditionalDuration >= 0f)
                            _conditionalTargetEvent.conditionalDuration = resultData.conditionalDuration;
                        Debug.Log($"[FileIPC] 新条件 ID={id} 已自动附加到目标事件");
                    }
                    Debug.Log($"[FileIPC] 已新建条件 ID={id}");
                }
            }

            string mode = _conditionalEditMode;
            OnConditionalSaved?.Invoke(id);
            Debug.Log($"[FileIPC] 条件{(mode == "edit" ? "编辑" : "新建")}完成，ID={id}");
        }

        private void ApplySettingsUpdates(Dictionary<string, string> updates)
        {
            var editor = scnEditor.instance;
            if (editor == null) return;

            using (new SaveStateScope())
            {
                object boxed = (object)editor.levelSettings;
                var settingsType = typeof(RDLevelSettings);

                foreach (var kv in updates)
                {
                    var field = settingsType.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null)
                    {
                        Debug.LogWarning($"[FileIPC] 未找到设置字段：{kv.Key}");
                        continue;
                    }

                    var fieldType = field.FieldType;
                    object parsedValue = null;
                    bool success = true;

                    if (fieldType == typeof(string))
                    {
                        parsedValue = kv.Value;
                    }
                    else if (fieldType == typeof(int))
                    {
                        if (int.TryParse(kv.Value, out int iv)) parsedValue = iv;
                        else success = false;
                    }
                    else if (fieldType == typeof(float))
                    {
                        if (float.TryParse(kv.Value, out float fv)) parsedValue = fv;
                        else success = false;
                    }
                    else if (fieldType == typeof(bool))
                    {
                        parsedValue = kv.Value == "true";
                    }
                    else if (fieldType.IsEnum)
                    {
                        try { parsedValue = Enum.Parse(fieldType, kv.Value); }
                        catch { success = false; }
                    }
                    else if (fieldType == typeof(int[]))
                    {
                        var parts = kv.Value.Split(',');
                        var arr = new int[parts.Length];
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (!int.TryParse(parts[i].Trim(), out arr[i])) { success = false; break; }
                        }
                        if (success) parsedValue = arr;
                    }
                    else if (fieldType == typeof(string[]))
                    {
                        var trimmed = kv.Value?.Trim();
                        if (trimmed != null && trimmed.StartsWith("["))
                            parsedValue = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed);
                        else
                            parsedValue = (kv.Value ?? "").Split(',').Select(s => s.Trim()).ToArray();
                    }
                    else
                    {
                        Debug.LogWarning($"[FileIPC] 不支持的设置字段类型：{kv.Key} ({fieldType.Name})");
                        continue;
                    }

                    if (success && parsedValue != null)
                        field.SetValue(boxed, parsedValue);
                    else if (!success)
                        Debug.LogWarning($"[FileIPC] 无法解析设置字段值：{kv.Key} = {kv.Value}");
                }

                editor.levelSettings = (RDLevelSettings)boxed;
            }
            Debug.Log("[FileIPC] 已应用关卡元数据更改");
        }

        private void ApplyJumpToCursorUpdates(Dictionary<string, string> updates)
        {
            if (AccessLogic.Instance == null) return;
            if (updates == null) return;

            try
            {
                int bar = 1;
                float beat = 1f;

                if (updates.ContainsKey("bar"))
                {
                    if (!int.TryParse(updates["bar"], out bar) || bar < 1)
                    {
                        bar = 1;
                    }
                }

                if (updates.ContainsKey("beat"))
                {
                    if (!float.TryParse(updates["beat"], out beat) || beat < 1f)
                    {
                        beat = 1f;
                    }
                }

                AccessLogic.Instance._editCursor = new BarAndBeat(bar, beat);

                string position = ModUtils.FormatBarAndBeat(AccessLogic.Instance._editCursor);
                string message = string.Format(RDString.Get("eam.cursor.jump.success"), position);
                Narration.Say(message, NarrationCategory.Navigation);

                Debug.Log($"[FileIPC] 已跳转光标到 {position}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 跳转光标失败: {ex.Message}");
            }
        }

        private void AddBaseProperties(LevelEvent_Base ev, List<PropertyData> list)
        {
            // Bar (小节)
            if (ev.info.attribute.usesBar)
            {
                list.Add(new PropertyData
                {
                    name = "bar",
                    displayName = RDString.Get("editor.bar"),
                    value = ev.bar.ToString(),
                    type = "Int"
                });
            }

            // Beat (拍子)
            if (ev.usesBeat)
            {
                list.Add(new PropertyData
                {
                    name = "beat",
                    displayName = RDString.Get("editor.beat"),
                    value = ev.beat.ToString(),
                    type = "Float"
                });
            }

            // Y 位置
            if (ev.usesY)
            {
                list.Add(new PropertyData
                {
                    name = "y",
                    displayName = "Y Position",
                    value = ev.y.ToString(),
                    type = "Int"
                });
            }

            // Row (行)
            if (ev.info.usesRow)
            {
                var (rowOpts, rowLocalOpts) = BuildRowOptions(false);
                list.Add(new PropertyData
                {
                    name = "row",
                    displayName = RDString.Get("editor.row"),
                    value = ev.row.ToString(),
                    type = "Row",
                    options = rowOpts,
                    localizedOptions = rowLocalOpts
                });
            }

            // Active (激活状态)
            list.Add(new PropertyData
            {
                name = "active",
                displayName = "Active",
                value = ev.active.ToString().ToLower(),
                type = "Bool"
            });

            // Tag (标签)
            list.Add(new PropertyData
            {
                name = "tag",
                displayName = RDString.Get("editor.tag"),
                value = ev.tag ?? "",
                type = "String"
            });

            // TagRunNormally (标签正常运行)
            if (!string.IsNullOrEmpty(ev.tag))
            {
                list.Add(new PropertyData
                {
                    name = "tagRunNormally",
                    displayName = "Tag Run Normally",
                    value = ev.tagRunNormally.ToString().ToLower(),
                    type = "Bool"
                });
            }

            // Rooms (目标房间)
            if (ev.roomsUsage != RoomsUsage.NotUsed)
            {
                int rc = TryGetRoomCount();
                var roomNames = Enumerable.Range(0, rc)
                    .Select(i => RDString.Get("editor.roomIndex").Replace("[index]", (i + 1).ToString()))
                    .ToArray();
                list.Add(new PropertyData
                {
                    name = "rooms",
                    displayName = RDString.Get("editor.rooms") ?? "目标房间",
                    value = string.Join(",", ev.rooms ?? new int[] { 0 }),
                    type = "Rooms",
                    roomCount = rc,
                    roomsUsage = ev.roomsUsage.ToString(),
                    localizedOptions = roomNames
                });
            }
        }

        private int TryGetRoomCount()
        {
            try
            {
                var gameField = typeof(scnEditor).GetProperty("game") ?? (System.Reflection.MemberInfo)typeof(scnEditor).GetField("game");
                object game = null;
                if (gameField is System.Reflection.PropertyInfo pi) game = pi.GetValue(scnEditor.instance);
                else if (gameField is System.Reflection.FieldInfo fi) game = fi.GetValue(scnEditor.instance);
                if (game == null) return 4;
                var roomsProp = game.GetType().GetProperty("rooms") ?? (System.Reflection.MemberInfo)game.GetType().GetField("rooms");
                System.Collections.ICollection rooms = null;
                if (roomsProp is System.Reflection.PropertyInfo rpi) rooms = rpi.GetValue(game) as System.Collections.ICollection;
                else if (roomsProp is System.Reflection.FieldInfo rfi) rooms = rfi.GetValue(game) as System.Collections.ICollection;
                return rooms?.Count ?? 4;
            }
            catch { return 4; }
        }

        private string GetLocalizedPropertyName(LevelEvent_Base ev, BasePropertyInfo prop)
        {
            string propertyName = prop.name;
            string localized;

            // 如果有自定义本地化键，直接使用
            if (!string.IsNullOrEmpty(prop.customLocalizationKey))
            {
                localized = RDString.Get(prop.customLocalizationKey);
            }
            else
            {
                // 尝试特定于事件类型的键: editor.{eventType}.{propertyName}
                string specificKey = $"editor.{ev.type}.{propertyName}";
                localized = RDString.GetWithCheck(specificKey, out bool exists);
                if (!exists)
                {
                    // 尝试通用键: editor.{propertyName}
                    string genericKey = $"editor.{propertyName}";
                    localized = RDString.GetWithCheck(genericKey, out exists);
                    if (!exists)
                    {
                        // 如果都没有找到，返回原始属性名
                        Debug.LogWarning($"[FileIPC] 未找到属性 '{propertyName}' 的本地化键");
                        localized = propertyName;
                    }
                }
            }

            // 过滤富文本颜色标签: <color=#...>...</color> 和 </color>
            return StripRichTextTags(localized);
        }

        private string StripRichTextTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 移除 <color=#...>...</color> 标签
            // 使用简单的字符串操作而不是正则，避免性能问题
            string result = text;
            int colorStart = result.IndexOf("<color=");
            while (colorStart >= 0)
            {
                int colorEnd = result.IndexOf(">", colorStart);
                if (colorEnd > colorStart)
                {
                    // 移除 <color=#...> 标签
                    result = result.Substring(0, colorStart) + result.Substring(colorEnd + 1);
                    
                    // 查找并移除对应的 </color> 结束标签
                    int closeTagStart = result.IndexOf("</color>", colorStart);
                    if (closeTagStart >= 0)
                    {
                        result = result.Substring(0, closeTagStart) + result.Substring(closeTagStart + 8);
                    }
                }
                else
                {
                    break;
                }
                colorStart = result.IndexOf("<color=", colorStart);
            }

            return result;
        }

        private string ConvertPropertyValue(object value)
        {
            if (value == null) return "";

            try
            {
                // 处理 Nullable<T> 类型
                var valueType = value.GetType();
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var hasValueProp = valueType.GetProperty("HasValue");
                    var hasValue = hasValueProp != null && (bool)hasValueProp.GetValue(value);
                    if (!hasValue) return "";  // null 值返回空字符串
                    
                    var valueProp = valueType.GetProperty("Value");
                    if (valueProp != null)
                    {
                        value = valueProp.GetValue(value);
                        if (value == null) return "";
                        valueType = value.GetType();
                    }
                }
                
                if (value is string[] sa) return System.Text.Json.JsonSerializer.Serialize(sa);
                if (value is int[]   ia) return string.Join(",", ia);
                if (value is float[] fa) return string.Join(",", fa);
                if (value is bool[]  ba) return string.Join(",", ba.Select(b => b ? "true" : "false"));
                // SoundDataStruct[] 必须在通用 Array 之前处理
                if (valueType.IsArray && valueType.GetElementType()?.Name == "SoundDataStruct")
                {
                    var arr = (Array)value;
                    var parts = new List<string>();
                    foreach (var item in arr)
                    {
                        var t = item.GetType();
                        var fn  = t.GetField("filename")?.GetValue(item);
                        var vol = t.GetField("volume")?.GetValue(item);
                        var pit = t.GetField("pitch")?.GetValue(item);
                        var pan = t.GetField("pan")?.GetValue(item);
                        var off = t.GetField("offset")?.GetValue(item);
                        parts.Add($"{fn}|{vol}|{pit}|{pan}|{off}");
                    }
                    return string.Join(";", parts);
                }
                if (value is Array   ga)
                {
                    if (ga.GetType().GetElementType() == typeof(string))
                        return System.Text.Json.JsonSerializer.Serialize(ga.Cast<string>().ToArray());
                    return string.Join(",", ga.Cast<object>().Select(o => o?.ToString() ?? ""));
                }

                if (value is UnityEngine.Vector2 v2) return $"{v2.x},{v2.y}";
                if (value is UnityEngine.Vector3 v3) return $"{v3.x},{v3.y},{v3.z}";
                if (value is UnityEngine.Color c)
                {
                    try { return $"#{UnityEngine.ColorUtility.ToHtmlStringRGB(c)}"; }
                    catch { return c.ToString(); }
                }
                // ColorOrPalette 类型
                if (valueType.Name == "ColorOrPalette")
                {
                    try
                    {
                        var colorObj = valueType.GetProperty("color")?.GetValue(value);
                        if (colorObj is UnityEngine.Color colorVal)
                        {
                            return $"#{UnityEngine.ColorUtility.ToHtmlStringRGB(colorVal)}";
                        }
                        var paletteIndex = valueType.GetProperty("paletteIndex")?.GetValue(value);
                        if (paletteIndex != null)
                        {
                            return $"pal{paletteIndex}";
                        }
                    }
                    catch { }
                }
                // Float2 类型
                if (valueType.Name == "Float2")
                {
                    float x = (float)(valueType.GetField("x")?.GetValue(value) ?? 0f);
                    float y = (float)(valueType.GetField("y")?.GetValue(value) ?? 0f);
                    return $"{x},{y}";
                }
                // FloatExpression 类型
                if (valueType.Name == "FloatExpression")
                {
                    return value.ToString();
                }
                // FloatExpression2 类型
                if (valueType.Name == "FloatExpression2")
                {
                    var xExpr = valueType.GetField("x")?.GetValue(value);
                    var yExpr = valueType.GetField("y")?.GetValue(value);
                    string xStr = xExpr?.ToString() ?? "";
                    string yStr = yExpr?.ToString() ?? "";
                    return $"{xStr},{yStr}";
                }
                // SoundDataStruct 类型
                if (valueType.Name == "SoundDataStruct")
                {
                    var filename = valueType.GetField("filename")?.GetValue(value);
                    var volume = valueType.GetField("volume")?.GetValue(value);
                    var pitch = valueType.GetField("pitch")?.GetValue(value);
                    var pan = valueType.GetField("pan")?.GetValue(value);
                    var offset = valueType.GetField("offset")?.GetValue(value);
                    var result = $"{filename}|{volume}|{pitch}|{pan}|{offset}";
                    Debug.Log($"[FileIPC] ConvertPropertyValue SoundDataStruct: property=?, result={result}");
                    return result;
                }
                if (value is Enum e) return e.ToString();
                if (value is bool b) return b ? "true" : "false";
                if (value is int i) return i.ToString();
                if (value is float f) return f.ToString();
                if (value is double d) return d.ToString();
            }
            catch { }

            return value.ToString();
        }

        private string[] GetLevelAudioFiles()
        {
            try
            {
                string filePath = scnEditor.instance?.openedFilePath;
                if (string.IsNullOrEmpty(filePath)) return null;
                string levelDir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(levelDir)) return null;
                var exts = new HashSet<string>(
                    GC.SupportedAudioFiles.Select(e => "." + e),
                    StringComparer.OrdinalIgnoreCase);
                return Directory.GetFiles(levelDir)
                    .Where(f => exts.Contains(Path.GetExtension(f)))
                    .Select(f => Path.GetFileName(f))
                    .OrderBy(f => f)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FileIPC] 获取关卡音频文件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取游戏内置音乐列表
        /// </summary>
        private Dictionary<string, string> GetInternalSongs()
        {
            try
            {
                var songOffsetsType = Type.GetType("RDSongOffsets, Assembly-CSharp");
                if (songOffsetsType == null)
                {
                    Debug.LogWarning("[FileIPC] 未找到 RDSongOffsets 类型");
                    return null;
                }

                var instanceProp = songOffsetsType.GetProperty("instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceProp == null)
                {
                    Debug.LogWarning("[FileIPC] 未找到 RDSongOffsets.instance 属性");
                    return null;
                }

                var instance = instanceProp.GetValue(null);
                if (instance == null)
                {
                    Debug.LogWarning("[FileIPC] RDSongOffsets.instance 为 null");
                    return null;
                }

                var miscField = songOffsetsType.GetField("misc",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (miscField == null)
                {
                    Debug.LogWarning("[FileIPC] 未找到 RDSongOffsets.misc 字段");
                    return null;
                }

                var miscList = miscField.GetValue(instance) as System.Collections.IList;
                if (miscList == null)
                {
                    Debug.LogWarning("[FileIPC] RDSongOffsets.misc 为 null 或不是列表");
                    return null;
                }

                var result = new Dictionary<string, string>();
                var songOffsetType = Type.GetType("SongOffset, Assembly-CSharp");
                var nameField = songOffsetType?.GetField("name");
                var folderField = songOffsetType?.GetField("folder");

                if (nameField == null)
                {
                    Debug.LogWarning("[FileIPC] 未找到 SongOffset.name 字段");
                    return null;
                }

                if (folderField == null)
                {
                    Debug.LogWarning("[FileIPC] 未找到 SongOffset.folder 字段");
                    return null;
                }

                foreach (var song in miscList)
                {
                    if (song == null) continue;

                    // 排除 Sfx/ 文件夹中的文件（音效），包含其他所有文件（音乐）
                    string folder = folderField.GetValue(song) as string;
                    if (!string.IsNullOrEmpty(folder) && folder.StartsWith("Sfx/"))
                        continue;

                    string name = nameField.GetValue(song) as string;
                    if (!string.IsNullOrEmpty(name))
                    {
                        // 使用本地化键获取显示名称
                        string displayName = RDString.GetWithCheck($"song.{name}", out bool exists);
                        if (!exists) displayName = name;
                        result[name] = displayName;
                    }
                }

                Debug.Log($"[FileIPC] 获取到 {result.Count} 个内置音乐");
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 获取内置音乐列表失败: {ex.Message}");
                return null;
            }
        }

        private string GetLevelDirectory()
        {
            try
            {
                string filePath = scnEditor.instance?.openedFilePath;
                if (string.IsNullOrEmpty(filePath)) return null;
                string levelDir = Path.GetDirectoryName(filePath);
                return Directory.Exists(levelDir) ? levelDir : null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FileIPC] 获取关卡目录失败: {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private class SourceData
        {
            public string editType;  // "event"、"row"、"condition" 等
            public string eventType;
            public string token;  // 会话特征码
            public List<PropertyData> properties;
            public string[] levelAudioFiles;  // 关卡目录中的音频文件名列表
            public string[] localizedLevelAudioFiles;  // 关卡音频文件的本地化显示名称
            public string levelDirectory;  // 关卡目录路径
            public Dictionary<string, string> internalSongs;  // 内置音乐列表 (filename -> displayName)
            // 条件编辑专用字段
            public string conditionEditMode;     // "create" 或 "edit"
            public int conditionalId;            // edit 时的目标条件 ID
            public string conditionalType;       // 当前选中类型（默认 "Custom"）
            public string conditionalTag;        // 条件 tag
            public string conditionalDescription; // 条件 description
            public string[] availableTypes;      // 可选条件类型名列表
            public string[] localizedTypes;      // 可选条件类型本地化名列表
            public Dictionary<string, List<PropertyData>> allTypeProperties; // 各类型的属性列表
            public string[] rowNames;            // 轨道名（首项为"任意行"，供 LastHit 使用）
            // 条件编辑器 UI 标签本地化
            public string conditionTypeLabelLocalized;        // "类型" 标签
            public string conditionTagLabelLocalized;         // "标签" 标签
            public string conditionDescriptionLabelLocalized; // "描述" 标签
            public float conditionalDuration;                 // 事件当前的持续时间（拍）
            public string conditionDurationLabelLocalized;    // "持续时间" 标签
        }

        [Serializable]
        private class ResultData
        {
            public string token;  // 会话特征码（必须回传）
            public string action;
            public Dictionary<string, string> updates;
            public string methodName;  // 当 action 为 "execute" 时使用
            public string bpmPropertyName;  // 当 action 为 "bpmCalculator" 时：目标属性名
            // 条件编辑结果专用字段
            public string conditionalType;
            public string conditionalTag;
            public string conditionalDescription;
            public float conditionalDuration;   // 事件持续时间（拍），-1 表示未修改
        }

        /// <summary>
        /// 播放声音请求（Helper → Mod 单向通信）
        /// </summary>
        [Serializable]
        private class PlaySoundRequest
        {
            public string token;      // 会话特征码
            public string soundName;  // 音效文件名
            public int volume;        // 音量 (0-100)
            public int pitch;         // 音调 (0-200)
            public int pan;           // 声像 (-100 到 100)
            public bool itsASong;     // 是否是音乐（true=音乐，false=音效）
        }

        /// <summary>
        /// 停止声音请求数据类
        /// </summary>
        [Serializable]
        private class StopSoundRequest
        {
            public string token;      // 会话特征码
        }

        [Serializable]
        private class PropertyData
        {
            public string name;
            public string displayName;
            public string value;
            public string type;
            public string[] options;
            public string[] localizedOptions; // 本地化显示名，null 时 Helper 直接用 options
            public string methodName;  // Button 类型专用：要调用的方法名
            public bool itsASong;      // SoundData 类型专用：区分歌曲/音效
            public bool isNullable;    // 是否为可空类型
            public string[] soundOptions;   // SoundData 类型专用：预设音效选项列表
            public string[] localizedSoundOptions;  // SoundData 类型专用：预设音效的本地化名称
            public bool allowCustomFile;    // SoundData 类型专用：是否允许浏览外部文件
            public bool isVisible = true;   // NEW: 该属性是否应该显示（enableIf判断结果）
            public string customName;       // Character 类型专用：自定义角色名称
            public int arrayLength;         // Array 类型专用：元素个数
            public int roomCount;           // Rooms 类型专用：房间总数
            public string roomsUsage;       // Rooms 类型专用：使用模式
            public string[] rowNames;       // EnumArray 专用：轨道显示名称列表
            public int rowCount;            // EnumArray 专用：实际显示的行数
            public MethodSuggestionDto[] autocompleteSuggestions;  // 自动完成建议列表
            public bool hasBPMCalculator;  // 是否带有 BPMCalculator 属性
            public string[] tabLabels;     // SoundDataArray 专用：各标签页的本地化名称
        }

        [Serializable]
        private class MethodSuggestionDto
        {
            public string scope;       // "level" / "vfx" / "room"
            public string name;        // 方法名
            public string signature;   // 完整签名（如 "SetBorderColor(string colHex, float opacity)"）
            public string description; // 方法描述（本地化文本，可为 null）
            public string fullText;    // 选中后填充的完整文本
        }

        // NEW: Helper → Mod 请求数据类
        [Serializable]
        private class PropertyUpdateRequest
        {
            public string token;                   // 关联原有的session token
            public string action = "validateVisibility";
            public Dictionary<string, string> updates;  // 修改的属性名 → 新值
            public PropertyData[] currentProperties;    // 当前的完整属性列表（含所有值）
        }

        // NEW: Mod → Helper 响应数据类
        [Serializable]
        private class PropertyUpdateResponse
        {
            public string token;
            public Dictionary<string, bool> visibilityChanges;  // 属性名 → 是否应该显示
            public Dictionary<string, string[]> tabLabelsChanges;  // 属性名 → 新的标签页名称列表（null表示改为单面板）
        }
    }
}
