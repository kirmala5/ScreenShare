using System.Text.Encodings.Web;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;

namespace ScreenShare.Server;

/// <summary>
/// Параметры запуска, полученные из командной строки.
/// </summary>
public sealed record ServerOptions
{
    /// <summary>Порт HTTP-сервера.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Пароль администратора: полный доступ, включая настройки и остановку.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Пароль наблюдателя: доступ только к просмотру.
    /// Если не задан, роль наблюдателя не используется — все, кто вошёл, администраторы.
    /// </summary>
    public string ViewerPassword { get; init; } = string.Empty;

    /// <summary>Имя пользователя для Basic Auth.</summary>
    public string UserName { get; init; } = "admin";

    /// <summary>Кадров в секунду при старте.</summary>
    public int Fps { get; init; } = 10;

    /// <summary>Качество JPEG при старте.</summary>
    public int Quality { get; init; } = 70;

    /// <summary>Ограничение ширины кадра; 0 — без масштабирования.</summary>
    public int MaxWidth { get; init; }

    /// <summary>Ограничение высоты кадра; 0 — без масштабирования.</summary>
    public int MaxHeight { get; init; }

    /// <summary>Показывать курсор мыши в трансляции.</summary>
    public bool ShowCursor { get; init; } = true;

    /// <summary>Максимальное число одновременных клиентов (п. 3.2).</summary>
    public int MaxClients { get; init; } = 8;

    /// <summary>Имя монитора для захвата; пусто — основной.</summary>
    public string Monitor { get; init; } = string.Empty;

    /// <summary>
    /// Публиковать ли HTTP на всех сетевых интерфейсах. Выключать не следует:
    /// без этого трансляция не будет доступна другим устройствам в сети.
    /// </summary>
    public bool BindAllInterfaces { get; init; } = true;

    /// <summary>Включена ли отдельная роль наблюдателя.</summary>
    public bool ViewerRoleEnabled { get; init; } = true;

    /// <summary>
    /// Генерировать ли случайный пароль при запуске, если он не задан.
    /// </summary>
    public bool GeneratePassword { get; init; } = true;

    public static ServerOptions FromArguments(string[] args)
    {
        var options = new ServerOptions();

        int port = options.Port;
        int fps = options.Fps;
        int quality = options.Quality;
        int maxWidth = options.MaxWidth;
        int maxHeight = options.MaxHeight;
        int maxClients = options.MaxClients;

        string password = string.Empty;
        string viewerPassword = string.Empty;
        bool disableViewerRole = false;
        string user = options.UserName;
        string monitor = string.Empty;
        bool showCursor = true;
        bool bindAll = true;
        bool generate = true;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            switch (arg)
            {
                case "--port" or "-p" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedPort))
                        port = Math.Clamp(parsedPort, 1, 65535);
                    break;

                case "--password" when i + 1 < args.Length:
                    password = args[++i];
                    break;

                case "--viewer-password" when i + 1 < args.Length:
                    viewerPassword = args[++i];
                    break;

                // Наблюдатели без отдельного пароля: одна роль для всех.
                case "--no-viewer-role":
                    disableViewerRole = true;
                    break;

                case "--user" when i + 1 < args.Length:
                    user = args[++i];
                    break;

                case "--fps" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedFps))
                        fps = Math.Clamp(parsedFps, 1, 60);
                    break;

                case "--quality" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedQuality))
                        quality = Math.Clamp(parsedQuality, 10, 100);
                    break;

                case "--max-width" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedWidth))
                        maxWidth = Math.Max(0, parsedWidth);
                    break;

                case "--max-height" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedHeight))
                        maxHeight = Math.Max(0, parsedHeight);
                    break;

                case "--max-clients" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int parsedClients))
                        maxClients = Math.Clamp(parsedClients, 1, 100);
                    break;

                case "--monitor" when i + 1 < args.Length:
                    monitor = args[++i];
                    break;

                case "--no-cursor":
                    showCursor = false;
                    break;

                case "--local-only":
                    bindAll = false;
                    break;

                case "--fixed-password":
                    generate = false;
                    break;
            }
        }

        return options with
        {
            Port = port,
            Fps = fps,
            Quality = quality,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            MaxClients = maxClients,
            Password = password,
            ViewerPassword = viewerPassword,
            UserName = user,
            Monitor = monitor,
            ShowCursor = showCursor,
            BindAllInterfaces = bindAll,
            GeneratePassword = generate,
            ViewerRoleEnabled = !disableViewerRole,
        };
    }

    /// <summary>Пароль для показа пользователю (при необходимости сгенерированный).</summary>
    public static string ResolvePassword(ServerOptions options)
    {
        if (!string.IsNullOrEmpty(options.Password))
            return options.Password;

        if (!options.GeneratePassword)
            return string.Empty;

        return Generate();
    }

    /// <summary>
    /// Пароль наблюдателя. Если роль включена, а пароль не задан и не запрещена
    /// автогенерация, выдаётся отдельный случайный пароль: общий пароль лишил бы
    /// наблюдателя смысла, он снова смог бы остановить трансляцию.
    /// </summary>
    public static string ResolveViewerPassword(ServerOptions options)
    {
        if (!options.ViewerRoleEnabled)
            return string.Empty;

        if (!string.IsNullOrEmpty(options.ViewerPassword))
            return options.ViewerPassword;

        if (!options.GeneratePassword)
            return string.Empty;

        return Generate();
    }

    private static string Generate()
    {
        // Пароль должен быть вводимым вручную и читаемым вслух,
        // поэтому из алфавита исключены похожие символы (0/O, 1/l).
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[10];

        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        return new string(chars);
    }

    public static string Usage =>
        """
        ScreenShare — трансляция экрана по локальной сети.

        ЗАПУСК
          ScreenShare.exe [параметры]

        ПАРАМЕТРЫ
          -p, --port <порт>        Порт HTTP-сервера (по умолчанию 8080)
              --password <пароль>  Пароль администратора (по умолчанию — случайный)
              --viewer-password <пароль>
                                   Пароль наблюдателя: только просмотр,
                                   без кнопок управления (по умолчанию — случайный)
              --no-viewer-role     Не разделять роли: один пароль на всех
              --user <имя>         Логин по умолчанию, если его не ввели (admin)
              --fps <число>         Кадров в секунду, 1…60 (10)
              --quality <число>     Качество JPEG, 10…100 (70)
              --max-width <px>      Ограничить ширину кадра (0 — без ограничения)
              --max-height <px>     Ограничить высоту кадра (0 — без ограничения)
              --max-clients <число> Максимум клиентов одновременно (8)
              --monitor <имя>       Монитор для захвата (по умолчанию основной)
              --no-cursor           Не рисовать курсор мыши
              --local-only          Слушать только 127.0.0.1 (без доступа из сети)
              --fixed-password      Не генерировать пароль автоматически
          -h, --help                Эта справка

        ПРИМЕР
          ScreenShare.exe --port 8080 --fps 15 --quality 60

        ВХОД В БРАУЗЕРЕ
          Логин можно вводить любой, даже пустой: роль определяет пароль.
          Введённое имя подхватывается страницей как имя для показа тому,
          кто ведёт трансляцию.
        """;
}

/// <summary>
/// Обработчик Basic Auth для защиты трансляции (п. 3.2 «защита паролем»).
///
/// Пароль сравнивается в постоянном времени: обычное сравнение строк завершается
/// на первом несовпавшем символе, и по времени ответа можно подбирать пароль
/// посимвольно.
/// </summary>
public sealed class BasicAuthHandler : AuthenticationHandler<BasicAuthSchemeOptions>
{
    /// <summary>Имя схемы аутентификации.</summary>
    public const string SchemeName = "Basic";

    public BasicAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<BasicAuthSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string encoded = header["Basic ".Length..].Trim();

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int separator = decoded.IndexOf(':');

            if (separator < 0)
                return Task.FromResult(AuthenticateResult.Fail("Некорректные учётные данные"));

            string user = decoded[..separator];
            string password = decoded[(separator + 1)..];

            // Логин не проверяется: роль определяет пароль. Раньше подходил
            // только «admin» для обоих ролей, и наблюдатель, представившийся
            // своим именем, просто не мог войти. Имя из поля входа идёт
            // в дальнейшем как имя для показа администратору.
            if (user.Length == 0)
                user = Options.UserName;

            if (FixedTimeEquals(password, Options.Password))
            {
                return Task.FromResult(Success(user, Roles.Admin));
            }

            // Пароль наблюдателя даёт доступ только к просмотру: остановить
            // трансляцию или изменить настройки такой пользователь не может.
            if (Options.ViewerRoleEnabled &&
                !string.IsNullOrEmpty(Options.ViewerPassword) &&
                FixedTimeEquals(password, Options.ViewerPassword))
            {
                return Task.FromResult(Success(user, Roles.Viewer));
            }

            return Task.FromResult(AuthenticateResult.Fail("Неверный пароль"));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Некорректные учётные данные"));
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"ScreenShare\", charset=\"UTF-8\"";

        // Браузер показывает диалог входа только если сервер явно его запросил.
        Response.Headers.CacheControl = "no-cache";

        return Task.CompletedTask;
    }

    /// <summary>Формирует успешный результат входа с указанной ролью.</summary>
    private AuthenticateResult Success(string user, string role)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
            ],
            Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), Scheme.Name));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        byte[] left = Encoding.UTF8.GetBytes(a);
        byte[] right = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>Названия ролей.</summary>
public static class Roles
{
    /// <summary>Полный доступ: просмотр, настройки, запуск и остановка.</summary>
    public const string Admin = "Admin";

    /// <summary>Только просмотр трансляции.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Имя политики авторизации для администраторских действий.</summary>
    public const string AdminPolicy = "AdminOnly";
}

public sealed class BasicAuthSchemeOptions : AuthenticationSchemeOptions
{
    public string UserName { get; set; } = "admin";
    public string Password { get; set; } = string.Empty;
    public string ViewerPassword { get; set; } = string.Empty;
    public bool ViewerRoleEnabled { get; set; } = true;
}