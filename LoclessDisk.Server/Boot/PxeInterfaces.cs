using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoclessDisk.Server.Boot;

internal static class PxeInterfaces
{
    internal sealed record InterfaceAddress(string Name, string Address);

    public static InterfaceAddress[] List() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(info => info.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(info.Address))
            .Select(info => new InterfaceAddress(adapter.Name, info.Address.ToString())))
        .DistinctBy(item => item.Address)
        .OrderBy(item => item.Name).ThenBy(item => item.Address)
        .ToArray();
}
