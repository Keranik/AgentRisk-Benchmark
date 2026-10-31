using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentRisk.Client.Protocol;

/// <summary>
/// Finds the address another machine on this network can use to reach
/// this one: the first live IPv4 address on a real interface, read from
/// the machine's interface table. Nothing is sent anywhere; the table
/// is only read. When no LAN address can be found, the loopback
/// address stands in (a table played on one machine still works).
/// </summary>
public static class LanAddress
{
    public static string DefaultConnectAddress()
    {
        string? linkLocal = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    string text = address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        // A link-local address reaches the local wire but
                        // nothing beyond it; keep it only as a fallback.
                        linkLocal ??= text;
                        continue;
                    }
                    return text;
                }
            }
        }
        catch (Exception)
        {
            // The interface table is a convenience for filling a form,
            // never a reason the form cannot open.
        }
        return linkLocal ?? "127.0.0.1";
    }
}
