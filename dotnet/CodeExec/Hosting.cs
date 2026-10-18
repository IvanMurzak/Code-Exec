using System;
using System.Reflection;
using System.Threading;
#if NET8_0_OR_GREATER
using System.IO;
using System.Runtime.Loader;
#endif

namespace com.IvanMurzak.CodeExec
{
    /// <summary>An emitted snippet assembly while it is in use. Disposing it releases it when the host can unload.</summary>
    public interface ILoadedAssembly : IDisposable
    {
        Assembly Assembly { get; }
    }

    /// <summary>Loads emitted images. Hosts differ only in whether they can unload them.</summary>
    public interface IAssemblyHost
    {
        bool SupportsUnload { get; }
        ILoadedAssembly Load(byte[] image, byte[]? symbols, string name);
    }

    /// <summary>
    /// Plain <c>Assembly.Load</c> — the only option on Mono (Unity) and .NET Framework. Nothing is unloaded until the
    /// domain goes away, which is why <see cref="CodeRunner"/> reuses loaded assemblies for identical requests.
    /// </summary>
    public sealed class DefaultAssemblyHost : IAssemblyHost
    {
        static int _resolverInstalled;

        public DefaultAssemblyHost()
        {
            // Assemblies loaded from bytes (snippets, or a host's in-memory game assembly) are not bound automatically
            // when another assembly references them (.NET Framework, Mono): resolve by name among loaded ones.
            if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) => AssemblyLookup.FindLoaded(new AssemblyName(args.Name).Name);
        }

        public bool SupportsUnload => false;

        public ILoadedAssembly Load(byte[] image, byte[]? symbols, string name) =>
            new Loaded(symbols == null ? Assembly.Load(image) : Assembly.Load(image, symbols));

        sealed class Loaded : ILoadedAssembly
        {
            public Loaded(Assembly assembly) { Assembly = assembly; }
            public Assembly Assembly { get; }
            public void Dispose() { }
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// One collectible <see cref="AssemblyLoadContext"/> per execution (CoreCLR: Godot, BRUTAL, plain .NET).
    /// References resolve against assemblies already loaded in the process: in Godot the game assembly lives in
    /// Godot's own collectible context, not in the default one.
    /// </summary>
    public sealed class CollectibleAssemblyHost : IAssemblyHost
    {
        public bool SupportsUnload => true;

        public ILoadedAssembly Load(byte[] image, byte[]? symbols, string name)
        {
            var context = new AssemblyLoadContext("CodeExec:" + name, isCollectible: true);
            context.Resolving += ResolveFromLoaded;
            using var imageStream = new MemoryStream(image);
            using var symbolStream = symbols == null ? null : new MemoryStream(symbols);
            return new Loaded(context, context.LoadFromStream(imageStream, symbolStream));
        }

        static Assembly? ResolveFromLoaded(AssemblyLoadContext context, AssemblyName name) => AssemblyLookup.FindLoaded(name.Name);

        sealed class Loaded : ILoadedAssembly
        {
            AssemblyLoadContext? _context;

            public Loaded(AssemblyLoadContext context, Assembly assembly)
            {
                _context = context;
                Assembly = assembly;
            }

            public Assembly Assembly { get; private set; }

            public void Dispose()
            {
                if (_context == null)
                    return;
                _context.Resolving -= ResolveFromLoaded;
                _context.Unload();
                _context = null;
                Assembly = null!;
            }
        }
    }
#endif

    static class AssemblyLookup
    {
        public static Assembly? FindLoaded(string? simpleName)
        {
            if (simpleName == null)
                return null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }
            return null;
        }
    }

    public static class AssemblyHosts
    {
        /// <summary>Collectible on CoreCLR builds of this library, plain load on netstandard2.0 (Unity / Mono).</summary>
        public static IAssemblyHost CreateDefault()
        {
#if NET8_0_OR_GREATER
            return new CollectibleAssemblyHost();
#else
            return new DefaultAssemblyHost();
#endif
        }
    }
}
