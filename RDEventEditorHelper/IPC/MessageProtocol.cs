using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDEventEditorHelper.IPC
{
    // ===================================================================================
    // 消息协议 - 与主 Mod 共享
    // ===================================================================================

    public enum MessageType
    {
        OpenEditor,
        ApplyChanges,
        CloseEditor,
        EditorClosed
    }

    public class PropertyData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("value")]
        public object Value { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("options")]
        public string[] Options { get; set; } // For enum types
    }

    public class PipeMessage
    {
        [JsonPropertyName("type")]
        public MessageType Type { get; set; }

        [JsonPropertyName("eventId")]
        public string EventId { get; set; }

        [JsonPropertyName("eventType")]
        public string EventType { get; set; }

        [JsonPropertyName("properties")]
        public List<PropertyData> Properties { get; set; }

        [JsonPropertyName("updates")]
        public Dictionary<string, object> Updates { get; set; }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this);
        }

        public static PipeMessage FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<PipeMessage>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeClient] 解析消息失败: {ex.Message}");
                return null;
            }
        }
    }
}
