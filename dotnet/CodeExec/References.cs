using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace com.IvanMurzak.CodeExec
{
    /// <summary>Supplies the metadata references (assembly file paths) a snippet compiles against.</summary>
    public interface IReferenceProvider
    {
        IReadOnlyList<string> GetReferencePaths();
    }

    /// <summary>
    /// Every non-dynamic assembly loaded in the process that has a file on disk, one per simple name.
    /// Snippet assemblies themselves are skipped.
    /// </summary>
    public sealed class LoadedAssembliesReferenceProvider : IReferenceProvider
    {
        readonly Func<Assembly, string?>? _locationResolver;

        /// <param name="locationResolver">
        /// Host hook for assemblies loaded from memory (empty <see cref="Assembly.Location"/>): Godot loads the game
        /// assembly from a stream, so the host points at the DLL it was built to. Without it such assemblies are
        /// invisible to snippets.
        /// </param>
        public LoadedAssembliesReferenceProvider(Func<Assembly, string?>? locationResolver = null)
        {
            _locationResolver = locationResolver;
        }

        public IReadOnlyList<string> GetReferencePaths()
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;
                var name = assembly.GetName().Name;
                if (name == null || byName.ContainsKey(name) || name.StartsWith(CodeRunner.SnippetAssemblyPrefix, StringComparison.Ordinal))
                    continue;
                var location = LocationOf(assembly);
                if (location.Length > 0 && File.Exists(location))
                    byName[name] = location;
            }
            return new List<string>(byName.Values);
        }

        string LocationOf(Assembly assembly)
        {
            string location;
            try { location = assembly.Location; }
            catch (NotSupportedException) { location = ""; }
            if (location.Length == 0 && _locationResolver != null)
                location = _locationResolver(assembly) ?? "";
            return location;
        }
    }
}
