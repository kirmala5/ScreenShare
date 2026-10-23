using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenShare.Core.Networking;

/// <summary>
/// Определение адреса, по которому трансляция будет доступна в локальной сети
/// (п. 3.1 № 6 и № 8, п. 3.2 «автоматический выбор IP-адреса нужного интерфейса»).
///
/// Сложность в том, что у машины обычно несколько сетевых интерфейсов:
/// Ethernet, Wi-Fi, виртуальные адаптеры (Hyper-V, WSL, VPN). Показывать
/// пользователю все адреса бессмысленно — он должен увидеть тот, по которому
/// к нему реально подключится коллега.
/// </summary>
public static class NetworkInfo
{
    /// <summary>Все IPv4-адреса, подходящие для обращения из локальной сети.</summary>
    public static IReadOnlyList<string> GetLanAddresses()
    {
        var result = new List<string>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (IPAddress.IsLoopback(address.Address))
                    continue;

                result.Add(address.Address.ToString());
            }
        }

        return result;
    }

    /// <summary>
    /// Наиболее вероятный адрес для трансляции: предпочитаем обычные
    /// проводные и беспроводные интерфейсы, отбрасывая виртуальные.
    /// </summary>
    public static string GetPreferredAddress()
    {
        var candidates = new List<(string Address, NetworkInterfaceType Type, string Name)>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var ipv4 = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address) &&
                    !IsLinkLocal(a.Address));

            if (ipv4 is null)
                continue;

            candidates.Add((ipv4.Address.ToString(), nic.NetworkInterfaceType, nic.Name));
        }

        if (candidates.Count == 0)
            return "127.0.0.1";

        // Метрика интерфейса в .NET не публикуется (IPInterfaceProperties.Metric
        // недоступен), поэтому выбор сделан по типу интерфейса, а при равенстве —
        // по имени, чтобы результат был хотя бы предсказуемым.
        return candidates
            .OrderBy(c => TypeRank(c.Type))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .First()
            .Address;
    }

    /// <summary>Меньший ранг — более вероятный адрес для подключения коллеги.</summary>
    private static int TypeRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        NetworkInterfaceType.GigabitEthernet => 0,
        _ => 2,
    };

    /// <summary>Полный список адресов с указанием интерфейса — для панели настроек.</summary>
    public static IReadOnlyList<InterfaceAddress> DescribeInterfaces()
    {
        var result = new List<InterfaceAddress>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                result.Add(new InterfaceAddress(
                    address.Address.ToString(),
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType.ToString()));
            }
        }

        return result;
    }

    /// <summary>
    /// Адрес автонастройки 169.254.0.0/16 (APIPA). Он назначается автоматически,
    /// когда сеть не настроена, и для доступа к трансляции не годится.
    /// Отдельного метода IPAddress.IsLinkLocal в .NET нет — проверяем вручную.
    /// </summary>
    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}

public sealed record InterfaceAddress(string Address, string Name, string Description, string Type);