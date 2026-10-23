namespace ScreenShare.Server;

/// <summary>
/// Имя, под которым наблюдатель появляется у администратора.
///
/// Имя приходит от клиента и показывается администратору, поэтому считается
/// чужими данными: длиной ограничивается, управляющие символы и разметка
/// вырезаются. Проверка делается на сервере — править имена из браузера
/// нельзя, иначе проверку можно было бы обойти вместе с ней самой.
/// </summary>
internal static class ViewerNames
{
    /// <summary>Имя cookie, через которое имя доходит до MJPEG-запроса.</summary>
    public const string CookieName = "ss-viewer-name";

    /// <summary>Предельная длина имени.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Приводит введённое имя к безопасному виду.
    /// Пустое или состоящее из невидимых символов имя превращается в 0 —
    /// дальше подставляется автоимяна по адресу.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        Span<char> buffer = stackalloc char[MaxLength];
        int length = 0;

        foreach (char ch in raw)
        {
            if (length == MaxLength)
                break;

            // Управляющие символы, разметка и всё, что не печатается,
            // в имя не попадает: иначе в списке клиентов может оказаться
            // нечитаемая мешанина.
            if (char.IsControl(ch) || char.IsSurrogate(ch))
                continue;

            if (ch is '<' or '>' or '&' or '"' or '\'' or '\\' or '/' or '{' or '}' or '[' or ']')
                continue;

            if (length > 0 && buffer[length - 1] == ' ' && ch == ' ')
                continue;

            buffer[length++] = ch;
        }

        return new string(buffer[..length]).Trim();
    }

    /// <summary>
    /// Имя для показа, если клиент своё не задал. Адрес сюда не вставляется:
    /// он и так стоит рядом отдельной колонкой, а повторялся бы дважды.
    /// </summary>
    public static string Fallback(string remoteAddress, int ordinal) =>
        $"Наблюдатель {ordinal}";

    /// <summary>Записывает имя в cookie, который уйдёт и с запросом /stream.</summary>
    public static void WriteCookie(HttpContext context, string name)
    {
        context.Response.Cookies.Append(CookieName, Uri.EscapeDataString(name), new CookieOptions
        {
            Path = "/",
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            // Сессия закрыта браузером — имя не переживает перезапуск,
            // как и не должно: у пришедшего назавтра другого человека
            // оно другое.
            Expires = DateTimeOffset.Now.AddDays(30),
        });
    }

    /// <summary>Читает имя из cookie запроса потока.</summary>
    public static string ReadCookie(HttpContext context)
    {
        string? raw = context.Request.Cookies[CookieName];

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        try
        {
            return Sanitize(Uri.UnescapeDataString(raw));
        }
        catch (UriFormatException)
        {
            // Повреждённая cookie не должна приводить к отказу в показе:
            // поток покажется под автоименем.
            return string.Empty;
        }
    }
}