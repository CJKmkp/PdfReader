using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PdfReader
{
    /// <summary>
    /// 已渲染页面的有界 LRU 缓存。键为 (页序号, 宽度桶)，
    /// 同时受条目数与字节预算两个上限约束，任一超出即淘汰最久未使用的条目。
    /// </summary>
    internal sealed class PageRenderCache
    {
        private readonly struct Key : IEquatable<Key>
        {
            public readonly int PageIndex;
            public readonly int WidthBucket;

            public Key(int pageIndex, int widthBucket)
            {
                PageIndex = pageIndex;
                WidthBucket = widthBucket;
            }

            public bool Equals(Key other) => PageIndex == other.PageIndex && WidthBucket == other.WidthBucket;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => (PageIndex * 397) ^ WidthBucket;
        }

        private sealed class Entry
        {
            public BitmapSource Bitmap;
            public long Bytes;
            public LinkedListNode<Key> Node;
        }

        private readonly Dictionary<Key, Entry> _map = new Dictionary<Key, Entry>();
        private readonly LinkedList<Key> _lru = new LinkedList<Key>();   // 头 = 最近使用
        private readonly object _gate = new object();

        private int _maxEntries;
        private long _maxBytes;
        private long _currentBytes;

        public PageRenderCache(int maxEntries, long maxBytes)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _maxBytes = Math.Max(1024L * 1024, maxBytes);
        }

        public long CurrentBytes
        {
            get { lock (_gate) return _currentBytes; }
        }

        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        /// <summary>调整字节预算，立即按新预算淘汰。</summary>
        public void SetBudget(int maxEntries, long maxBytes)
        {
            lock (_gate)
            {
                _maxEntries = Math.Max(1, maxEntries);
                _maxBytes = Math.Max(1024L * 1024, maxBytes);
                Trim();
            }
        }

        public bool TryGet(int pageIndex, int widthBucket, out BitmapSource bitmap)
        {
            var key = new Key(pageIndex, widthBucket);
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                    bitmap = entry.Bitmap;
                    return true;
                }
            }
            bitmap = null;
            return false;
        }

        public void Put(int pageIndex, int widthBucket, BitmapSource bitmap)
        {
            if (bitmap == null) return;
            var key = new Key(pageIndex, widthBucket);
            long bytes = EstimateBytes(bitmap);

            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _currentBytes -= existing.Bytes;
                    existing.Bitmap = bitmap;
                    existing.Bytes = bytes;
                    _currentBytes += bytes;
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                }
                else
                {
                    var node = _lru.AddFirst(key);
                    _map[key] = new Entry { Bitmap = bitmap, Bytes = bytes, Node = node };
                    _currentBytes += bytes;
                }
                Trim();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
                _currentBytes = 0;
            }
        }

        /// <summary>在锁内调用。</summary>
        private void Trim()
        {
            while ((_map.Count > _maxEntries || _currentBytes > _maxBytes) && _lru.Count > 0)
            {
                var last = _lru.Last;
                if (last == null) break;
                // 至少保留一条，否则刚放进来的图会被立刻淘汰
                if (_map.Count <= 1) break;
                _lru.RemoveLast();
                if (_map.TryGetValue(last.Value, out var entry))
                {
                    _currentBytes -= entry.Bytes;
                    _map.Remove(last.Value);
                }
            }
            if (_currentBytes < 0) _currentBytes = 0;
        }

        private static long EstimateBytes(BitmapSource bitmap)
        {
            try
            {
                int bpp = bitmap.Format.BitsPerPixel;
                if (bpp <= 0) bpp = 32;
                return (long)bitmap.PixelWidth * bitmap.PixelHeight * bpp / 8;
            }
            catch
            {
                return 4L * 1024 * 1024;
            }
        }
    }
}
