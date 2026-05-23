using AApp = global::Android.App;
using AContent = global::Android.Content;
using AGraphics = global::Android.Graphics;
using AOS = global::Android.OS;
using AViews = global::Android.Views;
using AWidget = global::Android.Widget;
using AndroidX.Core.App;
using Resource = Microsoft.Maui.Resource;

namespace Heart.Platforms.Android.Services;

[AApp.Service(Exported = false)]
public sealed class HeartOverlayService : AApp.Service
{
    private const string ChannelId = "heart_overlay_channel";
    private const int NotificationId = 10001;
    private const string ActionShow = "Heart.Overlay.Show";
    private const string ActionHide = "Heart.Overlay.Hide";
    private const string ExtraText = "overlay_text";

    private AViews.IWindowManager? _windowManager;
    private AViews.View? _overlayView;
    private AWidget.TextView? _textView;

    public override AOS.IBinder? OnBind(AContent.Intent? intent) => null;

    public override AApp.StartCommandResult OnStartCommand(AContent.Intent? intent, AApp.StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionShow;
        if (action == ActionHide)
        {
            StopForeground(true);
            StopSelf();
            return AApp.StartCommandResult.NotSticky;
        }

        var text = intent?.GetStringExtra(ExtraText) ?? "Heart rate: -- bpm";
        CreateNotificationChannel();
        StartForeground(NotificationId, BuildNotification(text));
        EnsureOverlayView();
        UpdateText(text);
        return AApp.StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_overlayView is not null && _windowManager is not null)
        {
            _windowManager.RemoveView(_overlayView);
        }

        _overlayView?.Dispose();
        _overlayView = null;
        _textView = null;
        _windowManager = null;
        base.OnDestroy();
    }

    public static void Start(AContent.Context context, string text)
    {
        var intent = new AContent.Intent(context, typeof(HeartOverlayService));
        intent.SetAction(ActionShow);
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
        var intent = new AContent.Intent(context, typeof(HeartOverlayService));
        intent.SetAction(ActionHide);
        context.StartService(intent);
    }

    private void EnsureOverlayView()
    {
        if (_overlayView is not null)
        {
            return;
        }

        _windowManager = GetSystemService(WindowService) as AViews.IWindowManager;
        if (_windowManager is null)
        {
            return;
        }

        var layout = new AWidget.LinearLayout(this)
        {
            Orientation = AWidget.Orientation.Vertical
        };
        layout.SetPadding(28, 18, 28, 18);
        layout.SetBackgroundColor(AGraphics.Color.Argb(220, 17, 24, 39));

        _textView = new AWidget.TextView(this)
        {
            Text = "Heart rate: -- bpm",
            Gravity = AViews.GravityFlags.CenterVertical
        };
        _textView.SetTextColor(AGraphics.Color.White);
        _textView.TextSize = 24f;
        _textView.SetTypeface(AGraphics.Typeface.DefaultBold, AGraphics.TypefaceStyle.Bold);
        layout.AddView(_textView);

        var layoutParams = new AViews.WindowManagerLayoutParams(
            AViews.ViewGroup.LayoutParams.WrapContent,
            AViews.ViewGroup.LayoutParams.WrapContent,
            AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.O ? AViews.WindowManagerTypes.ApplicationOverlay : AViews.WindowManagerTypes.Phone,
            AViews.WindowManagerFlags.NotFocusable | AViews.WindowManagerFlags.LayoutInScreen,
            AGraphics.Format.Translucent)
        {
            Gravity = AViews.GravityFlags.Top | AViews.GravityFlags.End,
            X = 32,
            Y = 180
        };

        _overlayView = layout;
        _windowManager.AddView(_overlayView, layoutParams);
    }

    private void UpdateText(string text)
    {
        _textView?.Post(() => _textView.Text = text);
        NotificationManagerCompat.From(this).Notify(NotificationId, BuildNotification(text));
    }

    private AApp.Notification BuildNotification(string text)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName);
        var pendingIntent = AApp.PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            AApp.PendingIntentFlags.UpdateCurrent | AApp.PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Heart overlay running")
            .SetContentText(text)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
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

        var channel = new AApp.NotificationChannel(ChannelId, "Heart overlay", AApp.NotificationImportance.Low)
        {
            Description = "Shows the current heart rate overlay"
        };

        manager?.CreateNotificationChannel(channel);
    }
}
