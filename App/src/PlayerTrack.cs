using System;
using System.Collections.Generic;

namespace FisherLogger
{
    // =====================================================================================
    // OVERLAY-03: живая позиция игрока из c2s kind=2 cmd 1006 (byte[] param 3).
    //  * +0  UInt32 player ID (NEXT-ANALYTICS-13), +9 X,Y,Z double LE, +33 поворот X,Y,Z double LE (градусы).
    //  * Проверено на записях CONTROLS-01 (cap_1629/1648, 12.11): расстояние сервера «игрок → наживка» совпадает
    //    с позицией 1006 (n=482, разброс 0,5 м); поворот Y = направление на точку заброса (17 забросов, медиана 1,8°).
    //  * Частота: раз в ~10 с, плюс при забросе и телепорте. Между пакетами позиция не известна (не выдумывается).
    //  * Только чтение трафика; в БД не пишется.
    // =====================================================================================
    public sealed class PlayerPose
    {
        public DateTime Time;          // время ПК при приёме
        public double X, Y, Z;
        /// <summary>Направление взгляда, градусы 0…360: 0 = +Z (север карты), 90 = +X (восток). null — нет в пакете.</summary>
        public double? Yaw;
        public int Length;             // длина byte[] (диагностика: 447 обычный, 143 при забросе)
    }

    public sealed class PlayerTrack
    {
        public const int Capacity = 4000;            // ~11 ч при 10 с
        private readonly object _lock = new();
        private readonly List<PlayerPose> _list = new();
        private PlayerPose? _last;

        public PlayerPose? Last { get { lock (_lock) return _last; } }
        public int Count { get { lock (_lock) return _list.Count; } }

        /// <summary>Разбор позы из данных cmd 1006. null — данные не похожи на позицию.</summary>
        public static PlayerPose? Parse(byte[] data, DateTime time)
        {
            if (data == null || data.Length < 33) return null;
            double x = BitConverter.ToDouble(data, 9), y = BitConverter.ToDouble(data, 17), z = BitConverter.ToDouble(data, 25);
            if (!Ok(x) || !Ok(y) || !Ok(z) || Math.Abs(x) > 20000 || Math.Abs(z) > 20000 || y < -1000 || y > 5000) return null;
            if (x == 0 && y == 0 && z == 0) return null;
            double? yaw = null;
            if (data.Length >= 57)
            {
                double rx = BitConverter.ToDouble(data, 33), ry = BitConverter.ToDouble(data, 41), rz = BitConverter.ToDouble(data, 49);
                if (Ok(rx) && Ok(ry) && Ok(rz) && !(rx == 0 && ry == 0 && rz == 0) && Math.Abs(ry) <= 3600)
                    yaw = ((ry % 360) + 360) % 360;
            }
            return new PlayerPose { Time = time, X = x, Y = y, Z = z, Yaw = yaw, Length = data.Length };
            static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>Добавить позу. Старые пакеты (импорт записи) не вытесняют живые: поза старше последней на 60 с отбрасывается.</summary>
        public bool Add(PlayerPose p)
        {
            lock (_lock)
            {
                if (_last != null && p.Time < _last.Time.AddSeconds(-60)) return false;
                if (_last != null && p.Time < _last.Time)
                {
                    int i = _list.FindLastIndex(q => q.Time <= p.Time);
                    _list.Insert(i + 1, p);
                }
                else { _list.Add(p); _last = p; }
                if (_list.Count > Capacity) _list.RemoveRange(0, _list.Count - Capacity);
                return true;
            }
        }

        /// <summary>Ближайшая по времени поза в пределах maxSec (для «где стоял игрок при забросе»).</summary>
        public PlayerPose? At(DateTime t, double maxSec = 15)
        {
            lock (_lock)
            {
                PlayerPose? best = null; double bd = maxSec;
                for (int i = _list.Count - 1; i >= 0; i--)
                {
                    var q = _list[i];
                    double d = Math.Abs((q.Time - t).TotalSeconds);
                    if (d <= bd) { bd = d; best = q; }
                    if (q.Time < t.AddSeconds(-maxSec)) break;
                }
                return best;
            }
        }

        public void Clear() { lock (_lock) { _list.Clear(); _last = null; } }
    }
}
