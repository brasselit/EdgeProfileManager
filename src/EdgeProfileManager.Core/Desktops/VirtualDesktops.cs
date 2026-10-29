using System.Runtime.InteropServices;
using System.Text;
using EdgeProfileManager.Core.Config;

namespace EdgeProfileManager.Core.Desktops;

/// <summary>Virtual desktops; numbers are 0-based.</summary>
public interface IVirtualDesktopService
{
    bool IsAvailable { get; }
    int CurrentDesktop { get; }
    int DesktopCount { get; }
    int? GetWindowDesktop(nint hwnd);
    bool MoveWindowToDesktop(nint hwnd, int desktop);
    bool GoToDesktop(int desktop);
    string? GetDesktopName(int desktop);

    /// <summary>Resolves a configured target (1-based number or name).</summary>
    int? Resolve(DesktopTarget? target)
    {
        if (target is null || !IsAvailable) return null;
        if (target.Number is { } number) return number >= 1 && number <= DesktopCount ? number - 1 : null;
        for (var i = 0; i < DesktopCount; i++)
        {
            if (string.Equals(GetDesktopName(i), target.Name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return null;
    }
}

public sealed class NullVirtualDesktopService : IVirtualDesktopService
{
    public bool IsAvailable => false;
    public int CurrentDesktop => 0;
    public int DesktopCount => 1;
    public int? GetWindowDesktop(nint hwnd) => null;
    public bool MoveWindowToDesktop(nint hwnd, int desktop) => false;
    public bool GoToDesktop(int desktop) => false;
    public string? GetDesktopName(int desktop) => null;
}

/// <summary>
/// Binding to VirtualDesktopAccessor.dll (https://github.com/Ciantic/VirtualDesktopAccessor).
/// Windows has no public API for moving other processes' windows between desktops.
/// The DLL is loaded dynamically; if it is missing, <see cref="IsAvailable"/> is false.
/// </summary>
public sealed unsafe class VdaVirtualDesktopService : IVirtualDesktopService
{
    public const string LibraryName = "VirtualDesktopAccessor.dll";

    private readonly delegate* unmanaged<int> _getCurrentDesktopNumber;
    private readonly delegate* unmanaged<int> _getDesktopCount;
    private readonly delegate* unmanaged<nint, int> _getWindowDesktopNumber;
    private readonly delegate* unmanaged<nint, int, int> _moveWindowToDesktopNumber;
    private readonly delegate* unmanaged<int, int> _goToDesktopNumber;
    private readonly delegate* unmanaged<int, byte*, nuint, int> _getDesktopName;

    private VdaVirtualDesktopService(nint library)
    {
        _getCurrentDesktopNumber = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "GetCurrentDesktopNumber");
        _getDesktopCount = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "GetDesktopCount");
        _getWindowDesktopNumber = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "GetWindowDesktopNumber");
        _moveWindowToDesktopNumber = (delegate* unmanaged<nint, int, int>)NativeLibrary.GetExport(library, "MoveWindowToDesktopNumber");
        _goToDesktopNumber = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(library, "GoToDesktopNumber");
        _getDesktopName = (delegate* unmanaged<int, byte*, nuint, int>)NativeLibrary.GetExport(library, "GetDesktopName");
    }

    /// <summary>Loads the DLL from the application directory; otherwise returns the null service.</summary>
    public static IVirtualDesktopService Load(out string? error)
    {
        error = null;
        var path = Path.Combine(AppContext.BaseDirectory, LibraryName);
        if (!File.Exists(path))
        {
            error = $"{LibraryName} not found ({path}). Desktop assignment is disabled.";
            return new NullVirtualDesktopService();
        }
        try
        {
            var service = new VdaVirtualDesktopService(NativeLibrary.Load(path));
            if (service.DesktopCount < 1) throw new InvalidOperationException("GetDesktopCount returned no desktop.");
            return service;
        }
        catch (Exception ex)
        {
            error = $"{LibraryName} could not be loaded: {ex.Message}";
            return new NullVirtualDesktopService();
        }
    }

    public bool IsAvailable => true;
    public int CurrentDesktop => _getCurrentDesktopNumber();
    public int DesktopCount => _getDesktopCount();

    public int? GetWindowDesktop(nint hwnd)
    {
        var number = _getWindowDesktopNumber(hwnd);
        return number >= 0 ? number : null;
    }

    public bool MoveWindowToDesktop(nint hwnd, int desktop) => _moveWindowToDesktopNumber(hwnd, desktop) >= 0;
    public bool GoToDesktop(int desktop) => _goToDesktopNumber(desktop) >= 0;

    public string? GetDesktopName(int desktop)
    {
        var buffer = stackalloc byte[512];
        if (_getDesktopName(desktop, buffer, 512) < 0) return null;
        var length = 0;
        while (length < 512 && buffer[length] != 0) length++;
        return length == 0 ? null : Encoding.UTF8.GetString(buffer, length);
    }
}
