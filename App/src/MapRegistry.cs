using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-135: реестр подложек карт водоёмов.
    //  * Файл: <AssetsDirectory>\maps\map_registry.json (обычно D:\TheFisherProject\Data\assets\maps).
    //  * Ключ — location_id = номер базы TFBase_N из Player.log (не spawn_locid!).
    //  * Координаты: мировые X (восток, вправо) / Z (север, вверх) игры.
    //      px = (X - MinX) / (MaxX - MinX) * Width,  py = (MaxZ - Z) / (MaxZ - MinZ) * Height
    //    Пиксельные координаты непрерывные; floor() — только при выборке пикселя.
    //    Точки вне границ НЕ переносятся на край.
    //  * Изображение проверяется по SHA-256 и размеру из реестра; при несовпадении подложка не показывается.
    // =====================================================================================
    public sealed class WaterbodyMapInfo
    {
        public uint LocationId { get; set; }
        public string Name { get; set; } = "";
        public string Bundle { get; set; } = "";
        public string Prefab { get; set; } = "";
        public string File { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
        public string Sha256 { get; set; } = "";
        public string MapVersion { get; set; } = "";
        public string Confidence { get; set; } = "";
        public string Validation { get; set; } = "";
        public string ValidationText { get; set; } = "";
        public double? WaterLevel { get; set; }
        public string Background { get; set; } = "";
        public string Source { get; set; } = "";

        // NEXT-MAP-RESOLUTION-01: необязательная подложка повышенного разрешения ТОЛЬКО для показа в окне карты.
        // Та же рамка (MinX..MaxZ) и тот же алгоритм построения; Width/Height/File/Sha256 выше не меняются
        // (по ним работают координаты, оверлей, батиметрия и API компаньона).
        public string HdFile { get; set; } = "";
        public int HdWidth { get; set; }
        public int HdHeight { get; set; }
        public string HdSha256 { get; set; } = "";
        /// <summary>HD-подложка задана и имеет те же пропорции, что основная карта.</summary>
        public bool HasHd => !string.IsNullOrWhiteSpace(HdFile) && HdWidth > 0 && HdHeight > 0 && Width > 0 && Height > 0 &&
                             (long)HdWidth * Height == (long)HdHeight * Width;

        public double SpanX => MaxX - MinX;
        public double SpanZ => MaxZ - MinZ;
        public bool IsValid => Width > 0 && Height > 0 && SpanX > 0 && SpanZ > 0 && !string.IsNullOrWhiteSpace(File);

        /// <summary>Точка внутри границ карты (границы включительно).</summary>
        public bool Contains(double x, double z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

        /// <summary>Мир → непрерывные пиксельные координаты изображения (без ограничения границами).</summary>
        public (double px, double py) WorldToPixel(double x, double z) =>
            ((x - MinX) / SpanX * Width, (MaxZ - z) / SpanZ * Height);

        /// <summary>Непрерывные пиксельные координаты → мир.</summary>
        public (double x, double z) PixelToWorld(double px, double py) =>
            (MinX + px / Width * SpanX, MaxZ - py / Height * SpanZ);
    }

    public static class MapRegistry
    {
        public const string RegistryRelativePath = "assets/maps/map_registry.json";

        private static readonly object Sync = new();
        private static Dictionary<uint, WaterbodyMapInfo>? _maps;
        private static readonly Dictionary<string, Bitmap?> Images = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _errors = new();

        private sealed class RegistryFile
        {
            public int Format { get; set; }
            public List<WaterbodyMapInfo> Maps { get; set; } = new();
        }

        /// <summary>Ошибки чтения реестра/изображений (для статуса окна).</summary>
        public static IReadOnlyList<string> Errors { get { lock (Sync) return _errors.ToArray(); } }

        public static string RegistryPath => IconLoader.ResolvePath(RegistryRelativePath);

        public static IReadOnlyCollection<WaterbodyMapInfo> All
        {
            get { lock (Sync) { EnsureLoaded(); return _maps!.Values; } }
        }

        public static WaterbodyMapInfo? Get(uint? locationId)
        {
            if (!locationId.HasValue) return null;
            lock (Sync)
            {
                EnsureLoaded();
                return _maps!.TryGetValue(locationId.Value, out var m) ? m : null;
            }
        }

        /// <summary>Сбросить кэш (повторное чтение реестра и изображений при следующем обращении).</summary>
        public static void Reload()
        {
            lock (Sync)
            {
                _maps = null;
                foreach (var b in Images.Values) b?.Dispose();
                Images.Clear();
                _errors.Clear();
            }
        }

        private static void EnsureLoaded()
        {
            if (_maps != null) return;
            _maps = new Dictionary<uint, WaterbodyMapInfo>();
            string path = RegistryPath;
            try
            {
                if (!System.IO.File.Exists(path)) { _errors.Add("нет реестра карт: " + path); return; }
                var opt = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var reg = JsonSerializer.Deserialize<RegistryFile>(System.IO.File.ReadAllText(path), opt);
                foreach (var m in reg?.Maps ?? new List<WaterbodyMapInfo>())
                {
                    if (!m.IsValid) { _errors.Add("некорректная запись реестра карт: location_id " + m.LocationId); continue; }
                    if (!_maps.TryAdd(m.LocationId, m)) _errors.Add("повтор location_id в реестре карт: " + m.LocationId);
                }
            }
            catch (Exception ex)
            {
                _errors.Add("реестр карт не читается: " + ex.Message);
            }
        }

        /// <summary>
        /// Загружает изображение карты (кэшируется). Проверяет SHA-256 и размер по реестру;
        /// при любом несовпадении возвращает null — тогда окно остаётся на нейтральной сетке.
        /// </summary>
        public static Bitmap? LoadImage(WaterbodyMapInfo map)
        {
            string path = IconLoader.ResolvePath("assets/maps/" + map.File);
            lock (Sync)
            {
                if (Images.TryGetValue(path, out var cached)) return cached;
                Bitmap? result = null;
                try
                {
                    if (!System.IO.File.Exists(path))
                        _errors.Add("нет файла карты: " + path);
                    else
                    {
                        byte[] bytes = System.IO.File.ReadAllBytes(path);
                        string hash = Convert.ToHexString(SHA256.HashData(bytes));
                        if (!string.IsNullOrEmpty(map.Sha256) && !hash.Equals(map.Sha256, StringComparison.OrdinalIgnoreCase))
                            _errors.Add("SHA-256 карты не совпадает с реестром: " + map.File);
                        else
                        {
                            using var ms = new MemoryStream(bytes);
                            using var src = new Bitmap(ms);
                            if (src.Width != map.Width || src.Height != map.Height)
                                _errors.Add("размер карты " + map.File + " " + src.Width + "x" + src.Height + " не совпадает с реестром");
                            else
                                result = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppPArgb);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _errors.Add("карта не читается: " + map.File + " (" + ex.Message + ")");
                }
                Images[path] = result;
                return result;
            }
        }

        /// <summary>
        /// NEXT-MAP-RESOLUTION-01: загружает HD-подложку (если задана в реестре) с проверкой SHA-256, размера и пропорций.
        /// Возвращает НОВЫЙ Bitmap, которым владеет вызывающий (не кэшируется: 4096² ≈ 64 МБ), или null —
        /// тогда вызывающий показывает обычную проверенную карту (LoadImage).
        /// </summary>
        public static Bitmap? LoadHdImage(WaterbodyMapInfo map)
        {
            if (!map.HasHd) return null;
            string path = IconLoader.ResolvePath("assets/maps/" + Path.GetFileName(map.HdFile));
            string? err = null;
            Bitmap? result = null;
            try
            {
                if (!System.IO.File.Exists(path)) err = "нет файла HD-карты: " + path;
                else
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    string hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (string.IsNullOrEmpty(map.HdSha256) || !hash.Equals(map.HdSha256, StringComparison.OrdinalIgnoreCase))
                        err = "SHA-256 HD-карты не совпадает с реестром: " + map.HdFile;
                    else
                    {
                        using var ms = new MemoryStream(bytes);
                        using var src = new Bitmap(ms);
                        if (src.Width != map.HdWidth || src.Height != map.HdHeight)
                            err = "размер HD-карты " + map.HdFile + " " + src.Width + "x" + src.Height + " не совпадает с реестром";
                        else
                            result = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppPArgb);
                    }
                }
            }
            catch (Exception ex)
            {
                result?.Dispose(); result = null;
                err = "HD-карта не читается: " + map.HdFile + " (" + ex.Message + ")";
            }
            if (err != null) lock (Sync) { if (!_errors.Contains(err)) _errors.Add(err); }
            return result;
        }
    }
    /// <summary>
    /// Отрисовка подложки карты в прямоугольной области (общая для окна карты и оверлея).
    /// Экранная точка мира: sx = W/2 + (x - centerX)*scale, sy = H/2 - (z - centerZ)*scale (X вправо, Z вверх),
    /// т. е. то же преобразование, что MapCanvas.WorldToScreen. Рисуется только видимая часть (src/dest).
    /// </summary>
    public static class MapRenderer
    {
        public static void DrawBackground(Graphics g, WaterbodyMapInfo m, Image img, double centerX, double centerZ, double scale, int width, int height)
        {
            if (width <= 0 || height <= 0 || scale <= 0) return;
            double left = width / 2.0 + (m.MinX - centerX) * scale;
            double right = width / 2.0 + (m.MaxX - centerX) * scale;
            double top = height / 2.0 - (m.MaxZ - centerZ) * scale;
            double bottom = height / 2.0 - (m.MinZ - centerZ) * scale;
            double vl = Math.Max(left, 0), vt = Math.Max(top, 0), vr = Math.Min(right, width), vb = Math.Min(bottom, height);
            if (vr <= vl || vb <= vt || right <= left || bottom <= top) return;
            double ipx = img.Width / (right - left), ipy = img.Height / (bottom - top); // image pixels per screen pixel
            var src = new RectangleF((float)((vl - left) * ipx), (float)((vt - top) * ipy), (float)((vr - vl) * ipx), (float)((vb - vt) * ipy));
            var dest = new[] { new PointF((float)vl, (float)vt), new PointF((float)vr, (float)vt), new PointF((float)vl, (float)vb) };
            var state = g.Save();
            try
            {
                g.PixelOffsetMode = PixelOffsetMode.Half;   // pixel (i,j) covers [i,i+1) - no half-pixel shift
                g.InterpolationMode = ipx > 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(WrapMode.TileFlipXY);         // no semi-transparent seam at the map edge
                g.DrawImage(img, dest, src, GraphicsUnit.Pixel, ia);
                using var pen = new Pen(Color.FromArgb(150, 210, 225, 240), 1f);
                float bl = (float)Math.Max(left, -10), bt = (float)Math.Max(top, -10);
                float br = (float)Math.Min(right, width + 10), bb = (float)Math.Min(bottom, height + 10);
                g.DrawRectangle(pen, bl, bt, br - bl, bb - bt);
            }
            finally { g.Restore(state); }
        }
    }
}
