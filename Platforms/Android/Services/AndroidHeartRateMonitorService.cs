using Android.Bluetooth;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Locations;
using Android.Util;
using Heart.Models;
using Heart.Services;
using Java.Util;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Heart.Platforms.Android.Services;

public sealed class AndroidHeartRateMonitorService : Java.Lang.Object, IHeartRateMonitorService
{
    // ── BLE UUIDs ────────────────────────────────────────────────────────────
    private static readonly UUID HeartRateServiceUuid =
        UUID.FromString("0000180D-0000-1000-8000-00805F9B34FB");
    private static readonly UUID HeartRateMeasurementUuid =
        UUID.FromString("00002A37-0000-1000-8000-00805F9B34FB");
    private static readonly UUID ClientCharacteristicConfigurationUuid =
        UUID.FromString("00002902-0000-1000-8000-00805F9B34FB");

    // ── Preferences keys ─────────────────────────────────────────────────────
    private const string RememberedAddressKey = "heart.remembered.address";
    private const string RememberedNameKey = "heart.remembered.name";

    /// <summary>
    /// How often the PeriodicTimer fires a batch UI update during an active scan.
    /// 500 ms keeps the list feeling live while capping UI work to ≤2 renders/sec.
    /// </summary>
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(500);

    // ── BLE infrastructure ───────────────────────────────────────────────────
    private readonly BluetoothManager? _bluetoothManager;
    private readonly BluetoothAdapter? _bluetoothAdapter;
    private readonly DeviceScanCallback _scanCallback;

    // ── Scan state — written from BLE callback thread, read from timer thread ─
    // ConcurrentDictionary gives lock-free per-key upserts from the BLE thread.
    private readonly ConcurrentDictionary<string, BluetoothPeripheral> _scanCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Native device map still needs a lock because BluetoothDevice is not thread-safe.
    private readonly object _nativeDevicesLock = new();
    private readonly Dictionary<string, BluetoothDevice> _nativeDevices =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Batch-timer lifecycle ────────────────────────────────────────────────
    private CancellationTokenSource? _batchCts;
    private Task? _batchTask;

    // ── GATT / reconnect state ───────────────────────────────────────────────
    private readonly object _gattLock = new();
    private BluetoothGatt? _bluetoothGatt;
    private TaskCompletionSource<bool>? _connectTcs;
    private CancellationTokenSource? _autoReconnectCts;
    private Task? _autoReconnectTask;
    private BluetoothPeripheral? _pendingPeripheral;
    private int _currentHeartRate;
    private bool _manualDisconnectRequested;

    public AndroidHeartRateMonitorService()
    {
        var context = global::Android.App.Application.Context;
        _bluetoothManager = context.GetSystemService(global::Android.Content.Context.BluetoothService) as BluetoothManager;
        _bluetoothAdapter = _bluetoothManager?.Adapter;
        _scanCallback = new DeviceScanCallback(this);
        RememberedDevice = LoadRememberedDevice();
    }

    public event EventHandler<int>? HeartRateUpdated;
    public event EventHandler<IReadOnlyList<BluetoothPeripheral>>? ScanBatchReady;
    public event EventHandler<string>? StatusMessageChanged;

    public BluetoothPeripheral? ConnectedDevice { get; private set; }

    public BluetoothPeripheral? RememberedDevice { get; private set; }

    public bool IsConnected => ConnectedDevice is not null;

    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanForDevicesAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default)
    {
        if (_bluetoothAdapter is null)
        {
            RaiseStatus("当前设备不支持蓝牙。", updateMonitoring: false);
            Log.Error("BLE_SCAN", "BluetoothAdapter is null");
            return Array.Empty<BluetoothPeripheral>();
        }

        if (!_bluetoothAdapter.IsEnabled)
        {
            RaiseStatus("蓝牙未开启，请先打开系统蓝牙。", updateMonitoring: false);
            Log.Error("BLE_SCAN", "BluetoothAdapter is not enabled");
            return Array.Empty<BluetoothPeripheral>();
        }

        // 诊断：检查系统定位服务（未开启时 BLE 扫描 callback 在 Android 12+ 不触发）
        var locationManager = global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.LocationService)
            as LocationManager;
        var isLocationEnabled = locationManager?.IsLocationEnabled ?? false;
        Log.Debug("BLE_SCAN", $"System location enabled: {isLocationEnabled}");

        // 诊断：确认运行时权限状态
        var scanPermResult = global::Android.App.Application.Context
            .CheckSelfPermission(global::Android.Manifest.Permission.BluetoothScan);
        var locationPermResult = global::Android.App.Application.Context
            .CheckSelfPermission(global::Android.Manifest.Permission.AccessFineLocation);
        var sdkInt = (int)global::Android.OS.Build.VERSION.SdkInt;
        Log.Debug("BLE_SCAN", $"BLUETOOTH_SCAN: {scanPermResult} | ACCESS_FINE_LOCATION: {locationPermResult} | SDK: {sdkInt}");

        var scanner = _bluetoothAdapter.BluetoothLeScanner;
        if (scanner is null)
        {
            RaiseStatus("无法启动蓝牙扫描。", updateMonitoring: false);
            Log.Error("BLE_SCAN", "BluetoothLeScanner is null");
            return Array.Empty<BluetoothPeripheral>();
        }

        // 清空上一次扫描缓存，保留已连接设备条目
        _scanCache.Clear();
        if (ConnectedDevice is not null)
        {
            _scanCache[ConnectedDevice.Address] = ConnectedDevice;
        }

        lock (_nativeDevicesLock)
        {
            _nativeDevices.Clear();
        }

        RaiseStatus("正在扫描心率设备...", updateMonitoring: false);

        // 启动批量刷新计时器
        StopBatchTimer();
        _batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _batchTask = RunBatchTimerAsync(_batchCts.Token);

        try
        {
            var settings = new ScanSettings.Builder()
                .SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)
                .Build();

            // ScanFilter：仅接收广播了心率服务 UUID 的设备，减少无关回调
            var filters = new List<ScanFilter>
            {
                new ScanFilter.Builder()
                    .SetServiceUuid(global::Android.OS.ParcelUuid.FromString(HeartRateServiceUuid.ToString()))
                    .Build()
            };

            // StartScan 必须在主线程调用；部分厂商 ROM（小米/MIUI）在非主线程调用时 callback 永远不触发
            var startTcs = new TaskCompletionSource<bool>();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    Log.Debug("BLE_SCAN", "Calling scanner.StartScan() on main thread...");
                    scanner.StartScan(filters, settings, _scanCallback);
                    Log.Debug("BLE_SCAN", "scanner.StartScan() called successfully");
                }
                catch (Exception startEx)
                {
                    Log.Error("BLE_SCAN", $"scanner.StartScan() threw: {startEx.Message}");
                }
                finally
                {
                    startTcs.TrySetResult(true);
                }
            });
            await startTcs.Task;
            await Task.Delay(scanDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RaiseStatus("扫描已取消。", updateMonitoring: false);
        }
        catch (Exception ex)
        {
            RaiseStatus($"扫描失败：{ex.Message}", updateMonitoring: false);
            Log.Error("BLE_SCAN", $"ScanForDevicesAsync exception: {ex}");
        }
        finally
        {
            TryStopScan();
            StopBatchTimer();
        }

        // 返回最终排序快照（连接优先 → 记忆设备优先 → RSSI 降序 → 取前 N 个）
        var discovered = BuildSortedSnapshot();

        RaiseStatus(discovered.Count == 0 ? "未发现蓝牙心率设备。" : $"扫描完成，发现 {discovered.Count} 个设备。", updateMonitoring: false);
        return discovered;
    }

    public Task<bool> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken = default)
    {
        return ConnectCoreAsync(ClonePeripheral(peripheral), isAutoReconnect: false, cancellationToken, cancelReconnectLoop: true);
    }

    public async Task<bool> TryReconnectToRememberedDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (RememberedDevice is null)
        {
            RaiseStatus("暂无上次连接设备。", updateMonitoring: false);
            return false;
        }

        return await ConnectCoreAsync(ClonePeripheral(RememberedDevice), isAutoReconnect: true, cancellationToken, cancelReconnectLoop: true);
    }

    public Task DisconnectAsync()
    {
        return DisconnectCoreAsync(isManual: true, raiseStatus: true);
    }

    private async Task<bool> ConnectCoreAsync(BluetoothPeripheral peripheral, bool isAutoReconnect, CancellationToken cancellationToken, bool cancelReconnectLoop)
    {
        if (_bluetoothAdapter is null || !_bluetoothAdapter.IsEnabled)
        {
            RaiseStatus(isAutoReconnect ? "蓝牙未开启，无法自动重连。" : "蓝牙未开启，无法连接设备。");
            return false;
        }

        if (ConnectedDevice?.Address == peripheral.Address)
        {
            RaiseStatus($"已连接到 {peripheral.Name}。");
            return true;
        }

        if (cancelReconnectLoop)
        {
            CancelAutoReconnect();
        }

        _manualDisconnectRequested = false;
        TryStopScan();
        await DisconnectCoreAsync(isManual: false, raiseStatus: false);

        BluetoothDevice? nativeDevice;
        lock (_nativeDevicesLock)
        {
            _nativeDevices.TryGetValue(peripheral.Address, out nativeDevice);
        }

        nativeDevice ??= _bluetoothAdapter.GetRemoteDevice(peripheral.Address);
        if (nativeDevice is null)
        {
            RaiseStatus("未找到要连接的蓝牙设备。");
            return false;
        }

        _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPeripheral = peripheral;

        RaiseStatus(isAutoReconnect ? $"正在自动重连 {peripheral.Name}..." : $"正在连接 {peripheral.Name}...");

        var callback = new DeviceGattCallback(this, peripheral);
        _bluetoothGatt = global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.M
            ? nativeDevice.ConnectGatt(global::Android.App.Application.Context, false, callback, BluetoothTransports.Le)
            : nativeDevice.ConnectGatt(global::Android.App.Application.Context, false, callback);

        using var registration = cancellationToken.Register(() => _connectTcs?.TrySetCanceled(cancellationToken));

        try
        {
            return await _connectTcs.Task;
        }
        catch (OperationCanceledException)
        {
            await DisconnectCoreAsync(isManual: false, raiseStatus: false);
            RaiseStatus(isAutoReconnect ? "自动重连已取消。" : "连接已取消。", updateMonitoring: false);
            return false;
        }
        finally
        {
            _connectTcs = null;
        }
    }

    private Task DisconnectCoreAsync(bool isManual, bool raiseStatus)
    {
        if (isManual)
        {
            _manualDisconnectRequested = true;
            CancelAutoReconnect();
        }

        // Mark every cached entry as disconnected
        foreach (var device in _scanCache.Values)
        {
            device.IsConnected = false;
        }

        var gatt = _bluetoothGatt;
        _bluetoothGatt = null;
        ConnectedDevice = null;
        _pendingPeripheral = null;
        _currentHeartRate = 0;

        if (gatt is not null)
        {
            try
            {
                gatt.Disconnect();
                gatt.Close();
            }
            catch
            {
            }
        }

        if (isManual)
        {
            StopBackgroundMonitoring();
            if (raiseStatus)
            {
                RaiseStatus("已断开当前设备。", updateMonitoring: false);
            }
        }

        return Task.CompletedTask;
    }

    // ── Batch timer ───────────────────────────────────────────────────────────

    /// <summary>
    /// Background loop that fires <see cref="ScanBatchReady"/> every
    /// <see cref="BatchInterval"/> ms so the UI thread is only woken ≤2 times/sec
    /// regardless of how frequently the BLE stack calls OnScanResult.
    /// </summary>
    private async Task RunBatchTimerAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(BatchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var batch = BuildSortedSnapshot();
                ScanBatchReady?.Invoke(this, batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation when scan ends
        }
    }

    private void StopBatchTimer()
    {
        if (_batchCts is null)
        {
            return;
        }

        try
        {
            _batchCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            _batchTask = null;
        }
    }

    /// <summary>
    /// Produces a stable sorted snapshot from <see cref="_scanCache"/>:
    /// connected first → remembered next → RSSI descending → top <see cref="IHeartRateMonitorService.MaxScanResults"/>.
    /// </summary>
    private List<BluetoothPeripheral> BuildSortedSnapshot()
    {
        return _scanCache.Values
            .OrderByDescending(d => d.IsConnected)
            .ThenByDescending(d => d.Address == RememberedDevice?.Address)
            .ThenByDescending(d => d.Rssi)
            .Take(IHeartRateMonitorService.MaxScanResults)
            .Select(ClonePeripheral)
            .ToList();
    }

    // ── BLE scan result handling ──────────────────────────────────────────────

    /// <summary>
    /// Called from the BLE callback thread. Upserts into <see cref="_scanCache"/>
    /// only — no UI work happens here. The PeriodicTimer thread will pick it up.
    /// </summary>
    private void HandleScanResult(ScanResult? result)
    {
        var nativeDevice = result?.Device;
        if (nativeDevice?.Address is null)
        {
            return;
        }

        Log.Debug("BLE_SCAN", $"OnScanResult: {nativeDevice.Name ?? "null"} [{nativeDevice.Address}] RSSI={result?.Rssi}");

        var advertisedServices = GetAdvertisedServices(result);
        var hasHeartRateService = advertisedServices.Any(service => string.Equals(service, HeartRateServiceUuid.ToString(), StringComparison.OrdinalIgnoreCase));
        var advertisedServicesDisplay = advertisedServices.Count == 0
            ? "未广播服务 UUID"
            : string.Join(", ", advertisedServices);

        // ConcurrentDictionary.AddOrUpdate is atomic per-key — no lock needed here.
        _scanCache.AddOrUpdate(
            nativeDevice.Address,
            addValueFactory: _ => new BluetoothPeripheral
            {
                Address = nativeDevice.Address,
                Name = GetDeviceName(nativeDevice),
                Rssi = result?.Rssi ?? 0,
                AdvertisedServices = advertisedServicesDisplay,
                HasHeartRateService = hasHeartRateService,
                IsConnected = ConnectedDevice?.Address == nativeDevice.Address
            },
            updateValueFactory: (_, existing) =>
            {
                existing.Name = GetDeviceName(nativeDevice);
                existing.Rssi = result?.Rssi ?? existing.Rssi;
                existing.AdvertisedServices = advertisedServicesDisplay;
                existing.HasHeartRateService = hasHeartRateService;
                existing.IsConnected = ConnectedDevice?.Address == nativeDevice.Address;
                return existing;
            });

        lock (_nativeDevicesLock)
        {
            _nativeDevices[nativeDevice.Address] = nativeDevice;
        }
        // No ScanBatchReady raised here — the PeriodicTimer handles that.

    }

    private void HandleScanFailed(ScanFailure errorCode)
    {
        // OnScanFailed 触发说明 StartScan 被系统拒绝，常见原因：
        // ScanningTooFrequently(2): 30秒内超过5次启动扫描（Android 7+限制）
        // ApplicationRegistrationFailed(1): 权限不足或定位未开启
        // FeatureUnsupported(4): 硬件不支持
        global::Android.Util.Log.Error("BLE_SCAN", $"OnScanFailed: errorCode={errorCode} ({(int)errorCode})");
        RaiseStatus($"蓝牙扫描失败：{errorCode}（代码 {(int)errorCode}）", updateMonitoring: false);
    }

    private void HandleConnected(BluetoothPeripheral peripheral)
    {
        foreach (var device in _scanCache.Values)
        {
            device.IsConnected = device.Address == peripheral.Address;
        }

        peripheral.IsConnected = true;
        ConnectedDevice = peripheral;
        RememberedDevice = ClonePeripheral(peripheral);
        SaveRememberedDevice(peripheral);
        StartBackgroundMonitoring(BuildMonitoringText($"已连接 {peripheral.Name}"));
        RaiseStatus($"已连接 {peripheral.Name}，正在接收心率数据。");
        _connectTcs?.TrySetResult(true);
    }

    private void HandleDisconnected(string statusMessage)
    {
        foreach (var device in _scanCache.Values)
        {
            device.IsConnected = false;
        }

        ConnectedDevice = null;
        _pendingPeripheral = null;
        _currentHeartRate = 0;

        if (_bluetoothGatt is not null)
        {
            try
            {
                _bluetoothGatt.Close();
            }
            catch
            {
            }
            finally
            {
                _bluetoothGatt = null;
            }
        }

        var shouldReconnect = !_manualDisconnectRequested && RememberedDevice is not null;
        if (shouldReconnect)
        {
            StartBackgroundMonitoring(BuildMonitoringText("设备断开，正在自动重连"));
        }
        else
        {
            StopBackgroundMonitoring();
        }

        RaiseStatus(statusMessage, updateMonitoring: shouldReconnect);
        _connectTcs?.TrySetResult(false);

        if (shouldReconnect)
        {
            ScheduleAutoReconnect();
        }
    }

    private void HandleServicesDiscovered(BluetoothGatt? gatt, BluetoothPeripheral peripheral, GattStatus status)
    {
        if (gatt is null || status != GattStatus.Success)
        {
            HandleDisconnected($"服务发现失败：{status}");
            return;
        }

        var service = gatt.GetService(HeartRateServiceUuid);
        var characteristic = service?.GetCharacteristic(HeartRateMeasurementUuid);
        if (characteristic is null)
        {
            HandleDisconnected("连接成功，但设备未提供标准心率服务，请确认这是心率设备。");
            return;
        }

        if (!gatt.SetCharacteristicNotification(characteristic, true))
        {
            HandleDisconnected("无法订阅心率通知。");
            return;
        }

        var descriptor = characteristic.GetDescriptor(ClientCharacteristicConfigurationUuid);
        if (descriptor is null)
        {
            HandleConnected(peripheral);
            return;
        }

        descriptor.SetValue(BluetoothGattDescriptor.EnableNotificationValue.ToArray());
        if (!gatt.WriteDescriptor(descriptor))
        {
            HandleDisconnected("无法启用心率通知。");
        }
    }

    private void HandleDescriptorWritten(BluetoothPeripheral peripheral, GattStatus status)
    {
        if (status == GattStatus.Success)
        {
            HandleConnected(peripheral);
            return;
        }

        HandleDisconnected($"启用心率通知失败：{status}");
    }

    private void HandleCharacteristicChanged(BluetoothGattCharacteristic? characteristic)
    {
        var heartRate = ParseHeartRate(characteristic);
        if (heartRate <= 0)
        {
            return;
        }

        _currentHeartRate = heartRate;
        HeartRateUpdated?.Invoke(this, heartRate);
        UpdateBackgroundMonitoring(BuildMonitoringText());
    }

    private void ScheduleAutoReconnect()
    {
        if (RememberedDevice is null)
        {
            return;
        }

        if (_autoReconnectTask is not null && !_autoReconnectTask.IsCompleted)
        {
            return;
        }

        _autoReconnectCts = new CancellationTokenSource();
        var token = _autoReconnectCts.Token;
        var device = ClonePeripheral(RememberedDevice);

        _autoReconnectTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !_manualDisconnectRequested && device is not null)
            {
                if (_bluetoothAdapter?.IsEnabled != true)
                {
                    RaiseStatus("蓝牙未开启，等待自动重连...", updateMonitoring: true);
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    continue;
                }

                var connected = await ConnectCoreAsync(ClonePeripheral(device), isAutoReconnect: true, token, cancelReconnectLoop: false);
                if (connected)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }, token);
    }

    private void CancelAutoReconnect()
    {
        if (_autoReconnectCts is null)
        {
            return;
        }

        try
        {
            _autoReconnectCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            _autoReconnectCts.Dispose();
            _autoReconnectCts = null;
            _autoReconnectTask = null;
        }
    }

    private static int ParseHeartRate(BluetoothGattCharacteristic? characteristic)
    {
        var data = characteristic?.GetValue();
        if (data is null || data.Length < 2)
        {
            return 0;
        }

        var isUInt16 = (data[0] & 0x01) != 0;
        return isUInt16 && data.Length >= 3
            ? data[1] | (data[2] << 8)
            : data[1];
    }

    private static string GetDeviceName(BluetoothDevice device)
    {
        return string.IsNullOrWhiteSpace(device.Name)
            ? $"设备 {device.Address[^5..]}"
            : device.Name;
    }

    private static IReadOnlyList<string> GetAdvertisedServices(ScanResult? result)
    {
        return result?.ScanRecord?.ServiceUuids?
            .Select(item => item?.Uuid?.ToString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    private void RaiseStatus(string message, bool updateMonitoring = true)
    {
        StatusMessageChanged?.Invoke(this, message);
        if (updateMonitoring && (ConnectedDevice is not null || RememberedDevice is not null))
        {
            UpdateBackgroundMonitoring(BuildMonitoringText(message));
        }
    }

    private string BuildMonitoringText(string? status = null)
    {
        var deviceName = ConnectedDevice?.Name ?? RememberedDevice?.Name ?? "心率设备";
        if (_currentHeartRate > 0 && ConnectedDevice is not null)
        {
            return $"{deviceName} · {_currentHeartRate} bpm";
        }

        return $"{deviceName} · {(string.IsNullOrWhiteSpace(status) ? "等待心率数据" : status)}";
    }

    private static BluetoothPeripheral? LoadRememberedDevice()
    {
        var address = Preferences.Default.Get(RememberedAddressKey, string.Empty);
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var name = Preferences.Default.Get(RememberedNameKey, "上次连接设备");
        return new BluetoothPeripheral
        {
            Address = address,
            Name = name,
            Rssi = 0,
            IsConnected = false
        };
    }

    private static void SaveRememberedDevice(BluetoothPeripheral peripheral)
    {
        Preferences.Default.Set(RememberedAddressKey, peripheral.Address);
        Preferences.Default.Set(RememberedNameKey, peripheral.Name);
    }

    private static BluetoothPeripheral ClonePeripheral(BluetoothPeripheral peripheral)
    {
        return new BluetoothPeripheral
        {
            Address = peripheral.Address,
            Name = peripheral.Name,
            Rssi = peripheral.Rssi,
            AdvertisedServices = peripheral.AdvertisedServices,
            HasHeartRateService = peripheral.HasHeartRateService,
            IsConnected = peripheral.IsConnected
        };
    }

    private void StartBackgroundMonitoring(string text)
    {
        var context = global::Android.App.Application.Context;
        HeartMonitoringForegroundService.Start(context, "Heart 后台监测中", text);
    }

    private void UpdateBackgroundMonitoring(string text)
    {
        var context = global::Android.App.Application.Context;
        HeartMonitoringForegroundService.Start(context, "Heart 后台监测中", text);
    }

    private void StopBackgroundMonitoring()
    {
        var context = global::Android.App.Application.Context;
        HeartMonitoringForegroundService.Stop(context);
    }

    private void TryStopScan()
    {
        try
        {
            _bluetoothAdapter?.BluetoothLeScanner?.StopScan(_scanCallback);
        }
        catch
        {
        }
    }

    private sealed class DeviceScanCallback : ScanCallback
    {
        private readonly AndroidHeartRateMonitorService _service;

        public DeviceScanCallback(AndroidHeartRateMonitorService service)
        {
            _service = service;
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            if (results is null)
            {
                return;
            }

            foreach (var result in results)
            {
                _service.HandleScanResult(result);
            }
        }

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            _service.HandleScanResult(result);
        }

        public override void OnScanFailed(ScanFailure errorCode)
        {
            _service.HandleScanFailed(errorCode);
        }
    }

    private sealed class DeviceGattCallback : BluetoothGattCallback
    {
        private readonly AndroidHeartRateMonitorService _service;
        private readonly BluetoothPeripheral _peripheral;

        public DeviceGattCallback(AndroidHeartRateMonitorService service, BluetoothPeripheral peripheral)
        {
            _service = service;
            _peripheral = peripheral;
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (gatt is null)
            {
                _service.HandleDisconnected("蓝牙连接异常中断。");
                return;
            }

            if (status != GattStatus.Success)
            {
                _service.HandleDisconnected($"连接失败：{status}");
                return;
            }

            if (newState == ProfileState.Connected)
            {
                _service.RaiseStatus($"已连接 {_peripheral.Name}，正在发现服务...");
                gatt.DiscoverServices();
                return;
            }

            if (newState == ProfileState.Disconnected)
            {
                _service.HandleDisconnected("设备已断开连接。");
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            _service.HandleServicesDiscovered(gatt, _peripheral, status);
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            _service.HandleDescriptorWritten(_peripheral, status);
        }

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            _service.HandleCharacteristicChanged(characteristic);
        }
    }
}
