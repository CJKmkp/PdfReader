using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfReader
{
    /// <summary>
    /// 插件配置，持久化到 PluginConfigFolder/config.json。
    /// 嵌入式模式下没有自己的窗口，因此只保留渲染与记忆相关的项。
    /// </summary>
    internal sealed class ReaderConfig
    {
        /// <summary>
        /// 渲染质量档位：性能=固定 2.0 倍（默认，现状水平）；均衡=固定 3.0 倍；
        /// 质量=按视口算到最大缩放（8×）下吃满显示密度，上限放宽到 16384px/320MP。
        /// </summary>
        [JsonPropertyName("renderQuality")]
        public RenderQualityMode RenderQuality { get; set; } = RenderQualityMode.Performance;

        /// <summary>旧版渲染倍率（1–4×），设置页滑条仍读写它，但渲染已按 <see cref="RenderQuality"/> 走。</summary>
        [JsonPropertyName("renderScale")]
        public double RenderScale { get; set; } = 2.0;

        [JsonPropertyName("rememberLastDocument")]
        public bool RememberLastDocument { get; set; } = true;

        /// <summary>页面缓存字节预算（MB）。</summary>
        [JsonPropertyName("cacheBudgetMb")]
        public int CacheBudgetMb { get; set; } = 192;

        [JsonPropertyName("lastDocumentPath")]
        public string LastDocumentPath { get; set; } = "";

        [JsonPropertyName("lastPageIndex")]
        public int LastPageIndex { get; set; }

        /// <summary>旧版渲染倍率的有效值，限制在 1.0–4.0（设置页滑条显示用）。</summary>
        [JsonIgnore]
        public double NormalizedRenderScale
        {
            get
            {
                if (double.IsNaN(RenderScale) || double.IsInfinity(RenderScale)) return 2.0;
                if (RenderScale < 1.0) return 1.0;
                if (RenderScale > 4.0) return 4.0;
                return RenderScale;
            }
        }

        [JsonIgnore]
        public int NormalizedCacheBudgetMb
        {
            get
            {
                if (CacheBudgetMb < 32) return 32;
                if (CacheBudgetMb > 1024) return 1024;
                return CacheBudgetMb;
            }
        }

        [JsonIgnore]
        public long CacheBudgetBytes => NormalizedCacheBudgetMb * 1024L * 1024L;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static ReaderConfig Load(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var config = JsonSerializer.Deserialize<ReaderConfig>(json, Options);
                        if (config != null)
                        {
                            config.Normalize();
                            return config;
                        }
                    }
                }
            }
            catch
            {
                // 配置损坏时回退到默认值，不阻塞插件加载
            }
            return new ReaderConfig();
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            }
            catch
            {
                // 写盘失败不应影响使用
            }
        }

        private void Normalize()
        {
            RenderScale = NormalizedRenderScale;
            CacheBudgetMb = NormalizedCacheBudgetMb;
            if (LastPageIndex < 0) LastPageIndex = 0;
            if (LastDocumentPath == null) LastDocumentPath = "";
        }
    }
}
