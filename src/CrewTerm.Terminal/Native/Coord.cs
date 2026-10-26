using System.Runtime.InteropServices;

namespace CrewTerm.Terminal.Native;

/// <summary>
/// Win32 <c>COORD</c>：以字符单元计的列数与行数。
/// </summary>
/// <param name="X">列数。</param>
/// <param name="Y">行数。</param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Coord(short X, short Y);
