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

    public sealed class TimelineLibraryOwner : IDisposable
    {
        global::Tl.TimelineWorld _world;
        KindIndex _kinds;
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
            try
            {
                _kinds = BuildKinds(_world);
            }
            catch
            {
                _world.Dispose();
                throw;
            }
            _writeTicks = Stamp(path);
        }

        public Entity Entity { get; internal set; }

        public uint Generation { get; private set; } = 1;

        public KindIndex Kinds
        {
            get
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TimelineLibraryOwner));
                return _kinds;
            }
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

        public TimelineLibrary Component => new TimelineLibrary { View = View, Kinds = Kinds, Generation = Generation };

        public bool IsStale => !_disposed && Stamp(_path) != _writeTicks;

        public bool PollReload()
        {
            if (!IsStale)
                return false;
            _world.Reload(File.ReadAllBytes(_path));
            _kinds.Dispose();
            _kinds = BuildKinds(_world);
            _writeTicks = Stamp(_path);
            Generation++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _kinds.Dispose();
            _world.Dispose();
            GC.SuppressFinalize(this);
        }

        static KindIndex BuildKinds(global::Tl.TimelineWorld world)
        {
            var kinds = KindIndex.Build(world.View, world);
            try
            {
                kinds.Bind(KindRegistry.Discover());
            }
            catch
            {
                kinds.Dispose();
                throw;
            }
            return kinds;
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

#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TimelineLibraryBootstrap
    {
        static readonly Dictionary<ulong, TimelineLibraryOwner> Owners = new Dictionary<ulong, TimelineLibraryOwner>();
        static string SourcePath;
        static byte[] SourceBytes;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ForgetPreviousPlay()
        {
            foreach (var owner in Owners.Values)
                owner.Dispose();
            Owners.Clear();
            SourcePath = null;
            SourceBytes = null;
        }

        public static bool HasSource => SourceBytes != null;

        public static void Use(string path, byte[] tlib)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (tlib == null || tlib.Length == 0)
                throw new ArgumentException("Timeline library bytes are empty.", nameof(tlib));
            SourcePath = path;
            SourceBytes = tlib;
        }

        public static void Release()
        {
            SourcePath = null;
            SourceBytes = null;
        }

        public static TimelineLibraryOwner Ensure(EntityManager entities, string path, byte[] tlib)
        {
            var key = entities.WorldUnmanaged.SequenceNumber;
            if (Owners.TryGetValue(key, out var existing))
                return existing;
            TlGeneratedBootstrap.InstallDiscoveredModules();
            var owner = new TimelineLibraryOwner(path, tlib);
            var entity = entities.CreateEntity(typeof(TimelineLibrary), typeof(TimelineStep), typeof(ClosedPlayback));
            entities.SetComponentData(entity, owner.Component);
            entities.SetComponentData(entity, new TimelineStep { Forward = true });
            owner.Entity = entity;
            Owners.Add(key, owner);
            return owner;
        }

        public static TimelineLibraryOwner Find(EntityManager entities)
        {
            return Owners.TryGetValue(entities.WorldUnmanaged.SequenceNumber, out var owner) ? owner : null;
        }

        internal static bool LoadSource(EntityManager entities)
        {
            if (Find(entities) != null)
                return true;
            if (SourceBytes == null)
                return false;
            Ensure(entities, SourcePath, SourceBytes);
            return true;
        }

        internal static void PollReload(EntityManager entities)
        {
            var owner = Find(entities);
            if (owner == null || !owner.IsStale)
                return;
            entities.CompleteAllTrackedJobs();
            if (owner.PollReload() && entities.Exists(owner.Entity))
                entities.SetComponentData(owner.Entity, owner.Component);
        }

        internal static void Forget(EntityManager entities)
        {
            var key = entities.WorldUnmanaged.SequenceNumber;
            if (!Owners.TryGetValue(key, out var owner))
                return;
            entities.CompleteAllTrackedJobs();
            owner.Dispose();
            Owners.Remove(key);
        }

        public static void Shutdown(EntityManager entities)
        {
            var owner = Find(entities);
            if (owner == null)
                return;
            entities.CompleteAllTrackedJobs();
            if (entities.Exists(owner.Entity))
                entities.DestroyEntity(owner.Entity);
            Forget(entities);
        }
    }
}
