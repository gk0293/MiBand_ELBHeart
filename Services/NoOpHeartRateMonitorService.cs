using Heart.Models;

namespace Heart.Services;

public sealed class NoOpHeartRateMonitorService : IHeartRateMonitorService
{
    public event EventHandler<int>? HeartRateUpdated;
    public event EventHandler<IReadOnlyList<BluetoothPeripheral>>? ScanBatchReady;
    public event EventHandler<string>? StatusMessageChanged;

    public BluetoothPeripheral? ConnectedDevice => null;
    public BluetoothPeripheral? RememberedDevice => null;
    public bool IsConnected => false;

    public Task<IReadOnlyList<BluetoothPeripheral>> ScanForDevicesAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default)
    {
        StatusMessageChanged?.Invoke(this, "当前平台不支持蓝牙心率读取。");
        return Task.FromResult<IReadOnlyList<BluetoothPeripheral>>(Array.Empty<BluetoothPeripheral>());
    }

    public Task<bool> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken = default)
    {
        StatusMessageChanged?.Invoke(this, "当前平台不支持蓝牙心率读取。");
        return Task.FromResult(false);
    }

    public Task<bool> TryReconnectToRememberedDeviceAsync(CancellationToken cancellationToken = default)
    {
        StatusMessageChanged?.Invoke(this, "当前平台不支持蓝牙心率读取。");
        return Task.FromResult(false);
    }

    public Task DisconnectAsync()
    {
        StatusMessageChanged?.Invoke(this, "当前平台不支持蓝牙心率读取。");
        return Task.CompletedTask;
    }
}
