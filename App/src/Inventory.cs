using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FisherLogger
{
    // =====================================================================================
    // NEXT-BAIT-05: инвентарь игрока из ответов сервера (Photon kind=3, param 1 = cmd, param 3 = byte[]).
    //   cmd 180 — страница полного снимка инвентаря (в захвате: несколько страниц по ≤ 101 записи);
    //   cmd 181 — выдача / изменение одного предмета (тот же формат записи);
    //   cmd 190 — удаление предмета: ровно 8 байт, int64 uid.
    // Формат записи (little-endian, BinaryReader клиента):
    //   int64 uid (читает вызывающий код), затем Weapon::readFromBytes (RVA 0x61B700, дизассемблировано):
    //   int64 shid (владелец), int32 inid, int64 addid (uid родителя, 0 = нет), int32 bagid, int32 cell, int32 count,
    //   int32 innerCount (→ Weapon.net_innerCount), float32 dur, bool isTimed (1 байт),
    //   DateTime = 6 × int16 (год, месяц, день, час, минута, секунда), string = int16 длина + UTF-8 (XML addParametr).
    //   Тип предмета (item_id) — элемент <tid> в XML (сверено: 534/534 записей есть в Weapons.txt).
    //   inid — НЕ тип предмета (значения 0/3); смысл UNKNOWN, хранится как есть.
    // Разбор строгий: любое нарушение формата → всё сообщение отклоняется (частичного применения нет).
    // =====================================================================================
    public sealed class InventoryRecord
    {
        public long Uid { get; init; }
        public long OwnerId { get; init; }
        public int Inid { get; init; }
        public long ParentUid { get; init; }
        public int Bag { get; init; }
        public int Cell { get; init; }
        public int Count { get; init; }
        public int InnerCount { get; init; }
        public float Dur { get; init; }
        public bool IsTimed { get; init; }
        public DateTime? ItemTime { get; init; }
        public string Xml { get; init; } = "";
        /// <summary>Тип предмета из XML &lt;tid&gt;; null — элемента нет (тип неизвестен, не угадывается).</summary>
        public int? ItemId { get; init; }
    }

    public sealed class InventoryMessage
    {
        public int Cmd { get; init; }                 // 180 / 181 / 190
        public DateTime ReceivedAt { get; init; }
        public List<InventoryRecord> Records { get; init; } = new();
        public long RemovedUid { get; init; }         // только 190
    }

    /// <summary>
    /// NEXT-BAIT-05: отдельный канал инвентаря. Сегменты сервер → клиент собираются по номерам TCP seq (SeqTcpReassembler:
    /// повторы отбрасываются, порядок восстанавливается, пропуск = сброс потока), поэтому большие страницы снимка
    /// (20–28 КБ) не портятся повторной передачей. Из собранного потока берутся только кадры kind=3 с полным заголовком
    /// FB len 00 01 F3 и командой 180/181/190. Основной путь разбора (TcpReassembler → ParseFrames) не меняется.
    /// </summary>
    public sealed class InventoryChannel
    {
        private SeqTcpReassembler _re = new();
        public event Action<string>? Rejected;
        public int Gaps { get; private set; }
        public int RejectedCount { get; private set; }
        public int Messages { get; private set; }

        public InventoryChannel() => Wire();

        private void Wire() => _re.GapDetected += g => { Gaps++; Rejected?.Invoke("пропуск данных TCP: " + g); };

        /// <summary>Новое соединение / реплей: начать сборку заново.</summary>
        public void Reset() { _re = new SeqTcpReassembler(); Wire(); }

        public List<InventoryMessage> Feed(TcpSegment? seg)
        {
            var list = new List<InventoryMessage>();
            if (seg == null || !seg.FromServer) return list;
            foreach (var ch in _re.Feed(seg))
            {
                var f = ch.Frame;
                if (f.Length < 20 || f[5] != 0x00 || f[6] != 0x01 || f[8] != 0x03) continue;
                var r = PacketDeepParser.DecodeCommand(f);
                if (r == null || !InventoryCodec.IsInventoryCmd(r.Value.cmd)) continue;
                if (InventoryCodec.TryDecode(r.Value.cmd, r.Value.data, seg.Timestamp, out var msg, out string err)) { list.Add(msg!); Messages++; }
                else { RejectedCount++; Rejected?.Invoke("сообщение " + r.Value.cmd + " отклонено: " + err); }
            }
            return list;
        }
    }

    public static class InventoryCodec
    {
        public const int CmdSnapshotPage = 180, CmdIssue = 181, CmdRemove = 190;
        public const int MaxRecordsPerMessage = 5000;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly Regex TidRx = new(@"<tid>\s*(\d{1,9})\s*</tid>", RegexOptions.CultureInvariant);

        public static bool IsInventoryCmd(int cmd) => cmd is CmdSnapshotPage or CmdIssue or CmdRemove;

        /// <summary>Строгий разбор данных (param 3). false + причина — сообщение отклонено целиком.</summary>
        public static bool TryDecode(int cmd, byte[] data, DateTime receivedAt, out InventoryMessage? msg, out string error)
        {
            msg = null; error = "";
            if (data == null) { error = "no data"; return false; }
            if (cmd == CmdRemove)
            {
                if (data.Length != 8) { error = "190: length " + data.Length + " != 8"; return false; }
                long uid = BitConverter.ToInt64(data, 0);
                if (uid <= 0) { error = "190: uid <= 0"; return false; }
                msg = new InventoryMessage { Cmd = cmd, ReceivedAt = receivedAt, RemovedUid = uid };
                return true;
            }
            if (cmd != CmdSnapshotPage && cmd != CmdIssue) { error = "not an inventory cmd"; return false; }
            var list = new List<InventoryRecord>();
            int p = 0;
            while (p < data.Length)
            {
                if (list.Count >= MaxRecordsPerMessage) { error = "too many records"; return false; }
                if (!TryReadRecord(data, ref p, out var rec, out error)) { error = cmd + ": record " + list.Count + " @" + p + ": " + error; return false; }
                list.Add(rec!);
            }
            if (list.Count == 0) { error = cmd + ": no records"; return false; }
            if (cmd == CmdIssue && list.Count != 1) { error = "181: " + list.Count + " records (expected 1)"; return false; }
            msg = new InventoryMessage { Cmd = cmd, ReceivedAt = receivedAt, Records = list };
            return true;
        }

        private static bool TryReadRecord(byte[] b, ref int p, out InventoryRecord? rec, out string error)
        {
            rec = null; error = "";
            const int Fixed = 8 + 8 + 4 + 8 + 4 + 4 + 4 + 4 + 4 + 1 + 12 + 2;   // 63 bytes before the string bytes
            if (p + Fixed > b.Length) { error = "truncated (fixed part)"; return false; }
            long uid = BitConverter.ToInt64(b, p); p += 8;
            long owner = BitConverter.ToInt64(b, p); p += 8;
            int inid = BitConverter.ToInt32(b, p); p += 4;
            long parent = BitConverter.ToInt64(b, p); p += 8;
            int bag = BitConverter.ToInt32(b, p); p += 4;
            int cell = BitConverter.ToInt32(b, p); p += 4;
            int count = BitConverter.ToInt32(b, p); p += 4;
            int inner = BitConverter.ToInt32(b, p); p += 4;
            float dur = BitConverter.ToSingle(b, p); p += 4;
            byte timed = b[p]; p += 1;
            var dt = new short[6];
            for (int i = 0; i < 6; i++) { dt[i] = BitConverter.ToInt16(b, p); p += 2; }
            short slen = BitConverter.ToInt16(b, p); p += 2;
            if (uid <= 0) { error = "uid <= 0"; return false; }
            if (owner <= 0) { error = "owner <= 0"; return false; }
            if (parent < 0) { error = "parent uid < 0"; return false; }
            if (count < 0 || inner < 0) { error = "negative count"; return false; }
            if (float.IsNaN(dur) || float.IsInfinity(dur)) { error = "dur not finite"; return false; }
            if (timed > 1) { error = "isTimed byte " + timed; return false; }
            DateTime? when = null;
            if (dt[0] != 0 || dt[1] != 0 || dt[2] != 0)
            {
                try { when = new DateTime(dt[0], dt[1], dt[2], dt[3], dt[4], dt[5]); }
                catch (ArgumentOutOfRangeException) { error = "invalid DateTime"; return false; }
            }
            string xml = "";
            if (slen > 0)
            {
                if (p + slen > b.Length) { error = "string length " + slen + " beyond data"; return false; }
                try { xml = StrictUtf8.GetString(b, p, slen); }
                catch (DecoderFallbackException) { error = "string not UTF-8"; return false; }
                p += slen;
            }
            int? itemId = null;
            var ms = TidRx.Matches(xml);
            if (ms.Count > 1) { error = "several <tid>"; return false; }
            if (ms.Count == 1)
            {
                int tid = int.Parse(ms[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (tid <= 0) { error = "tid <= 0"; return false; }
                itemId = tid;
            }
            rec = new InventoryRecord
            {
                Uid = uid, OwnerId = owner, Inid = inid, ParentUid = parent, Bag = bag, Cell = cell, Count = count, InnerCount = inner,
                Dur = dur, IsTimed = timed == 1, ItemTime = when, Xml = xml, ItemId = itemId
            };
            return true;
        }
    }
}
