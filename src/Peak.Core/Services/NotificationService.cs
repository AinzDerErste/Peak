using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Peak.Core.Services;

public class NotificationService : IDisposable
{
    private UserNotificationListener? _listener;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private readonly HashSet<uint> _seenIds = new();
    private const int MaxSeenIds = 512;

    public event Action<Models.NotificationData>? NewNotification;
    public bool IsAvailable { get; private set; }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            IsAvailable = access == UserNotificationListenerAccessStatus.Allowed;

            if (IsAvailable)
            {
                // Prime the seen set with everything currently in the Action Center so
                // we don't replay historical notifications on startup.
                try
                {
                    var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    foreach (var n in existing)
                        _seenIds.Add(n.Id);
                }
                catch { /* ignore — first poll will catch up */ }
            }

            return IsAvailable;
        }
        catch
        {
            IsAvailable = false;
            return false;
        }
    }

    public void StartPolling()
    {
        if (!IsAvailable || _listener == null) return;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        _ = PollAsync(_cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                var notifications = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var notif in notifications)
                {
                    if (_seenIds.Contains(notif.Id)) continue;
                    if (_seenIds.Count >= MaxSeenIds) _seenIds.Clear();
                    _seenIds.Add(notif.Id);

                    var binding = notif.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding == null) continue;

                    var texts = binding.GetTextElements().ToList();
                    byte[]? iconBytes = null;
                    try
                    {
                        var logo = notif.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(32, 32));
                        if (logo != null)
                        {
                            using var stream = await logo.OpenReadAsync();
                            using var ms = new System.IO.MemoryStream();
                            var buf = new byte[4096];
                            var inputStream = stream.AsStreamForRead();
                            int read;
                            while ((read = await inputStream.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                                ms.Write(buf, 0, read);
                            iconBytes = ms.ToArray();
                        }
                    }
                    catch { /* icon extraction failed, continue without */ }

                    var data = new Models.NotificationData
                    {
                        Id = notif.Id,
                        AppName = notif.AppInfo?.DisplayInfo?.DisplayName ?? "Unknown",
                        AppUserModelId = notif.AppInfo?.AppUserModelId ?? "",
                        Title = texts.Count > 0 ? texts[0].Text : "",
                        Body = texts.Count > 1 ? texts[1].Text : "",
                        Timestamp = notif.CreationTime,
                        IconBytes = iconBytes
                    };

                    NewNotification?.Invoke(data);
                }
            }
            catch { /* Ignore transient errors */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }
}
