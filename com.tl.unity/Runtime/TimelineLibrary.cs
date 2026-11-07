using System.IO;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public struct TimelineLibrary : IComponentData
    {
        public global::Tl.LibraryView View;
        public KindIndex Kinds;
        public uint Generation;
    }

    public struct TimelineStep : IComponentData
    {
        public bool Forward;
    }

#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static partial class TlGeneratedBootstrap
    {
        static readonly HashSet<string> Installed = new HashSet<string>();

        static partial void InstallGeneratedModules();

        public static void InstallAll()
        {
            InstallGeneratedModules();
            InstallDiscoveredModules();
        }

        public static void InstallDiscoveredModules()
        {
            foreach (var assembly in TlAssemblies.Loaded())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (type.IsInterface || type.IsAbstract)
                        continue;
                    if (!typeof(global::Tl.ITlModule).IsAssignableFrom(type))
                        continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                        continue;
                    var name = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
                    lock (Installed)
                    {
                        if (!Installed.Add(name))
                            continue;
                    }
                    try
                    {
                        global::Tl.TlGenerated.Install((global::Tl.ITlModule)Activator.CreateInstance(type));
                    }
                    catch
                    {
                        lock (Installed)
                        {
                            Installed.Remove(name);
                        }
                        throw;
                    }
                }
            }
        }
    }

}
