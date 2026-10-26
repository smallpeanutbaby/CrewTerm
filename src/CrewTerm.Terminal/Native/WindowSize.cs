using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// POSIX <c>struct winsize</c>：伪终端的字符行列数与像素尺寸。
/// </summary>
/// <param name="Rows">行数（<c>ws_row</c>）。</param>
/// <param name="Columns">列数（<c>ws_col</c>）。</param>
/// <param name="PixelWidth">像素宽度（<c>ws_xpixel</c>），不使用时为 0。</param>
/// <param name="PixelHeight">像素高度（<c>ws_ypixel</c>），不使用时为 0。</param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct WindowSize(ushort Rows, ushort Columns, ushort PixelWidth, ushort PixelHeight);
