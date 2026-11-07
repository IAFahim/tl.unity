using System;
using System.Collections.Generic;
using UnityEngine;

namespace KindGame
{
    public sealed class LogScan : IDisposable
    {
        readonly List<string> _found = new List<string>();

        public LogScan()
        {
            Application.logMessageReceivedThreaded += OnLog;
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Log)
                return;
            lock (_found)
                _found.Add(type + ": " + message);
        }

        public string Report()
        {
            lock (_found)
                return string.Join("\n", _found);
        }

        public void Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
        }
    }
}
