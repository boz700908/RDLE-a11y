using System;
using System.Windows.Forms;
using RDEventEditorHelper.IPC;

namespace RDEventEditorHelper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 创建编辑器窗口
            EditorForm editorForm = new EditorForm();

            // 创建并启动管道客户端（作为服务器监听）
            PipeClient listener = new PipeClient();
            listener.EditorForm = editorForm;
            listener.Start();

            // 绑定应用更改事件 - 发送给主 Mod
            editorForm.OnApplyChanges += (updates) =>
            {
                var response = new PipeMessage
                {
                    Type = MessageType.ApplyChanges,
                    Updates = updates
                };
                listener.SendMessage(response);
            };

            // 绑定关闭事件 - 通知主 Mod
            editorForm.OnCloseRequested += () =>
            {
                var response = new PipeMessage
                {
                    Type = MessageType.EditorClosed
                };
                listener.SendMessage(response);
            };

            // 启动 WinForms 消息循环
            Application.Run(editorForm);

            // 退出时停止监听
            listener.Stop();
        }
    }
}
