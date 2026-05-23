using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Heart.Models;

public sealed class BluetoothPeripheral : INotifyPropertyChanged
{
    private string _name = "未命名设备";
    private int _rssi;
    private bool _isConnected;
    private string _advertisedServices = "未广播服务 UUID";
    private bool _hasHeartRateService;

    public required string Address { get; init; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? "未命名设备" : value);
    }

    public int Rssi
    {
        get => _rssi;
        set => SetProperty(ref _rssi, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public string AdvertisedServices
    {
        get => _advertisedServices;
        set => SetProperty(ref _advertisedServices, string.IsNullOrWhiteSpace(value) ? "未广播服务 UUID" : value);
    }

    public bool HasHeartRateService
    {
        get => _hasHeartRateService;
        set => SetProperty(ref _hasHeartRateService, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
