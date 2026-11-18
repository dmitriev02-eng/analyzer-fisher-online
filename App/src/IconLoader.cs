using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace FisherLogger
{
    /// <summary>
    /// Ленивая загрузка PNG-иконок. Пути из словарей вида "assets/fish_icons/fish_38.png"
    /// разрешаются централизованно через AppPaths.AssetsDirectory (общая папка Data\assets),
    /// поэтому иконки работают и в сессии по умолчанию, и при --data-dir.
    /// Кэш не даёт многократно открывать один и тот же файл и блокировать его.
    /// </summary>
    public static class IconLoader
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, Image> Cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Сообщения о недостающих файлах (один раз на путь).</summary>
        public static Action<string>? Logger;

        public static string ResolvePath(string? relativeOrAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
                return "";

            string path = relativeOrAbsolutePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(path))
                return path;

            // "assets\fish_icons\x.png" -> <AssetsDirectory>\fish_icons\x.png
            string prefix = "assets" + Path.DirectorySeparatorChar;
            string underAssets = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(prefix.Length)
                : path;

            string shared = Path.Combine(AppPaths.AssetsDirectory, underAssets);
            if (File.Exists(shared)) return shared;

            // Совместимость: старая копия ассетов внутри папки сессии.
            string legacy = Path.Combine(AppPaths.BaseDirectory, path);
            if (File.Exists(legacy)) return legacy;

            return shared;
        }

        /// <summary>
        /// Возвращает Image или null (нет пути, нет файла, файл не читается). Изображение кэшируется.
        /// </summary>
        public static Image? Load(string? relativeOrAbsolutePath)
        {
            string path = ResolvePath(relativeOrAbsolutePath);
            if (string.IsNullOrEmpty(path))
                return null;

            lock (Sync)
            {
                if (Cache.TryGetValue(path, out Image? cached))
                    return cached;
                if (Missing.Contains(path))
                    return null;

                if (!File.Exists(path))
                {
                    Missing.Add(path);
                    try { Logger?.Invoke("[ICON] нет файла: " + path); } catch { }
                    return null;
                }

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var source = Image.FromStream(stream);
                    var copy = new Bitmap(source);
                    Cache[path] = copy;
                    return copy;
                }
                catch (Exception ex)
                {
                    Missing.Add(path);
                    try { Logger?.Invoke("[ICON] не читается: " + path + " (" + ex.Message + ")"); } catch { }
                    return null;
                }
            }
        }
    }
}
