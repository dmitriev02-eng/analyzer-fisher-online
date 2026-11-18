using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FisherLogger
{
    /// <summary>
    /// AUDIT-04 (C3): журнал программы — один открытый файл и запись на диск раз в секунду (раньше: открыть-дописать-закрыть на каждую строку).
    /// При выходе и при ошибках строки сбрасываются сразу. Файл открыт с FileShare.ReadWrite — его можно читать во время работы.
    /// </summary>
    public static class LogFile
    {
        private static readonly object Sync = new();
        private static StreamWriter? _w;
        private static string _path = "";
        private static bool _dirty;
        private static System.Threading.Timer? _timer;

        public static void Append(string path, string text, bool flushNow = false)
        {
            lock (Sync)
            {
                if (_w == null || !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                {
                    try { _w?.Flush(); _w?.Dispose(); } catch { }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = false };
                    _path = path;
                    if (_timer == null)
                    {
                        _timer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
                        AppDomain.CurrentDomain.ProcessExit += (s, e) => Close();
                    }
                }
                _w.Write(text);
                _dirty = true;
                if (flushNow) { _w.Flush(); _dirty = false; }
            }
        }

        public static void Flush()
        {
            lock (Sync) { if (_dirty && _w != null) { try { _w.Flush(); } catch { } _dirty = false; } }
        }

        public static void Close()
        {
            lock (Sync) { try { _w?.Flush(); _w?.Dispose(); } catch { } _w = null; _path = ""; _dirty = false; }
        }
    }
}
