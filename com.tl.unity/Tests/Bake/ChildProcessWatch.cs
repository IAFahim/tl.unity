using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tl.Unity.Bake.Tests
{
    sealed class ChildProcessWatch : IDisposable
    {
        readonly int _self = Process.GetCurrentProcess().Id;
        readonly HashSet<int> _before;
        readonly HashSet<int> _spawned = new HashSet<int>();
        readonly Thread _thread;
        volatile bool _stop;
        public int Polls;

        public ChildProcessWatch()
        {
            _before = Children();
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            while (Volatile.Read(ref Polls) == 0) Thread.Yield();
        }

        public IReadOnlyCollection<int> Spawned
        {
            get
            {
                lock (_spawned) return new List<int>(_spawned);
            }
        }

        public static bool Supported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Directory.Exists("/proc/self");

        void Run()
        {
            while (!_stop)
            {
                Poll();
                Thread.Sleep(1);
            }
        }

        void Poll()
        {
            foreach (var pid in Children())
                if (!_before.Contains(pid))
                    lock (_spawned) _spawned.Add(pid);
            Interlocked.Increment(ref Polls);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(5000);
            Poll();
        }

        HashSet<int> Children()
        {
            var result = new HashSet<int>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var snapshot = CreateToolhelp32Snapshot(2, 0);
                if (snapshot == new IntPtr(-1)) return result;
                try
                {
                    var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                    for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                        if (entry.ParentProcessId == _self) result.Add((int)entry.ProcessId);
                }
                finally { CloseHandle(snapshot); }
                return result;
            }
            if (!Directory.Exists("/proc")) return result;
            foreach (var dir in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                    if (int.Parse(fields[1]) == _self) result.Add(pid);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
