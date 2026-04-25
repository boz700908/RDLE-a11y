using System;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace RDEventEditorHelper
{
    static class Program
    {
        private static readonly string TempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        private static readonly string SourcePath = Path.Combine(TempDir, "source.json");
        private static readonly string ResultPath = Path.Combine(TempDir, "result.json");
        private static readonly string LogPath = Path.Combine(TempDir, "RDEventEditorHelper.log");

        [STAThread]
        static void Main()
        {
            // 启动时覆盖之前的日志
            if (File.Exists(LogPath))
            {
                File.Delete(LogPath);
            }
            Log("=== Helper 启动 ===");

            if (!Directory.Exists(TempDir))
            {
                Directory.CreateDirectory(TempDir);
            }

            if (!File.Exists(SourcePath))
            {
                Log("source.json 不存在，退出");
                return;
            }

            string json = File.ReadAllText(SourcePath);
            File.Delete(SourcePath);
            Log($"已读取 source.json 内容:\n{json}");

            var sourceData = JsonConvert.DeserializeObject<SourceData>(json);
            Log($"编辑类型: {sourceData?.editType ?? "event"}, 事件类型: {sourceData?.eventType}, 特征码: {sourceData?.token}, 属性数量: {sourceData?.properties?.Length ?? 0}");

            // 保存特征码，必须在所有响应中回传
            string sessionToken = sourceData?.token ?? "";
            string editType = sourceData?.editType ?? "event";
            
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            EditorForm editorForm = new EditorForm();
            
            // 根据编辑类型设置标题
            string title;
            if (editType == "condition")
            {
                title = sourceData?.conditionEditMode == "create"
                    ? "新建条件 (Create Condition)"
                    : $"编辑条件 (Edit Condition): {sourceData?.conditionalDescription ?? sourceData?.conditionalTag}";
                var condData = new ConditionSourceData
                {
                    conditionEditMode = sourceData?.conditionEditMode,
                    conditionalId = sourceData?.conditionalId ?? 0,
                    conditionalType = sourceData?.conditionalType,
                    conditionalTag = sourceData?.conditionalTag,
                    conditionalDescription = sourceData?.conditionalDescription,
                    availableTypes = sourceData?.availableTypes,
                    localizedTypes = sourceData?.localizedTypes,
                    allTypeProperties = sourceData?.allTypeProperties,
                    rowNames = sourceData?.rowNames,
                    conditionTypeLabelLocalized = sourceData?.conditionTypeLabelLocalized,
                    conditionTagLabelLocalized = sourceData?.conditionTagLabelLocalized,
                    conditionDescriptionLabelLocalized = sourceData?.conditionDescriptionLabelLocalized,
                    conditionalDuration = sourceData?.conditionalDuration ?? 0f,
                    conditionDurationLabelLocalized = sourceData?.conditionDurationLabelLocalized
                };
                editorForm.SetConditionData(condData, title, sessionToken);
            }
            else
            {
                title = editType == "settings"
                    ? "编辑关卡元数据 (Edit Level Settings)"
                    : editType == "row"
                        ? "编辑轨道 (Edit Row)"
                        : editType == "jump"
                            ? "跳转到位置 (Jump to Position)"
                            : editType == "chainName"
                                ? "保存事件链 (Save Event Chain)"
                                : editType == "gridCustom"
                                    ? "自定义网格精度 (Custom Grid Size)"
                                    : $"编辑事件 (Edit Event): {sourceData?.eventType}";
                editorForm.SetData(sourceData?.eventType, sourceData?.properties, title, sourceData?.levelAudioFiles, sourceData?.levelDirectory, sourceData?.localizedLevelAudioFiles, sessionToken, sourceData?.internalSongs);
            }

            editorForm.OnOK += (updates) =>
            {
                var result = new ResultData { token = sessionToken, action = "ok", updates = updates };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log($"已写入 result.json (ok), token: {sessionToken}，退出");
            };

            editorForm.OnCancel += () =>
            {
                var result = new ResultData { token = sessionToken, action = "cancel" };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log($"已写入 result.json (cancel), token: {sessionToken}，退出");
            };

            editorForm.OnExecute += (methodName) =>
            {
                var result = new ResultData { token = sessionToken, action = "execute", methodName = methodName };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log($"已写入 result.json (execute: {methodName}), token: {sessionToken}，退出");
            };

            editorForm.OnBPMCalculator += (updates) =>
            {
                var result = new ResultData { token = sessionToken, action = "bpmCalculator", updates = updates };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log($"已写入 result.json (bpmCalculator), token: {sessionToken}，退出");
            };

            Log("显示编辑器窗口");
            Application.Run(editorForm);

            Log("=== Helper 退出 ===");
        }

        private static void Log(string msg)
        {
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
                sw.Flush();
            }
            catch { }
        }

        private class SourceData
        {
            public string editType;  // "event"、"row"、"condition" 等
            public string eventType;
            public string token;  // 会话特征码
            public PropertyData[] properties;
            public string[] levelAudioFiles;  // 关卡目录中的音频文件名列表
            public string[] localizedLevelAudioFiles;  // 本地化的音频文件显示名称
            public string levelDirectory;  // 关卡目录路径
            public System.Collections.Generic.Dictionary<string, string> internalSongs;  // 内置音乐列表 (filename -> displayName)
            // 条件编辑专用字段
            public string conditionEditMode;     // "create" 或 "edit"
            public int conditionalId;
            public string conditionalType;
            public string conditionalTag;
            public string conditionalDescription;
            public string[] availableTypes;
            public string[] localizedTypes;
            public System.Collections.Generic.Dictionary<string, RDEventEditorHelper.PropertyData[]> allTypeProperties;
            public string[] rowNames;
            // UI 标签本地化
            public string conditionTypeLabelLocalized;
            public string conditionTagLabelLocalized;
            public string conditionDescriptionLabelLocalized;
            public float conditionalDuration;
            public string conditionDurationLabelLocalized;
        }

        private class ResultData
        {
            public string token;  // 会话特征码（必须回传）
            public string action;
            public System.Collections.Generic.Dictionary<string, string> updates;
            public string methodName;  // 当 action 为 "execute" 时使用
            // 条件编辑结果专用字段
            public string conditionalType;
            public string conditionalTag;
            public string conditionalDescription;
            public float conditionalDuration = -1f;  // -1 表示未修改
        }
    }
}
