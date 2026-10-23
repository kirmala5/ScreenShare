using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using ScreenShare.Core.Capture;
using ScreenShare.Core.Streaming;
using Microsoft.AspNetCore.Hosting;
using ScreenShare.Server;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch
{
    // Неинтерактивная консоль — не критично.
}

if (args.Any(a => a is "-h" or "--help" or "/?"))
{
    Console.WriteLine(ServerOptions.Usage);
    return 0;
}

ServerOptions options = ServerOptions.FromArguments(args);

// Раньше --fixed-password без --password означал пустой пароль, то есть
// полностью открытую трансляцию. Имя ключа обещало обратное, поэтому
// такой запуск теперь запрещён явно.
if (!options.GeneratePassword && string.IsNullOrEmpty(options.Password))
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Ошибка: ключ --fixed-password требует явного --password.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Без --password пароль был бы пустым, и трансляцию");
    Console.Error.WriteLine("  открыл бы кто угодно, без запроса на вход.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("      ScreenShare.exe --fixed-password --password мойпароль");
    Console.Error.WriteLine();
    return 1;
}

string password = ServerOptions.ResolvePassword(options);
string viewerPassword = ServerOptions.ResolveViewerPassword(options);

options = options with { Password = password, ViewerPassword = viewerPassword };

JsonSerializerOptions json = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Kestrel по умолчанию ограничивает размер ответа, а поток MJPEG длинный:
// лимит снять, иначе соединение будет рваться на середине трансляции.
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<BroadcastService>();

builder.Services
    .AddAuthentication(BasicAuthHandler.SchemeName)
    .AddScheme<BasicAuthSchemeOptions, BasicAuthHandler>(
        BasicAuthHandler.SchemeName,
        // Схема обязана получить пароль: без этого обработчик сравнивал бы
        // введённый пароль с пустой строкой и отклонял бы всех.
        scheme =>
        {
            scheme.UserName = options.UserName;
            scheme.Password = options.Password;
            scheme.ViewerPassword = options.ViewerPassword;
            scheme.ViewerRoleEnabled = options.ViewerRoleEnabled;
        });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.AdminPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));

WebApplication app = builder.Build();

BroadcastService broadcast = app.Services.GetRequiredService<BroadcastService>();

// Логи и статистика тоже под паролем: там видны адреса подключившихся.
app.UseAuthentication();
app.UseAuthorization();

// ------------------------------------------------------------- страница

app.MapGet("/", () => Results.Content(ViewerPage.Html, "text/html; charset=utf-8"))
   .RequireAuthorization();

// ----------------------------------------------------------- MJPEG поток

app.MapGet("/stream", (HttpContext context, BroadcastService b) => MjpegStream.StreamAsync(context, b))
   .RequireAuthorization();

// ------------------------------------------------------------------- API

// Список подключившихся с адресами и именами — сведения для администратора,
// поэтому наблюдателю не отдаётся (см. /api/status ниже).
app.MapPost("/api/viewer/name", (HttpContext context, NameRequest request) =>
{
    string name = ViewerNames.Sanitize(request.Name);

    ViewerNames.WriteCookie(context, name);

    return Results.Json(new { name }, json);
})
.RequireAuthorization();

app.MapGet("/api/status", (ClaimsPrincipal user, BroadcastService b) =>
{
    BroadcastStatus status = b.GetStatus();

    // Наблюдателю список клиентов не показывается: там адреса и имена
    // других людей, а роль наблюдателя на них не даёт смотреть.
    return Results.Json(
        user.IsInRole(Roles.Admin) ? status : status with { Clients = [] },
        json);
})
.RequireAuthorization();

app.MapGet("/api/frame.jpg", (BroadcastService b) =>
{
    Frame? frame = b.Hub.Latest;

    return frame is null
        ? Results.NotFound(new { error = "Кадр ещё не захвачен" })
        : Results.File(frame.Jpeg, "image/jpeg");
})
.RequireAuthorization();

app.MapPost("/api/start", (BroadcastService b) =>
{
    b.Start();
    return Results.Json(new { running = b.IsRunning });
})
.RequireAuthorization(Roles.AdminPolicy);

app.MapPost("/api/stop", async (BroadcastService b) =>
{
    await b.StopAsync();
    return Results.Json(new { running = b.IsRunning });
})
.RequireAuthorization(Roles.AdminPolicy);

app.MapPost("/api/settings", (BroadcastService b, SettingsRequest request) =>
{
    // Значения нормализуются здесь же: некорректный запрос не должен
    // суметь сломать цикл захвата.
    ServerOptions updated = options with
    {
        Fps = Math.Clamp(request.Fps ?? options.Fps, 1, 60),
        Quality = Math.Clamp(request.Quality ?? options.Quality, 10, 100),
        MaxWidth = Math.Max(0, request.MaxWidth ?? options.MaxWidth),
        MaxHeight = Math.Max(0, request.MaxHeight ?? options.MaxHeight),
        ShowCursor = request.ShowCursor ?? options.ShowCursor,
        Monitor = request.Monitor ?? options.Monitor,
        MaxClients = Math.Clamp(request.MaxClients ?? options.MaxClients, 1, 100),
    };

    options = updated;
    b.ApplyOptions(updated);

    return Results.Json(b.GetStatus(), json);
})
.RequireAuthorization(Roles.AdminPolicy);

// Роль текущего пользователя и его сохранённое имя: страница просмотра
// прячет администраторские кнопки и подставляет имя в поле.
app.MapGet("/api/me", (HttpContext context, ClaimsPrincipal user) => Results.Json(new
{
    user = user.Identity?.Name ?? "unknown",
    isAdmin = user.IsInRole(Roles.Admin),
    viewerName = ViewerNames.ReadCookie(context),
}, json))
.RequireAuthorization();

app.MapGet("/api/interfaces", (BroadcastService b) =>
{
    BroadcastStatus status = b.GetStatus();

    return Results.Json(new
    {
        monitors = status.Monitors,
        addresses = status.Interfaces,
    }, json);
})
.RequireAuthorization();

// ------------------------------------------------------------------ запуск

broadcast.Start();

string listen = options.BindAllInterfaces ? "0.0.0.0" : "127.0.0.1";
app.Urls.Clear();
app.Urls.Add($"http://{listen}:{options.Port}");

// Сервер запускается ДО печати баннера. Иначе при занятом порте программа
// сначала напечатает адрес, логин и пароль, а потом упадёт — и напечатанное
// окажется неправдой.
try
{
    await app.StartAsync();
}
catch (IOException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Порт {options.Port} уже занят другим процессом.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Закройте прежний экземпляр или выберите другой порт:");
    Console.Error.WriteLine($"      ScreenShare.exe --port {options.Port + 1}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Кто слушает этот порт:");
    Console.Error.WriteLine($"      netstat -ano | findstr :{options.Port}");
    Console.Error.WriteLine();

    await broadcast.DisposeAsync();
    return 1;
}

StartupBanner.Print(options, broadcast, password);

Console.WriteLine($"  Трансляция доступна по адресу:  {broadcast.AccessAddress}");
Console.WriteLine($"  Локально:                     http://localhost:{options.Port}");
Console.WriteLine();
Console.WriteLine("  Ctrl+C — остановка.");
Console.WriteLine();

try
{
    // Сервер уже запущен выше, поэтому ждём завершения, а не RunAsync:
    // он попытался бы стартовать повторно.
    await app.WaitForShutdownAsync();
}
finally
{
    await broadcast.DisposeAsync();
}

return 0;

internal sealed class SettingsRequest
{
    public int? Fps { get; set; }
    public int? Quality { get; set; }
    public int? MaxWidth { get; set; }
    public int? MaxHeight { get; set; }
    public bool? ShowCursor { get; set; }
    public string? Monitor { get; set; }
    public int? MaxClients { get; set; }
}

/// <summary>Запрос на сохранение имени наблюдателя.</summary>
internal sealed class NameRequest
{
    public string? Name { get; set; }
}