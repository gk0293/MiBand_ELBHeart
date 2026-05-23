using System.Collections.ObjectModel;
using Heart.Collections;
using Heart.Models;
using Heart.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace Heart;

public partial class MainPage : ContentPage
{
    private readonly ObservableRangeCollection<BluetoothPeripheral> _devices = new();
    private IHeartRateMonitorService? _heartRateMonitorService;
    private BluetoothPeripheral? _selectedPeripheral;
    private bool _overlayEnabled;
    private bool _eventsSubscribed;
    private bool _autoReconnectAttempted;
    private int _currentHeartRate;

    private CollectionView DevicesCollection => this.FindByName<CollectionView>("DevicesCollectionView")!;
    private Button ConnectButton => this.FindByName<Button>("ConnectDeviceButton")!;
    private Button DisconnectButton => this.FindByName<Button>("DisconnectDeviceButton")!;
    private Button RememberedReconnectButton => this.FindByName<Button>("ReconnectRememberedButton")!;
    private Label BluetoothDeviceStatusLabel => this.FindByName<Label>("BluetoothDeviceLabel")!;
    private Label ConnectionStateLabel => this.FindByName<Label>("ConnectionStatusLabel")!;
    private Label BluetoothPermissionStateLabel => this.FindByName<Label>("BluetoothPermissionLabel")!;
    private Label RememberedDeviceStateLabel => this.FindByName<Label>("RememberedDeviceLabel")!;
    private Label BackgroundMonitoringStateLabel => this.FindByName<Label>("BackgroundMonitoringLabel")!;
    private Label ScanHintStatusLabel => this.FindByName<Label>("ScanHintLabel")!;
    private Editor ScanLogView => this.FindByName<Editor>("ScanLogEditor")!;
    private ActivityIndicator ScanBusyIndicator => this.FindByName<ActivityIndicator>("ScanningIndicator")!;

    public MainPage()
    {
        InitializeComponent();
        DevicesCollection.ItemsSource = _devices;
        UpdateHeartRate(0);
        UpdateOverlayState(false);
        UpdateConnectionState(null);
        UpdateBluetoothPermissionLabel("待检查", "#6B7280");
        UpdateRememberedDeviceState(null);
        UpdateBackgroundMonitoringState("未运行", "#DC2626");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        EnsureServiceSubscriptions();
        UpdateOverlayPermissionState();
        _ = InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_heartRateMonitorService is not null && _eventsSubscribed)
        {
            _heartRateMonitorService.HeartRateUpdated -= OnHeartRateUpdated;
            _heartRateMonitorService.ScanBatchReady -= OnScanBatchReady;
            _heartRateMonitorService.StatusMessageChanged -= OnStatusMessageChanged;
            _eventsSubscribed = false;
        }
    }

    private async Task InitializeAsync()
    {
        await RefreshBluetoothPermissionStateAsync();
        SyncConnectedDeviceState();
        UpdateRememberedDeviceState(_heartRateMonitorService?.RememberedDevice);
        await TryAutoReconnectRememberedDeviceAsync();
    }

    private void EnsureServiceSubscriptions()
    {
        if (_heartRateMonitorService is null)
        {
            _heartRateMonitorService = Handler?.MauiContext?.Services.GetService<IHeartRateMonitorService>();
        }

        if (_heartRateMonitorService is null || _eventsSubscribed)
        {
            return;
        }

        _heartRateMonitorService.HeartRateUpdated += OnHeartRateUpdated;
        _heartRateMonitorService.ScanBatchReady += OnScanBatchReady;
        _heartRateMonitorService.StatusMessageChanged += OnStatusMessageChanged;
        _eventsSubscribed = true;
    }

    private async void OnScanDevicesClicked(object sender, EventArgs e)
    {
        EnsureServiceSubscriptions();
        if (_heartRateMonitorService is null)
        {
            await DisplayAlert("初始化失败", "蓝牙服务未正确加载。", "确定");
            return;
        }

        var permissionGranted = await EnsureBluetoothPermissionsAsync();
        if (!permissionGranted)
        {
            return;
        }

        SetScanningState(true, "正在扫描附近心率设备...");
        ScanLogView.Text = $"[{DateTime.Now:HH:mm:ss}] 开始扫描 BLE 设备...{Environment.NewLine}";

        try
        {
            // 保存当前选中设备的唯一标识（Address）
            var previouslySelectedAddress = _selectedPeripheral?.Address;

            var devices = await _heartRateMonitorService.ScanForDevicesAsync(TimeSpan.FromSeconds(10));
            _devices.ReplaceAll(devices);

            // 尝试使用 Address 恢复选中（如果仍存在）
            if (!string.IsNullOrEmpty(previouslySelectedAddress))
            {
                var restored = _devices.FirstOrDefault(d => d.Address == previouslySelectedAddress);
                if (restored is not null)
                {
                    _selectedPeripheral = restored;
                    DevicesCollection.SelectedItem = restored;
                }
                else
                {
                    _selectedPeripheral = null;
                    DevicesCollection.SelectedItem = null;
                }
            }
            else
            {
                _selectedPeripheral = null;
                DevicesCollection.SelectedItem = null;
            }

            ConnectButton.IsEnabled = _selectedPeripheral is not null && !_selectedPeripheral.IsConnected;
            DisconnectButton.IsEnabled = _heartRateMonitorService.IsConnected;
            ScanHintStatusLabel.Text = devices.Count == 0 ? "未发现可用心率设备，可确认设备已开启并靠近手机。" : $"已发现 {devices.Count} 个设备，请选择后连接。";
        }
        finally
        {
            SetScanningState(false, ScanHintStatusLabel.Text);
        }
    }

    private async void OnReconnectRememberedClicked(object sender, EventArgs e)
    {
        EnsureServiceSubscriptions();
        if (_heartRateMonitorService?.RememberedDevice is null)
        {
            await DisplayAlert("暂无上次设备", "请先成功连接一次蓝牙心率设备。", "确定");
            return;
        }

        var permissionGranted = await EnsureBluetoothPermissionsAsync();
        if (!permissionGranted)
        {
            return;
        }

        UpdateBackgroundMonitoringState("自动重连中", "#2563EB");
        var connected = await _heartRateMonitorService.TryReconnectToRememberedDeviceAsync();
        if (connected)
        {
            SyncConnectedDeviceState();
        }
    }

    private async void OnConnectDeviceClicked(object sender, EventArgs e)
    {
        EnsureServiceSubscriptions();
        if (_heartRateMonitorService is null)
        {
            await DisplayAlert("初始化失败", "蓝牙服务未正确加载。", "确定");
            return;
        }

        if (_selectedPeripheral is null)
        {
            await DisplayAlert("请选择设备", "请先从设备列表中选择一个蓝牙心率设备。", "确定");
            return;
        }

        var permissionGranted = await EnsureBluetoothPermissionsAsync();
        if (!permissionGranted)
        {
            return;
        }

        UpdateBackgroundMonitoringState("连接中", "#2563EB");
        var connected = await _heartRateMonitorService.ConnectAsync(_selectedPeripheral);
        if (!connected)
        {
            return;
        }

        _autoReconnectAttempted = true;
        SyncConnectedDeviceState();
        UpdateRememberedDeviceState(_heartRateMonitorService.RememberedDevice);

        if (_overlayEnabled)
        {
#if ANDROID
            Platforms.Android.Services.HeartOverlayService.Start(global::Android.App.Application.Context, BuildOverlayText());
#endif
        }
    }

    private async void OnDisconnectDeviceClicked(object sender, EventArgs e)
    {
        EnsureServiceSubscriptions();
        if (_heartRateMonitorService is null)
        {
            return;
        }

        await _heartRateMonitorService.DisconnectAsync();
        UpdateConnectionState(null);
        UpdateBackgroundMonitoringState("未运行", "#DC2626");
        UpdateRememberedDeviceState(_heartRateMonitorService.RememberedDevice);

        if (_overlayEnabled)
        {
#if ANDROID
            Platforms.Android.Services.HeartOverlayService.Stop(global::Android.App.Application.Context);
#endif
            UpdateOverlayState(false);
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPeripheral = e.CurrentSelection.FirstOrDefault() as BluetoothPeripheral;
        ConnectButton.IsEnabled = _selectedPeripheral is not null && !_selectedPeripheral.IsConnected;
    }

    private void OnHeartRateUpdated(object? sender, int heartRate)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateHeartRate(heartRate);
            ConnectionStateLabel.Text = "已连接，正在接收心率数据";
            ConnectionStateLabel.TextColor = Color.FromArgb("#16A34A");
            UpdateBackgroundMonitoringState("监测中", "#16A34A");

            if (_overlayEnabled)
            {
#if ANDROID
                Platforms.Android.Services.HeartOverlayService.Start(global::Android.App.Application.Context, BuildOverlayText());
#endif
            }
        });
    }

    /// <summary>
    /// Raised by the PeriodicTimer on a background thread every 500 ms.
    /// Marshals to the UI thread and replaces the entire device list atomically
    /// with a single Reset notification — no per-item Add/Remove overhead.
    /// </summary>
    private void OnScanBatchReady(object? sender, IReadOnlyList<BluetoothPeripheral> batch)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 保存当前选中设备的唯一标识（Address）
            var previouslySelectedAddress = _selectedPeripheral?.Address;

            _devices.ReplaceAll(batch);
            AppendScanLog($"[{DateTime.Now:HH:mm:ss}] 批次刷新：{batch.Count} 个设备");

            // 恢复选中（如果仍在新列表中）
            if (!string.IsNullOrEmpty(previouslySelectedAddress))
            {
                var restored = _devices.FirstOrDefault(d => d.Address == previouslySelectedAddress);
                if (restored is not null)
                {
                    _selectedPeripheral = restored;
                    DevicesCollection.SelectedItem = restored;
                }
                else
                {
                    // 如果设备不存在，可以保留已连接设备为选中（可选逻辑）
                    _selectedPeripheral = null;
                    DevicesCollection.SelectedItem = null;
                }
            }

            // 更新 Connect 按钮状态
            ConnectButton.IsEnabled = _selectedPeripheral is not null && !_selectedPeripheral.IsConnected;
        });
    }

    private void OnStatusMessageChanged(object? sender, string message)
    {
        MainThread.BeginInvokeOnMainThread(() => ApplyStatusMessage(message));
    }

    private void SyncConnectedDeviceState()
    {
        EnsureServiceSubscriptions();
        UpdateConnectionState(_heartRateMonitorService?.ConnectedDevice);
        UpdateRememberedDeviceState(_heartRateMonitorService?.RememberedDevice);
    }

    private async Task TryAutoReconnectRememberedDeviceAsync()
    {
        EnsureServiceSubscriptions();
        if (_autoReconnectAttempted || _heartRateMonitorService is null || _heartRateMonitorService.IsConnected || _heartRateMonitorService.RememberedDevice is null)
        {
            return;
        }

#if ANDROID
        var hasPermission = await HasBluetoothPermissionsAsync();
        if (!hasPermission)
        {
            return;
        }
#endif

        _autoReconnectAttempted = true;
        UpdateBackgroundMonitoringState("自动重连中", "#2563EB");
        await _heartRateMonitorService.TryReconnectToRememberedDeviceAsync();
    }

    private void UpdateHeartRate(int heartRate)
    {
        _currentHeartRate = heartRate;
        HeartRateValueLabel.Text = heartRate > 0 ? heartRate.ToString() : "--";
        HeartRateUpdatedLabel.Text = heartRate > 0
            ? $"最后更新：{DateTime.Now:HH:mm:ss}"
            : "等待设备推送心率数据";
    }

    private void UpsertDiscoveredDevice(BluetoothPeripheral peripheral)
    {
        var existing = _devices.FirstOrDefault(device => device.Address == peripheral.Address);
        if (existing is null)
        {
            _devices.Add(peripheral);
            return;
        }

        existing.Name = peripheral.Name;
        existing.Rssi = peripheral.Rssi;
        existing.AdvertisedServices = peripheral.AdvertisedServices;
        existing.HasHeartRateService = peripheral.HasHeartRateService;
        existing.IsConnected = peripheral.IsConnected;
    }

    private void UpdateConnectionState(BluetoothPeripheral? peripheral)
    {
        var connected = peripheral is not null;
        BluetoothDeviceStatusLabel.Text = connected ? peripheral!.Name : "未连接";
        BluetoothDeviceStatusLabel.TextColor = connected ? Color.FromArgb("#111827") : Color.FromArgb("#DC2626");
        ConnectionStateLabel.Text = connected ? "已连接" : "未连接";
        ConnectionStateLabel.TextColor = connected ? Color.FromArgb("#16A34A") : Color.FromArgb("#DC2626");
        DisconnectButton.IsEnabled = connected;

        RefreshDeviceSelectionState(peripheral);

        if (connected)
        {
            UpdateBackgroundMonitoringState("监测中", "#16A34A");
        }
    }

    private void RefreshDeviceSelectionState(BluetoothPeripheral? peripheral)
    {
        var connected = peripheral is not null;
        foreach (var device in _devices)
        {
            device.IsConnected = connected && peripheral?.Address == device.Address;
        }

        if (connected)
        {
            _selectedPeripheral = peripheral;
            DevicesCollection.SelectedItem = peripheral;
        }

        ConnectButton.IsEnabled = _selectedPeripheral is not null && !_selectedPeripheral.IsConnected;
    }

    private void UpdateRememberedDeviceState(BluetoothPeripheral? peripheral)
    {
        RememberedDeviceStateLabel.Text = peripheral?.Name ?? "暂无";
        RememberedDeviceStateLabel.TextColor = peripheral is null ? Color.FromArgb("#6B7280") : Color.FromArgb("#111827");
        RememberedReconnectButton.IsEnabled = peripheral is not null && !(_heartRateMonitorService?.IsConnected ?? false);
    }

    private void ApplyStatusMessage(string message)
    {
        var isSuccess = message.Contains("已连接", StringComparison.OrdinalIgnoreCase) || message.Contains("完成", StringComparison.OrdinalIgnoreCase);
        var isReconnect = message.Contains("重连", StringComparison.OrdinalIgnoreCase);
        var isError = message.Contains("失败", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未", StringComparison.OrdinalIgnoreCase)
            || message.Contains("无法", StringComparison.OrdinalIgnoreCase)
            || message.Contains("取消", StringComparison.OrdinalIgnoreCase)
            || message.Contains("断开", StringComparison.OrdinalIgnoreCase);

        ConnectionStateLabel.Text = message;
        ConnectionStateLabel.TextColor = isSuccess
            ? Color.FromArgb("#16A34A")
            : isReconnect
                ? Color.FromArgb("#2563EB")
                : isError
                    ? Color.FromArgb("#DC2626")
                    : Color.FromArgb("#2563EB");

        if (isSuccess)
        {
            UpdateBackgroundMonitoringState("监测中", "#16A34A");
        }
        else if (isReconnect)
        {
            UpdateBackgroundMonitoringState("自动重连中", "#2563EB");
        }
        else if (isError && message.Contains("断开", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBackgroundMonitoringState("重连等待中", "#F59E0B");
        }

        UpdateRememberedDeviceState(_heartRateMonitorService?.RememberedDevice);
        RefreshDeviceSelectionState(_heartRateMonitorService?.ConnectedDevice);
    }

    private void UpdateOverlayState(bool isEnabled)
    {
        _overlayEnabled = isEnabled;
        OverlayStatusLabel.Text = isEnabled ? "已开启" : "未开启";
        OverlayStatusLabel.TextColor = isEnabled ? Color.FromArgb("#16A34A") : Color.FromArgb("#DC2626");
        ShowOverlayButton.IsEnabled = !isEnabled;
        HideOverlayButton.IsEnabled = isEnabled;
    }

    private void UpdateBackgroundMonitoringState(string text, string color)
    {
        BackgroundMonitoringStateLabel.Text = text;
        BackgroundMonitoringStateLabel.TextColor = Color.FromArgb(color);
    }

    private void UpdateBluetoothPermissionLabel(string text, string color)
    {
        BluetoothPermissionStateLabel.Text = text;
        BluetoothPermissionStateLabel.TextColor = Color.FromArgb(color);
    }

    private void SetScanningState(bool isScanning, string hint)
    {
        ScanBusyIndicator.IsVisible = isScanning;
        ScanBusyIndicator.IsRunning = isScanning;
        ScanHintStatusLabel.Text = hint;
    }

    private void AppendScanLog(string message)
    {
        var current = ScanLogView.Text ?? string.Empty;
        ScanLogView.Text = string.IsNullOrWhiteSpace(current)
            ? message
            : $"{current}{Environment.NewLine}{message}";

        var lines = ScanLogView.Text.Split(Environment.NewLine);
        if (lines.Length > 80)
        {
            ScanLogView.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 80));
        }
    }

    private async Task RefreshBluetoothPermissionStateAsync()
    {
#if ANDROID
        var granted = await HasBluetoothPermissionsAsync();
        UpdateBluetoothPermissionLabel(granted ? "已授权" : "未授权", granted ? "#16A34A" : "#DC2626");
#else
        UpdateBluetoothPermissionLabel("仅 Android", "#6B7280");
#endif
    }

    private void UpdateOverlayPermissionState()
    {
#if ANDROID
        var allowed = global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.M
            || global::Android.Provider.Settings.CanDrawOverlays(global::Android.App.Application.Context);
        OverlayPermissionLabel.Text = allowed ? "已授权" : "未授权";
        OverlayPermissionLabel.TextColor = allowed ? Color.FromArgb("#16A34A") : Color.FromArgb("#DC2626");
#else
        OverlayPermissionLabel.Text = "仅 Android";
        OverlayPermissionLabel.TextColor = Color.FromArgb("#6B7280");
#endif
    }

    private string BuildOverlayText() => _currentHeartRate > 0 ? $"Heart rate: {_currentHeartRate} bpm" : "Heart rate: -- bpm";

    private async void OnShowOverlayClicked(object sender, EventArgs e)
    {
#if ANDROID
        var notificationGranted = await EnsureNotificationPermissionAsync();
        if (!notificationGranted)
        {
            await DisplayAlert("权限未开启", "需要通知权限才能显示前台服务通知。", "确定");
            return;
        }

        var canDrawOverlays = await EnsureOverlayPermissionAsync();
        if (!canDrawOverlays)
        {
            UpdateOverlayPermissionState();
            return;
        }

        Platforms.Android.Services.HeartOverlayService.Start(global::Android.App.Application.Context, BuildOverlayText());
        UpdateOverlayState(true);
#else
        await DisplayAlert("暂不支持", "悬浮窗当前仅支持 Android。", "确定");
#endif
    }

    private void OnHideOverlayClicked(object sender, EventArgs e)
    {
#if ANDROID
        Platforms.Android.Services.HeartOverlayService.Stop(global::Android.App.Application.Context);
#endif
        UpdateOverlayState(false);
    }

#if ANDROID
    private async Task<bool> EnsureBluetoothPermissionsAsync()
    {
        var granted = await HasBluetoothPermissionsAsync();
        if (granted)
        {
            UpdateBluetoothPermissionLabel("已授权", "#16A34A");
            return true;
        }

        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
        {
            var scanStatus = await Permissions.RequestAsync<Platforms.Android.Permissions.BluetoothScanPermission>();
            var connectStatus = await Permissions.RequestAsync<Platforms.Android.Permissions.BluetoothConnectPermission>();
            // Android 12+ 即使声明了 neverForLocation，部分厂商 ROM（如 MIUI）仍需位置权限才能回调
            // 此处额外请求，用户拒绝不影响主流程，仅作兼容性兜底
            _ = await Permissions.RequestAsync<Platforms.Android.Permissions.FineLocationPermission>();
            granted = scanStatus == PermissionStatus.Granted && connectStatus == PermissionStatus.Granted;
        }
        else
        {
            var locationStatus = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            granted = locationStatus == PermissionStatus.Granted;
        }

        UpdateBluetoothPermissionLabel(granted ? "已授权" : "未授权", granted ? "#16A34A" : "#DC2626");

        if (!granted)
        {
            await DisplayAlert("需要蓝牙权限", "请允许蓝牙扫描/连接权限后再继续。", "确定");
        }

        return granted;
    }

    private static async Task<bool> HasBluetoothPermissionsAsync()
    {
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S)
        {
            var scanStatus = await Permissions.CheckStatusAsync<Platforms.Android.Permissions.BluetoothScanPermission>();
            var connectStatus = await Permissions.CheckStatusAsync<Platforms.Android.Permissions.BluetoothConnectPermission>();
            return scanStatus == PermissionStatus.Granted && connectStatus == PermissionStatus.Granted;
        }

        var locationStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        return locationStatus == PermissionStatus.Granted;
    }

    private static async Task<bool> EnsureNotificationPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (status == PermissionStatus.Granted)
        {
            return true;
        }

        status = await Permissions.RequestAsync<Permissions.PostNotifications>();
        return status == PermissionStatus.Granted;
    }

    private async Task<bool> EnsureOverlayPermissionAsync()
    {
        var context = global::Android.App.Application.Context;
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.M
            || global::Android.Provider.Settings.CanDrawOverlays(context))
        {
            return true;
        }

        await DisplayAlert("需要悬浮窗权限", "请在接下来的系统页面中允许“显示在其他应用的上层”，然后返回应用重新点击开启。", "知道了");

        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionManageOverlayPermission,
            global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);
        return false;
    }
#else
    private Task<bool> EnsureBluetoothPermissionsAsync()
    {
        UpdateBluetoothPermissionLabel("仅 Android", "#6B7280");
        return Task.FromResult(false);
    }
#endif

    // 在 MainPage 类内新增方法并在开始扫描前调用（例如在 OnScanDevicesClicked 的开头）
    private async Task AppendBluetoothDiagnosticsAsync()
    {
        var ctx = global::Android.App.Application.Context;
        // Get the BluetoothManager from system services and obtain the adapter from it.
        // On newer Android versions GetSystemService(Context.BluetoothService) returns a BluetoothManager,
        // so casting directly to BluetoothAdapter can cause InvalidCastException.
        var btManager = ctx.GetSystemService(global::Android.Content.Context.BluetoothService) as global::Android.Bluetooth.BluetoothManager;
        var adapter = btManager?.Adapter ?? global::Android.Bluetooth.BluetoothAdapter.DefaultAdapter;

        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] BluetoothAdapter present: {adapter != null}");
        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] Bluetooth Enabled: {(adapter?.IsEnabled == true)}");

#if ANDROID
        // 运行时权限检查
        var hasScan = await Permissions.CheckStatusAsync<Platforms.Android.Permissions.BluetoothScanPermission>();
        var hasConnect = await Permissions.CheckStatusAsync<Platforms.Android.Permissions.BluetoothConnectPermission>();
        var hasLocation = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] Permission BLUETOOTH_SCAN: {hasScan}");
        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] Permission BLUETOOTH_CONNECT: {hasConnect}");
        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] Permission LocationWhenInUse: {hasLocation}");
#endif

        // 检查 BluetoothLeScanner 可用性
        try
        {
            var scanner = adapter?.BluetoothLeScanner;
            AppendScanLog($"[{DateTime.Now:HH:mm:ss}] BluetoothLeScanner present: {scanner != null}");
        }
        catch (Exception ex)
        {
            AppendScanLog($"[{DateTime.Now:HH:mm:ss}] BluetoothLeScanner check failed: {ex.Message}");
        }

        // 额外提醒
        AppendScanLog($"[{DateTime.Now:HH:mm:ss}] 请确认系统位置服务已开启（Settings > Location）。");
    }
}
