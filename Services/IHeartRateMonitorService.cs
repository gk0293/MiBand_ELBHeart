using Heart.Models;

namespace Heart.Services;

public interface IHeartRateMonitorService
{
    event EventHandler<int>? HeartRateUpdated;

    /// <summary>
    /// Raised on a background thread at most once per batch interval (¡Ö500 ms) with
    /// the current top-<see cref="MaxScanResults"/> devices ordered by RSSI.
    /// Consumers must marshal to the UI thread before touching UI elements.
    /// </summary>
    event EventHandler<IReadOnlyList<BluetoothPeripheral>>? ScanBatchReady;

    event EventHandler<string>? StatusMessageChanged;

    /// <summary>Maximum number of devices surfaced per scan batch.</summary>
    static int MaxScanResults => 10;

    BluetoothPeripheral? ConnectedDevice { get; }
    BluetoothPeripheral? RememberedDevice { get; }
    bool IsConnected { get; }

    Task<IReadOnlyList<BluetoothPeripheral>> ScanForDevicesAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default);
    Task<bool> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken = default);
    Task<bool> TryReconnectToRememberedDeviceAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}
