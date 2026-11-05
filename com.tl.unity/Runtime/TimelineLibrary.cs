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
    }

    public sealed class TimelineLibraryOwner : IDisposable
    {
        global::Tl.TimelineWorld _world;
        readonly string _path;
        long _writeTicks;
        bool _disposed;

        public TimelineLibraryOwner(string path, byte[] tlib)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (tlib == null || tlib.Length == 0)
                throw new ArgumentException("Timeline library bytes are empty.", nameof(tlib));
            _path = path;
            _world = new global::Tl.TimelineWorld(tlib);
            _writeTicks = Stamp(path);
        }

        public global::Tl.LibraryView View
        {
            get
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TimelineLibraryOwner));
                return _world.View;
            }
        }

        public bool PollReload()
        {
            if (_disposed)
                return false;
            var stamp = Stamp(_path);
            if (stamp == _writeTicks)
                return false;
            _world.Reload(File.ReadAllBytes(_path));
            _writeTicks = Stamp(_path);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _world.Dispose();
            GC.SuppressFinalize(this);
        }

        static long Stamp(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (IOException)
            {
                return -1;
            }
            catch (UnauthorizedAccessException)
            {
                return -1;
            }
        }
    }

    public static class TimelineLibraryBake
    {
        public static NativeArray<byte> Bake(byte[] tlib)
        {
            if (tlib == null || tlib.Length == 0)
                throw new ArgumentException("Timeline library bytes are empty.", nameof(tlib));
            var blob = new NativeArray<byte>(tlib.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            blob.CopyFrom(tlib);
            return blob;
        }
    }

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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

    public static class TimelineLibraryBootstrap
    {
        static TimelineLibraryOwner Owner;
        static Entity OwnerEntity;
        static bool HasOwnerEntity;

        public static TimelineLibraryOwner Ensure(EntityManager entities, string path, byte[] tlib)
        {
            if (Owner != null)
                return Owner;
            Owner = new TimelineLibraryOwner(path, tlib);
            OwnerEntity = entities.CreateEntity(typeof(TimelineLibrary), typeof(ClosedPlayback));
            entities.SetComponentData(OwnerEntity, new TimelineLibrary { View = Owner.View });
            HasOwnerEntity = true;
            return Owner;
        }

        public static void Publish(EntityManager entities)
        {
            if (Owner == null || !HasOwnerEntity)
                return;
            entities.SetComponentData(OwnerEntity, new TimelineLibrary { View = Owner.View });
        }

        public static bool TryPollReload()
        {
            return Owner != null && Owner.PollReload();
        }

        public static void Shutdown(EntityManager entities)
        {
            if (HasOwnerEntity)
            {
                entities.DestroyEntity(OwnerEntity);
                HasOwnerEntity = false;
            }
            if (Owner != null)
            {
                Owner.Dispose();
                Owner = null;
            }
        }
    }
}
