using ScreenShare.Core.Capture;

namespace ScreenShare.Server;

/// <summary>
/// Проверка готовности трансляции к работе из локальной сети.
///
/// Главная причина «локально работает, а с другого компьютера не открывается» —
/// брандмауэр Windows: по умолчанию входящие соединения блокируются, и
/// программа без разрешения туда просто не попадает. localhost при этом
/// работает всегда, поэтому проблема выглядит загадочно.
/// </summary>
internal static class NetworkReadiness
{
    /// <summary>
    /// Проверяет, есть ли разрешающее правило брандмауэра для текущего
    /// исполняемого файла. Возвращает true, если проверка не удалась —
    /// молчать ошибочно о блокировке хуже, чем ничего не сказать.
    /// </summary>
    public static bool IsInboundAllowed()
    {
        string? executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
            return true;

        try
        {
            // netsh — единственный источник сведений о правилах, доступный
            // без установки модулей и без прав администратора на чтение.
            var info = new System.Diagnostics.ProcessStartInfo("netsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("advfirewall");
            info.ArgumentList.Add("firewall");
            info.ArgumentList.Add("show");
            info.ArgumentList.Add("rule");
            info.ArgumentList.Add("name=all");
            info.ArgumentList.Add("verbose");

            using var process = System.Diagnostics.Process.Start(info);

            if (process is null)
                return true;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            // Правило для нашей программы: совпадение пути в блоке Program.
            return output.Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Команда, добавляющая разрешающее правило (нужны права администратора).</summary>
    public static string BuildAllowCommand(int port)
    {
        string? executable = Environment.ProcessPath ?? "ScreenShare.exe";

        // Строка собирается конкатенацией: в интерполируемом литерале
        // кавычки на конце («""» + закрывающая) неоднозначны для компилятора.
        const string quote = "\"";

        return "netsh advfirewall firewall add rule " +
               $"name={quote}ScreenShare (входящие){quote} " +
               $"dir=in action=allow protocol=TCP localport={port} " +
               $"program={quote}{executable}{quote}";
    }
}

/// <summary>Приветственный вывод при запуске (п. 3.1 № 8 — показать IP и порт).</summary>
internal static class StartupBanner
{
    public static void Print(ServerOptions options, BroadcastService broadcast, string password)
    {
        Console.WriteLine("ScreenShare — трансляция экрана по локальной сети");
        Console.WriteLine(new string('=', 58));
        Console.WriteLine(" Мониторы:");

        foreach (MonitorInfo monitor in ScreenCapturer.GetMonitors())
        {
            string mark = monitor.IsPrimary ? "  (основной)" : "";
            Console.WriteLine($"   {monitor.DeviceName,-18} {monitor.Width}×{monitor.Height}{mark}");
        }

        Console.WriteLine($" Захват:     {options.Fps} fps, качество JPEG {options.Quality}");
        Console.WriteLine($"             курсор {(options.ShowCursor ? "виден" : "скрыт")}, " +
                          $"масштаб {(options.MaxWidth > 0 ? $"до {options.MaxWidth}px" : "без ограничения")}");
        Console.WriteLine($" Клиентов:   максимум {options.MaxClients}");
        Console.WriteLine($" Пароль:     {(string.IsNullOrEmpty(password) ? "НЕ ЗАДААН (трансляция открыта!)" : password)}");

        // Отдельный пароль наблюдателя: его выдают тем, кому смотреть можно,
        // а останавливать или перенастраивать трансляцию — нет.
        if (options.ViewerRoleEnabled && !string.IsNullOrEmpty(options.ViewerPassword))
        {
            Console.WriteLine($" Наблюдатель: {options.ViewerPassword}   (только просмотр)");
        }

        Console.WriteLine(new string('-', 58));
        Console.WriteLine(" ВНИМАНИЕ: в трансляции видно всё содержимое экрана,");
        Console.WriteLine(" включая открытые документы и введённые пароли.");
        Console.WriteLine(" Не запускайте сервер в общих или гостиничных сетях.");
        Console.WriteLine();

        // Без этого предупреждения самая частая жалоба выглядит загадочно:
        // «на своём компьютере открывается, с другого — нет».
        if (options.BindAllInterfaces && !NetworkReadiness.IsInboundAllowed())
        {
            Console.WriteLine(" ! Брандмауэр Windows, скорее всего, блокирует входящие");
            Console.WriteLine("   соединения. С другого устройства в сети страница");
            Console.WriteLine("   не откроется, пока не добавлено разрешение.");
            Console.WriteLine();
            Console.WriteLine("   Выполните в PowerShell от имени администратора:");
            Console.WriteLine();
            Console.WriteLine("     " + NetworkReadiness.BuildAllowCommand(options.Port));
            Console.WriteLine();
        }
    }
}