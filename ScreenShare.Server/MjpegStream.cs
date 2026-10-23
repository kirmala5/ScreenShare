using System.Threading.Channels;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ScreenShare.Core.Streaming;

namespace ScreenShare.Server;

/// <summary>
/// Трансляция кадров по протоколу MJPEG — multipart/x-mixed-replace
/// (Вариант B технического задания, п. 3.1 № 4).
///
/// Почему MJPEG, а не WebSocket: браузер получает поток через обычный
/// &lt;img src="/stream"&gt; без единой строки JavaScript. Трансляция работает
/// даже там, где скрипты отключены политикой, и открывается с телефона
/// или планшета без установки приложений.
/// </summary>
public static class MjpegStream
{
    private const string Boundary = "screenshareframe";

    /// <summary>Заголовок части multipart-ответа с одним кадром.</summary>
    private static byte[] BuildPartHeader(int length) =>
        EncodingCache.Ascii.GetBytes(
            $"--{Boundary}\r\n" +
            "Content-Type: image/jpeg\r\n" +
            $"Content-Length: {length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

    private static byte[] BuildClosingPart() =>
        EncodingCache.Ascii.GetBytes($"--{Boundary}--\r\n");

    /// <summary>Отдаёт поток JPEG-кадров клиенту до его отключения.</summary>
    public static async Task StreamAsync(HttpContext context, BroadcastService broadcast)
    {
        HttpResponse response = context.Response;

        string remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Поток открывается обычным <img>, заголовки к нему из браузера
        // добавить нельзя. Поэтому имя приходит в cookie: она уходит и
        // с этим запросом, в отличие от обращений к /api/.
        string name = ViewerNames.ReadCookie(context);

        if (!broadcast.Clients.TryRegister(remote, "MJPEG", name, out ClientSession? client) || client is null)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsync("Достигнут лимит одновременных клиентов.").ConfigureAwait(false);
            return;
        }

        try
        {
            // Поток бесконечный: прокси и браузер не должны считать его
            // «неполным» и обрывать по Content-Length.
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "multipart/x-mixed-replace; boundary=" + Boundary;

            // Отключаем буферизацию: иначе Kestrel скопит кадры и покажет
            // картинку с задержкой в несколько секунд.
            response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            response.Headers[HeaderNames.Pragma] = "no-cache";
            response.Headers[HeaderNames.Expires] = "0";
            response.Headers[HeaderNames.Connection] = "close";
            response.Headers[HeaderNames.AcceptRanges] = "none";

            await response.Body.FlushAsync().ConfigureAwait(false);

            ChannelReader<Frame> reader = broadcast.Hub.Subscribe(out Action unsubscribe);

            byte[] partHeader = BuildPartHeader(0);

            try
            {
                await foreach (Frame frame in reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    partHeader = BuildPartHeader(frame.Jpeg.Length);

                    await response.Body.WriteAsync(partHeader, context.RequestAborted).ConfigureAwait(false);
                    await response.Body.WriteAsync(frame.Jpeg, context.RequestAborted).ConfigureAwait(false);
                    await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

                    client.RecordFrame(frame.Jpeg.Length, broadcast.Capturer.Options.FrameIntervalMs);
                }
            }
            catch (OperationCanceledException)
            {
                // Клиент закрыл вкладку — это нормальный сценарий.
            }
            catch (IOException)
            {
                // Соединение разорвано: браузер закрыл вкладку или сеть пропала.
            }
            finally
            {
                unsubscribe();
            }
        }
        finally
        {
            broadcast.Clients.Unregister(client);
        }
    }
}

/// <summary>Кодировки ASCII для заголовков multipart-ответа.</summary>
internal static class EncodingCache
{
    public static System.Text.Encoding Ascii { get; } = System.Text.Encoding.ASCII;
}