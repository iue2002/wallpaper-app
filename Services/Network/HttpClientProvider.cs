using System;
using System.Net;
using System.Net.Http;

namespace App1.Services.Network
{
    /// <summary>
    /// 全应用共享的 HttpClient。
    ///
    /// 为什么要这个：
    ///   之前每次下载图片都 `new HttpClient(...)`，用完就 Dispose。看起来没毛病，
    ///   实际每次都要重新做 TCP 三次握手 + TLS 协商（国内到境外图床单这一项就两三百毫秒），
    ///   而且旧连接会在 TIME_WAIT 状态挂好几分钟，连开十几张图就把本机端口耗掉一片。
    ///
    ///   HttpClient 本身是线程安全、可长期复用的（它的 Dispose 才会销毁连接池）。
    ///   所以这里做成全局单例，连接池常驻，第二张图以后基本省掉握手时间。
    /// </summary>
    public static class HttpClientProvider
    {
        /// <summary>
        /// 下载图片用。关闭系统代理 —— 本机若挂了代理软件，图片走代理反而更慢，
        /// 而且部分图床（Upx8、Qihu）通过国内代理会直接 403。
        /// </summary>
        public static HttpClient Images { get; } = Create(TimeSpan.FromSeconds(30));

        /// <summary>
        /// 只取文件头、探测尺寸用。超时给得短，探不到就当不合格，不拖累整批。
        /// </summary>
        public static HttpClient Probe { get; } = Create(TimeSpan.FromSeconds(8));

        private static HttpClient Create(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                Proxy = null,

                // 连接池：一个源同时最多 4 条连接，够用又不至于把对方 CDN 打限流
                MaxConnectionsPerServer = 4,

                // 连接最多复用 5 分钟。太长会在 CDN 侧变成「陈旧连接」反而失败，
                // 太短又回到每次握手的老路。
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // 壁纸图床普遍开 gzip，JPEG 本身压不动，但 JSON 接口（Pexels/Bing）能省一半体积
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            var client = new HttpClient(handler)
            {
                Timeout = timeout,

                // 部分图床（必应、Pixabay）对空 UA 直接返回 403
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36");

            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");

            return client;
        }
    }
}
