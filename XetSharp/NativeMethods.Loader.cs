// Cross-platform native library resolution for the csbindgen-generated NativeMethods.
//
// The generated code uses [DllImport(__DllName)] where __DllName == "xetcore_native".
// Note: a [DllImport] name MUST be a compile-time constant, so __DllName cannot itself be made
// OS-specific. Instead this resolver maps that constant token to the correct per-OS file name at
// runtime (see NativeFileName), so one managed assembly loads the right binary:
//   Windows -> xetcore_native.dll   Linux -> libxetcore_native.so   macOS -> libxetcore_native.dylib
// and locates it under a NuGet-style `runtimes/<rid>/native/` layout (or flat, during local dev).
//
// NativeLibrary / [ModuleInitializer] only exist on .NET 5+; on netstandard2.1 there is no resolver
// and consumers rely on default probing (which already applies the OS prefix/extension convention).
#if NET5_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XetSharp
{
    internal static unsafe partial class NativeMethods
    {
        // CA2255: using [ModuleInitializer] from a library is the intended, csbindgen-recommended
        // way to register a resolver once for the whole assembly before any P/Invoke runs.
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void RegisterDllResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
        }
#pragma warning restore CA2255

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            // Only take over our own library; let the runtime handle anything else.
            if (libraryName != __DllName)
                return IntPtr.Zero;

            string fileName = NativeFileName(__DllName);
            string baseDir = AppContext.BaseDirectory;

            // 1. NuGet convention: runtimes/<rid>/native/<file>
            string ridPath = Path.Combine(baseDir, "runtimes", RuntimeIdentifier(), "native", fileName);
            if (File.Exists(ridPath) && NativeLibrary.TryLoad(ridPath, out IntPtr handle))
                return handle;

            // 2. Flat next to the managed assembly (local dev / single-RID publish).
            string flatPath = Path.Combine(baseDir, fileName);
            if (File.Exists(flatPath) && NativeLibrary.TryLoad(flatPath, out handle))
                return handle;

            // 3. Fall back to the OS default search (LD_LIBRARY_PATH, PATH, rpath, ...).
            return NativeLibrary.TryLoad(fileName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
        }

        /// <summary>Platform-specific file name, e.g. "libxetcore_native.so" / "xetcore_native.dll".</summary>
        private static string NativeFileName(string name)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return name + ".dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "lib" + name + ".dylib";
            return "lib" + name + ".so";
        }

        /// <summary>RID fragment used in the runtimes/&lt;rid&gt;/native path, e.g. "linux-x64".</summary>
        private static string RuntimeIdentifier()
        {
            string os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
                "linux";

            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };

            return $"{os}-{arch}";
        }
    }
}
#endif
