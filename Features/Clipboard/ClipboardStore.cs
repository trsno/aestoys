using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Aestoys.Features.Clipboard;

internal sealed record ClipFormat(string Name, byte[] Data);

internal sealed record ClipRow(long Id, DateTime Date, string Text, bool Pinned, bool IsImage, bool IsFiles);

/// <summary>
/// Clipboard history database. Uses Ditto's table layout (Main = one row per clip, Data = one row
/// per clipboard format with the exact bytes), so Ditto's database imports as a straight copy and
/// nothing is converted or lost. Format names follow Ditto: CF_UNICODETEXT, CF_TEXT, CF_DIB,
/// CF_HDROP, and registered names such as "HTML Format" or "PNG".
/// </summary>
internal sealed class ClipboardStore : IDisposable
{
    public static string DefaultPath => Path.Combine(Services.AppSettings.Dir, "clipboard.db");

    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public ClipboardStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        // Same as Ditto's, so a copied Ditto.db is already in the right shape (extra Ditto tables are ignored).
        Exec("""
            CREATE TABLE IF NOT EXISTS Main(lID INTEGER PRIMARY KEY AUTOINCREMENT, lDate INTEGER, mText TEXT, lShortCut INTEGER, lDontAutoDelete INTEGER,
                CRC INTEGER, bIsGroup INTEGER, lParentID INTEGER, QuickPasteText TEXT, clipOrder REAL, clipGroupOrder REAL, globalShortCut INTEGER,
                lastPasteDate INTEGER, stickyClipOrder REAL, stickyClipGroupOrder REAL, MoveToGroupShortCut INTEGER, GlobalMoveToGroupShortCut INTEGER);
            CREATE TABLE IF NOT EXISTS Data(lID INTEGER PRIMARY KEY AUTOINCREMENT, lParentID INTEGER, strClipBoardFormat TEXT, ooData BLOB);
            CREATE INDEX IF NOT EXISTS Main_ClipOrder on Main(clipOrder DESC);
            CREATE INDEX IF NOT EXISTS Data_ParentId_Format ON Data(lParentID COLLATE BINARY ASC, strClipBoardFormat COLLATE NOCASE ASC);
            """);
    }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public int Count()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT count(*) FROM Main WHERE bIsGroup = 0";
            return Convert.ToInt32(c.ExecuteScalar());
        }
    }

    /// <summary>Newest first; <paramref name="search"/> matches anywhere in the text, case-insensitive.</summary>
    public List<ClipRow> List(string? search, int limit)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = """
                SELECT m.lID, m.lDate, m.mText, m.lDontAutoDelete,
                       EXISTS(SELECT 1 FROM Data d WHERE d.lParentID = m.lID AND d.strClipBoardFormat IN ('CF_DIB','PNG','CF_BITMAP')),
                       EXISTS(SELECT 1 FROM Data d WHERE d.lParentID = m.lID AND d.strClipBoardFormat = 'CF_HDROP')
                FROM Main m
                WHERE m.bIsGroup = 0 AND ($q IS NULL OR m.mText LIKE '%' || $q || '%' ESCAPE '\')
                ORDER BY m.lDontAutoDelete > 0 DESC, m.clipOrder DESC
                LIMIT $limit
                """;
            c.Parameters.AddWithValue("$q", string.IsNullOrWhiteSpace(search) ? DBNull.Value : Escape(search.Trim()));
            c.Parameters.AddWithValue("$limit", limit);
            var list = new List<ClipRow>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new ClipRow(r.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(r.IsDBNull(1) ? 0 : r.GetInt64(1)).LocalDateTime,
                    r.IsDBNull(2) ? "" : r.GetString(2), !r.IsDBNull(3) && r.GetInt64(3) > 0, r.GetInt64(4) != 0, r.GetInt64(5) != 0));
            return list;
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public List<ClipFormat> Formats(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT strClipBoardFormat, ooData FROM Data WHERE lParentID = $id ORDER BY lID";
            c.Parameters.AddWithValue("$id", id);
            var list = new List<ClipFormat>();
            using var r = c.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(1)) list.Add(new ClipFormat(r.GetString(0), (byte[])r[1]));
            return list;
        }
    }

    public byte[]? Format(long id, params string[] names)
    {
        lock (_lock)
        {
            foreach (var name in names)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "SELECT ooData FROM Data WHERE lParentID = $id AND strClipBoardFormat = $f LIMIT 1";
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$f", name);
                if (c.ExecuteScalar() is byte[] b) return b;
            }
            return null;
        }
    }

    /// <summary>
    /// Adds a clip, or moves an identical existing one to the top (same text and same bytes in its
    /// first format). Returns the clip's id.
    /// </summary>
    public long Add(string text, IReadOnlyList<ClipFormat> formats, int maxEntries)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            double top = TopOrder() + 1;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long? same = FindSame(text, formats);
            long id;
            if (same is long existing)
            {
                using var u = _db.CreateCommand();
                u.CommandText = "UPDATE Main SET clipOrder = $o, lDate = $d WHERE lID = $id";
                u.Parameters.AddWithValue("$o", top);
                u.Parameters.AddWithValue("$d", now);
                u.Parameters.AddWithValue("$id", existing);
                u.ExecuteNonQuery();
                id = existing;
            }
            else
            {
                using var m = _db.CreateCommand();
                m.CommandText = """
                    INSERT INTO Main(lDate, mText, lShortCut, lDontAutoDelete, CRC, bIsGroup, lParentID, QuickPasteText, clipOrder, clipGroupOrder,
                                     globalShortCut, lastPasteDate, stickyClipOrder, stickyClipGroupOrder, MoveToGroupShortCut, GlobalMoveToGroupShortCut)
                    VALUES($d, $t, 0, 0, $crc, 0, -1, '', $o, 0, 0, $d, 0, 0, 0, 0);
                    SELECT last_insert_rowid();
                    """;
                m.Parameters.AddWithValue("$d", now);
                m.Parameters.AddWithValue("$t", text);
                m.Parameters.AddWithValue("$crc", Crc(formats));
                m.Parameters.AddWithValue("$o", top);
                id = Convert.ToInt64(m.ExecuteScalar());
                foreach (var f in formats)
                {
                    using var d = _db.CreateCommand();
                    d.CommandText = "INSERT INTO Data(lParentID, strClipBoardFormat, ooData) VALUES($p, $f, $b)";
                    d.Parameters.AddWithValue("$p", id);
                    d.Parameters.AddWithValue("$f", f.Name);
                    d.Parameters.Add("$b", SqliteType.Blob).Value = f.Data;
                    d.ExecuteNonQuery();
                }
                Trim(maxEntries);
            }
            tx.Commit();
            return id;
        }
    }

    private double TopOrder()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT ifnull(max(clipOrder), 0) FROM Main";
        return Convert.ToDouble(c.ExecuteScalar());
    }

    private long? FindSame(string text, IReadOnlyList<ClipFormat> formats)
    {
        if (formats.Count == 0) return null;
        var first = formats[0];
        using var c = _db.CreateCommand();
        c.CommandText = """
            SELECT m.lID FROM Main m JOIN Data d ON d.lParentID = m.lID AND d.strClipBoardFormat = $f
            WHERE m.bIsGroup = 0 AND m.mText = $t AND length(d.ooData) = $len AND d.ooData = $b LIMIT 1
            """;
        c.Parameters.AddWithValue("$f", first.Name);
        c.Parameters.AddWithValue("$t", text);
        c.Parameters.AddWithValue("$len", first.Data.Length);
        c.Parameters.Add("$b", SqliteType.Blob).Value = first.Data;
        return c.ExecuteScalar() is long id ? id : null;
    }

    private static long Crc(IReadOnlyList<ClipFormat> formats)
    {
        uint h = 2166136261;
        foreach (var f in formats) foreach (var b in f.Data) h = (h ^ b) * 16777619;
        return h;
    }

    /// <summary>Keeps the newest <paramref name="max"/> clips; pinned clips are never removed.</summary>
    private void Trim(int max)
    {
        if (max <= 0) return;
        using var c = _db.CreateCommand();
        c.CommandText = """
            DELETE FROM Data WHERE lParentID IN (SELECT lID FROM Main WHERE bIsGroup = 0 AND ifnull(lDontAutoDelete,0) = 0
                AND lID NOT IN (SELECT lID FROM Main WHERE bIsGroup = 0 ORDER BY clipOrder DESC LIMIT $max));
            DELETE FROM Main WHERE bIsGroup = 0 AND ifnull(lDontAutoDelete,0) = 0
                AND lID NOT IN (SELECT lID FROM Main WHERE bIsGroup = 0 ORDER BY clipOrder DESC LIMIT $max);
            """;
        c.Parameters.AddWithValue("$max", max);
        c.ExecuteNonQuery();
    }

    public void MoveToTop(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE Main SET clipOrder = (SELECT ifnull(max(clipOrder),0) + 1 FROM Main), lastPasteDate = $d WHERE lID = $id";
            c.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE Main SET lDontAutoDelete = $p WHERE lID = $id";
            c.Parameters.AddWithValue("$p", pinned ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0);
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "DELETE FROM Data WHERE lParentID = $id; DELETE FROM Main WHERE lID = $id;";
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Per-format row counts and byte totals, to compare an import with its source.</summary>
    public static List<(string format, long rows, long bytes)> Census(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT strClipBoardFormat, count(*), ifnull(sum(length(ooData)),0) FROM Data GROUP BY 1 ORDER BY 1";
        var list = new List<(string, long, long)>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        using var m = db.CreateCommand();
        m.CommandText = "SELECT 'clips', count(*), 0 FROM Main WHERE bIsGroup = 0";
        using var r2 = m.ExecuteReader();
        while (r2.Read()) list.Add((r2.GetString(0), r2.GetInt64(1), 0));
        return list;
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
