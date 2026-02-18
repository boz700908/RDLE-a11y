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
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RDEventEditorHelper.log");

        [STAThread]
        static void Main()
        {
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
            Log($"已读取 source.json: {json.Substring(0, Math.Min(200, json.Length))}...");

            var sourceData = JsonConvert.DeserializeObject<SourceData>(json);
            Log($"事件类型: {sourceData?.eventType}, 属性数量: {sourceData?.properties?.Length ?? 0}");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            EditorForm editorForm = new EditorForm();
            editorForm.SetData(sourceData?.eventType, sourceData?.properties);

            editorForm.OnApply += (updates) =>
            {
                var result = new ResultData { action = "apply", updates = updates };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log("已写入 result.json (apply)");
            };

            editorForm.OnOK += (updates) =>
            {
                var result = new ResultData { action = "ok", updates = updates };
                string resultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(ResultPath, resultJson);
                Log("已写入 result.json (ok)，退出");
            };

            editorForm.OnCancel += () =>
            {
                File.WriteAllText(ResultPath, "{}");
                Log("已写入空 result.json (cancel)，退出");
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
            public string eventType;
            public PropertyData[] properties;
        }

        private class ResultData
        {
            public string action;
            public System.Collections.Generic.Dictionary<string, string> updates;
        }
    }
}
