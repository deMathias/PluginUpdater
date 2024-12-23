using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WheresMyPluginsAt
{
    public class LogEntry(string message, System.Numerics.Vector4 color)
    {
        public string Message { get; } = message;
        public DateTime Timestamp { get; } = DateTime.Now;
        public System.Numerics.Vector4 Color { get; } = color;
    }
    public class ConsoleLog
    {
        private readonly List<LogEntry> _logEntries = [];
        private readonly object _logLock = new();

        private static readonly System.Numerics.Vector4
            ColorInfo = new System.Numerics.Vector4(1.0f, 1.0f, 1.0f, 1.0f),      // White
            ColorWarning = new System.Numerics.Vector4(1.0f, 0.8f, 0.0f, 1.0f),   // Yellow
            ColorError = new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f),     // Red
            ColorSuccess = new System.Numerics.Vector4(0.2f, 1.0f, 0.2f, 1.0f);   // Green

        public void AddLogMessage(string message, System.Numerics.Vector4 color)
        {
            lock (_logLock)
            {
                _logEntries.Add(new LogEntry(message, color));
            }
        }

        public void LogInfo(string message) => AddLogMessage(message, ColorInfo);
        public void LogWarning(string message) => AddLogMessage(message, ColorWarning);
        public void LogError(string message) => AddLogMessage(message, ColorError);
        public void LogSuccess(string message) => AddLogMessage(message, ColorSuccess);

        public void RenderConsoleLog()
        {
            ImGui.Spacing();
            ImGui.Text("Console Log");

            var size = new System.Numerics.Vector2(-1, -1);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new System.Numerics.Vector4(0.2f, 0.2f, 0.2f, 1.0f));
            if (ImGui.BeginChild("##consolelog", size, ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar))
            {
                lock (_logLock)
                {
                    foreach (var entry in _logEntries)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, entry.Color);
                        ImGui.Text($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                        ImGui.PopStyleColor();
                    }
                }

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                {
                    ImGui.SetScrollHereY(1.0f);
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
    }
}
