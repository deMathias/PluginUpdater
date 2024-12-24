using ImGuiNET;
using ExileCore2.Shared;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace WheresMyPluginsAt
{
    public enum NotificationType
    {
        Success,
        Warning,
        Error,
        Info
    }

    public class Notification
    {
        public string Message { get; }
        public string Title { get; }
        public NotificationType Type { get; }
        public DateTime CreatedAt { get; }
        public DateTime ExpiresAt { get; set; }
        public float Opacity { get; set; } = 1.0f;

        public Notification(string message, string title, NotificationType type, TimeSpan duration)
        {
            Message = message;
            Title = title;
            Type = type;
            CreatedAt = DateTime.Now;
            ExpiresAt = CreatedAt + duration;
        }

        public bool IsExpired => DateTime.Now > ExpiresAt;
    }

    public class NotificationSystem
    {
        private readonly List<Notification> _notifications = [];
        private readonly object _notificationLock = new();
        private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);
        private const float FadeOutDuration = 0.5f;

        private static readonly Dictionary<NotificationType, Vector4> TypeColors = new()
        {
            { NotificationType.Success, new Vector4(0.0f, 0.7f, 0.0f, 1.0f) },
            { NotificationType.Warning, new Vector4(1.0f, 0.7f, 0.0f, 1.0f) },
            { NotificationType.Error, new Vector4(0.9f, 0.1f, 0.1f, 1.0f) },
            { NotificationType.Info, new Vector4(0.0f, 0.45f, 0.9f, 1.0f) }
        };

        public void AddNotification(string message, string title, NotificationType type)
        {
            lock (_notificationLock)
            {
                _notifications.Add(new Notification(message, title, type, DefaultDuration));
            }
        }

        public void Render(RectangleF windowRect)
        {
            const float WindowBuffer = 50f;
            const float Padding = 10f;
            const float NotificationWidth = 250f;
            const float NotificationHeight = 80f;
            float startX = windowRect.Right - NotificationWidth - WindowBuffer;
            float startY = windowRect.Bottom - NotificationHeight - WindowBuffer;

            lock (_notificationLock)
            {
                for (int i = _notifications.Count - 1; i >= 0; i--)
                {
                    var notification = _notifications[i];
                    var timeLeft = (notification.ExpiresAt - DateTime.Now).TotalSeconds;

                    if (timeLeft <= 0)
                    {
                        _notifications.RemoveAt(i);
                        continue;
                    }

                    if (timeLeft < FadeOutDuration)
                    {
                        notification.Opacity = (float)(timeLeft / FadeOutDuration);
                    }

                    float yOffset = i * (NotificationHeight + Padding);
                    var pos = new Vector2(startX, startY - yOffset);

                    RenderNotification(notification, pos, NotificationWidth, NotificationHeight);
                }
            }
        }

        private static void RenderNotification(Notification notification, Vector2 position, float width, float height)
        {
            ImGui.SetNextWindowPos(position);
            ImGui.SetNextWindowSize(new Vector2(width, height));

            var indicatorColor = TypeColors[notification.Type];

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(1.0f, 1.0f, 1.0f, 0.95f * notification.Opacity));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.0f, 0.0f, 0.0f, 0.1f * notification.Opacity));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            var flags = ImGuiWindowFlags.NoTitleBar |
                       ImGuiWindowFlags.NoResize |
                       ImGuiWindowFlags.NoMove |
                       ImGuiWindowFlags.NoScrollbar |
                       ImGuiWindowFlags.NoCollapse |
                       ImGuiWindowFlags.NoSavedSettings;

            if (ImGui.Begin($"##notification_{notification.CreatedAt.Ticks}", flags))
            {
                float startY = ImGui.GetCursorPosY();
                float windowWidth = ImGui.GetWindowWidth();

                ImGui.SetCursorPos(new Vector2(windowWidth - 28, 8));
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.8f, 0.8f, 0.2f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.7f, 0.7f, 0.3f));
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, notification.Opacity));
                if (ImGui.Button("\u00D7##close"))
                {
                    notification.ExpiresAt = DateTime.Now;
                }
                ImGui.PopStyleColor(4);

                ImGui.SetCursorPos(new Vector2(16, startY));

                // Render notification type
                ImGui.PushStyleColor(ImGuiCol.Button, indicatorColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, indicatorColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, indicatorColor);
                ImGui.Button("##indicator", new Vector2(14, 14));
                ImGui.PopStyleColor(3);

                // Title
                ImGui.SameLine();
                ImGui.SetCursorPosY(startY - 2);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.1f, 0.1f, 0.1f, notification.Opacity));
                ImGui.Text(notification.Title);
                ImGui.PopStyleColor();

                // Separator
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8);
                ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.0f, 0.0f, 0.0f, 0.1f * notification.Opacity));
                ImGui.Separator();
                ImGui.PopStyleColor();

                // Message
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.2f, 0.2f, notification.Opacity));
                ImGui.TextWrapped(notification.Message);
                ImGui.PopStyleColor();
            }
            ImGui.End();

            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }
    }
}