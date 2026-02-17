using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace RDEventEditorHelper.IPC
{
    // ===================================================================================
    // 命名管道客户端 - 监听来自主 Mod 的连接
    // ===================================================================================
    public class PipeClient
    {
        private const string PipeName = "RDEventEditor";
        private Thread _listenThread;
        private NamedPipeServerStream _pipeServer;
        private bool _isRunning;

        // 当前活动连接
        private NamedPipeServerStream _currentPipe;
        private StreamWriter _currentWriter;

        public event Action<PipeMessage> OnMessageReceived;
        public EditorForm EditorForm { get; set; }

        public void Start()
        {
            _isRunning = true;
            _listenThread = new Thread(ListenForConnections);
            _listenThread.IsBackground = true;
            _listenThread.Start();
            Console.WriteLine("[PipeClient] 已启动，等待主 Mod 连接...");
        }

        public void Stop()
        {
            _isRunning = false;
            _pipeServer?.Dispose();
            _currentPipe?.Dispose();
        }

        public void SendMessage(PipeMessage message)
        {
            try
            {
                if (_currentWriter != null && _currentPipe?.IsConnected == true)
                {
                    string json = message.ToJson();
                    _currentWriter.WriteLine(json);
                    Console.WriteLine($"[PipeClient] 发送消息: {json.Substring(0, Math.Min(50, json.Length))}...");
                }
                else
                {
                    Console.WriteLine("[PipeClient] 无法发送，管道未连接");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeClient] 发送消息失败: {ex.Message}");
            }
        }

        private void ListenForConnections()
        {
            while (_isRunning)
            {
                try
                {
                    _pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    // 等待连接
                    _pipeServer.WaitForConnection();
                    Console.WriteLine("[PipeClient] 主 Mod 已连接");

                    // 保存当前连接
                    _currentPipe = _pipeServer;

                    // 处理连接
                    HandleConnection(_pipeServer);
                }
                catch (IOException)
                {
                    // 管道被关闭，继续等待
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PipeClient] 监听异常: {ex.Message}");
                }
                finally
                {
                    _currentWriter = null;
                    _currentPipe?.Dispose();
                    _currentPipe = null;
                    _pipeServer = null;
                }
            }
        }

        private void HandleConnection(NamedPipeServerStream pipeServer)
        {
            try
            {
                using var reader = new StreamReader(pipeServer);
                _currentWriter = new StreamWriter(pipeServer) { AutoFlush = true };

                while (pipeServer.IsConnected)
                {
                    // 读取消息
                    string json = reader.ReadLine();
                    if (string.IsNullOrEmpty(json)) break;

                    Console.WriteLine($"[PipeClient] 收到消息: {json.Substring(0, Math.Min(100, json.Length))}...");

                    var message = PipeMessage.FromJson(json);
                    if (message == null) continue;

                    // 处理消息
                    string response = ProcessMessage(message);
                    
                    // 发送响应
                    if (!string.IsNullOrEmpty(response))
                    {
                        _currentWriter.WriteLine(response);
                        Console.WriteLine($"[PipeClient] 发送响应: {response.Substring(0, Math.Min(50, response.Length))}...");
                    }
                }
            }
            catch (IOException)
            {
                // 连接关闭
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeClient] 处理连接异常: {ex.Message}");
            }
        }

        private string ProcessMessage(PipeMessage message)
        {
            switch (message.Type)
            {
                case MessageType.OpenEditor:
                    // 在 UI 线程上显示编辑器
                    if (EditorForm != null)
                    {
                        EditorForm.Invoke(new Action(() =>
                        {
                            EditorForm.ShowEditor(message.EventType, message.Properties);
                        }));
                    }
                    return null; // 无需响应

                case MessageType.CloseEditor:
                    if (EditorForm != null)
                    {
                        EditorForm.Invoke(new Action(() =>
                        {
                            EditorForm.HideEditor();
                        }));
                    }
                    return null;

                default:
                    Console.WriteLine($"[PipeClient] 未知消息类型: {message.Type}");
                    return null;
            }
        }
    }
}
