#nullable enable

using System;
using System.Collections.Concurrent;
using System.Windows.Automation;

public sealed class BamSmokeNotifications : IDisposable
{
    private readonly AutomationElement _element;
    private readonly AutomationEventHandler _handler;
    private readonly ConcurrentQueue<NotificationEventArgs> _messages = new();

    public BamSmokeNotifications(AutomationElement element)
    {
        _element = element;
        _handler = (_, args) =>
        {
            if (args is NotificationEventArgs notification) _messages.Enqueue(notification);
        };
        Automation.AddAutomationEventHandler(AutomationElement.NotificationEvent, _element, TreeScope.Element, _handler);
    }

    public int Count => _messages.Count;

    public NotificationEventArgs? Take() => _messages.TryDequeue(out var notification) ? notification : null;

    public void Dispose() => Automation.RemoveAutomationEventHandler(AutomationElement.NotificationEvent, _element, _handler);
}
