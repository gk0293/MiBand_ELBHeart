using AApp = global::Android.App;
using AContent = global::Android.Content;
using AOS = global::Android.OS;
using AndroidX.Core.App;
using Resource = Microsoft.Maui.Resource;

namespace Heart.Platforms.Android.Services;

[AApp.Service(
    Exported = false,
    ForegroundServiceType =
        global::Android.Content.PM.ForegroundService.TypeConnectedDevice)]
public sealed class HeartMonitoringForegroundService : AApp.Service
{
    private const string ChannelId = "heart_monitor_channel";
    private const int NotificationId = 10002;
    private const string ActionStart = "Heart.Monitor.Start";
    private const string ActionStop = "Heart.Monitor.Stop";
    private const string ExtraTitle = "monitor_title";
    private const string ExtraText = "monitor_text";

    public override AOS.IBinder? OnBind(AContent.Intent? intent) => null;

    public override AApp.StartCommandResult OnStartCommand(AContent.Intent? intent, AApp.StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionStart;
        if (action == ActionStop)
        {
            StopForeground(true);
            StopSelf();
            return AApp.StartCommandResult.NotSticky;
        }

        var title = intent?.GetStringExtra(ExtraTitle) ?? "Heart monitoring";
        var text = intent?.GetStringExtra(ExtraText) ?? "Waiting for heart-rate data";

        CreateNotificationChannel();
        var notification = BuildNotification(title, text);

        // Android 10 (API 29)+ 要求 StartForeground 传入 foregroundServiceType
        // Android 14 (API 34)+ 在 targetSdk=34 时强制执行，否则抛出 MissingForegroundServiceTypeException
        if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.Q)
        {
            StartForeground(
      NotificationId,
      notification,
      global::Android.Content.PM.ForegroundService.TypeConnectedDevice);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        NotificationManagerCompat.From(this).Notify(NotificationId, notification);
        return AApp.StartCommandResult.Sticky;
    }

    public static void Start(AContent.Context context, string title, string text)
    {
        var intent = new AContent.Intent(context, typeof(HeartMonitoringForegroundService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraTitle, title);
        intent.PutExtra(ExtraText, text);

        if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop(AContent.Context context)
    {
        var intent = new AContent.Intent(context, typeof(HeartMonitoringForegroundService));
        intent.SetAction(ActionStop);
        context.StartService(intent);
    }

    private AApp.Notification BuildNotification(string title, string text)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName);
        var pendingIntent = AApp.PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            AApp.PendingIntentFlags.UpdateCurrent | AApp.PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(pendingIntent)
            .Build();
    }

    private void CreateNotificationChannel()
    {
        if (AOS.Build.VERSION.SdkInt < AOS.BuildVersionCodes.O)
        {
            return;
        }

        var manager = (AApp.NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        var channel = new AApp.NotificationChannel(ChannelId, "Heart monitoring", AApp.NotificationImportance.Low)
        {
            Description = "Keeps heart-rate monitoring alive in the background"
        };

        manager?.CreateNotificationChannel(channel);
    }
}
