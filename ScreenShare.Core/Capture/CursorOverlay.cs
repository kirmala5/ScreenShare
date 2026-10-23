using System.Drawing;
using System.Runtime.InteropServices;

namespace ScreenShare.Core.Capture;

/// <summary>
/// Отрисовка курсора мыши поверх кадра (п. 3.2 «трансляция курсора мыши»).
///
/// Без этого трансляция практически бесполезна для демонстрации: пользователь
/// видит окно, но не видит, куда наведён курсор и где он нажал.
///
/// Схема получения курсора стандартная для Windows: взять дескриптор
/// текущего курсора, выяснить его горячую точку через GetIconInfo, нарисовать
/// иконку со сдвигом на эту точку.
/// </summary>
public static class CursorOverlay
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIsIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetCursor();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr cursor, out ICONINFO info);

    /// <summary>
    /// Рисует курсор в указанной точке. Метод не бросает исключений: сбой
    /// отрисовки курсора не должен останавливать трансляцию экрана.
    /// </summary>
    public static bool TryDraw(Graphics graphics, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(graphics);

        try
        {
            IntPtr cursor = GetCursor();

            // Дескриптор принадлежит системе: освобождать его нельзя,
            // в отличие от копии, полученной через CopyIcon.
            if (cursor == IntPtr.Zero)
                return false;

            if (!GetIconInfo(cursor, out ICONINFO info))
                return false;

            // У монохромного курсора hbmColor равен нулю, и DrawIcon его не рисует.
            if (info.hbmColor == IntPtr.Zero)
                return false;

            using Icon icon = Icon.FromHandle(cursor);

            // Перегрузка DrawIcon(Icon, int, int) — единственная, которая
            // принимает координаты верхнего левого угла курсора.
            graphics.DrawIcon(icon, x - info.xHotspot, y - info.yHotspot);

            return true;
        }
        catch
        {
            // GDI может отказать под нагрузкой — это не повод ронять захват.
            return false;
        }
    }

    /// <summary>Координаты курсора в экранной системе (может лежать вне кадра).</summary>
    public static bool TryGetPosition(out int x, out int y)
    {
        x = y = 0;

        try
        {
            if (!GetCursorPos(out POINT point))
                return false;

            x = point.X;
            y = point.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }
}