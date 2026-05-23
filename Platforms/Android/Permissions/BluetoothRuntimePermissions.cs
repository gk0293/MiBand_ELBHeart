using Microsoft.Maui.ApplicationModel;

namespace Heart.Platforms.Android.Permissions;

public sealed class BluetoothScanPermission : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[]
        {
            (global::Android.Manifest.Permission.BluetoothScan, true)
        };
}

public sealed class BluetoothConnectPermission : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[]
        {
            (global::Android.Manifest.Permission.BluetoothConnect, true)
        };
}

/// <summary>
/// Android 11 及以下 BLE 扫描需要的位置权限（Android 12+ 使用 neverForLocation 绕过）。
/// Android 12+ 若未声明 neverForLocation 仍需此权限，保留以兼容两种情况。
/// </summary>
public sealed class FineLocationPermission : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[]
        {
            (global::Android.Manifest.Permission.AccessFineLocation, true)
        };
}
