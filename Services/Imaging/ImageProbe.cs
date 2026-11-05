using System;
using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App1.Services.Imaging
{
    /// <summary>
    /// 图片尺寸探测器。
    ///
    /// 只读取图片文件的前 64KB 解析文件头，拿到宽高后立即断开连接，
    /// 不会把整张图下载下来。用于在"决定要不要这张图"时以最低成本取到尺寸。
    ///
    /// 支持 JPEG / PNG / GIF / BMP / WebP，覆盖当前所有壁纸源。
    /// 服务器忽略 Range 请求（返回 200 全量）时同样安全：
    /// ResponseHeadersRead + 读满 64KB 后释放流，连接会被中断。
    /// </summary>
    public static class ImageProbe
    {
        private const int MaxHeaderBytes = 64 * 1024;

        /// <summary>
        /// 探测图片宽高。失败返回 (0, 0)，调用方按不合格处理。
        /// </summary>
        public static async Task<(int Width, int Height)> GetSizeAsync(
            HttpClient client,
            string url,
            CancellationToken cancellationToken = default)
        {
            if (client == null || string.IsNullOrWhiteSpace(url))
            {
                return (0, 0);
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new RangeHeaderValue(0, MaxHeaderBytes - 1);

                    using (var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return (0, 0);
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            var buffer = new byte[MaxHeaderBytes];
                            int total = 0;

                            while (total < buffer.Length)
                            {
                                int read = await stream.ReadAsync(
                                    buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                                if (read <= 0)
                                {
                                    break;
                                }

                                total += read;

                                // 已经能解析出尺寸就不必继续读
                                var partial = Parse(buffer, total);
                                if (partial.Width > 0 && partial.Height > 0)
                                {
                                    return partial;
                                }
                            }

                            return Parse(buffer, total);
                        }
                    }
                }
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// 从字节缓冲中解析图片尺寸。数据不足时返回 (0, 0)。
        /// </summary>
        public static (int Width, int Height) Parse(byte[] data, int length)
        {
            if (data == null || length < 10)
            {
                return (0, 0);
            }

            // PNG
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                if (length < 24) return (0, 0);
                int w = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
                int h = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
                return (w, h);
            }

            // GIF
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            {
                int w = data[6] | (data[7] << 8);
                int h = data[8] | (data[9] << 8);
                return (w, h);
            }

            // BMP
            if (data[0] == 0x42 && data[1] == 0x4D && length >= 26)
            {
                int w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4));
                int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4)));
                return (w, h);
            }

            // WebP (RIFF....WEBP)
            if (length >= 30
                && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            {
                return ParseWebP(data, length);
            }

            // JPEG
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                return ParseJpeg(data, length);
            }

            return (0, 0);
        }

        private static (int Width, int Height) ParseJpeg(byte[] data, int length)
        {
            int i = 2;
            while (i + 8 < length)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                byte marker = data[i + 1];

                // 填充字节 / 无长度段
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                {
                    i += 2;
                    continue;
                }

                if (i + 3 >= length)
                {
                    break;
                }

                int segmentLength = (data[i + 2] << 8) | data[i + 3];
                if (segmentLength < 2)
                {
                    break;
                }

                // SOF0..SOF15，排除 DHT(C4) / JPG(C8) / DAC(CC)
                bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                    && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                if (isStartOfFrame)
                {
                    if (i + 8 >= length)
                    {
                        break;
                    }

                    int h = (data[i + 5] << 8) | data[i + 6];
                    int w = (data[i + 7] << 8) | data[i + 8];
                    return (w, h);
                }

                i += 2 + segmentLength;
            }

            return (0, 0);
        }

        private static (int Width, int Height) ParseWebP(byte[] data, int length)
        {
            string chunk = Encoding.ASCII.GetString(data, 12, 4);

            if (chunk == "VP8X" && length >= 30)
            {
                int w = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
                int h = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
                return (w, h);
            }

            if (chunk == "VP8 " && length >= 30 && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
            {
                int w = (data[26] | (data[27] << 8)) & 0x3FFF;
                int h = (data[28] | (data[29] << 8)) & 0x3FFF;
                return (w, h);
            }

            if (chunk == "VP8L" && length >= 25 && data[20] == 0x2F)
            {
                int bits = data[21] | (data[22] << 8) | (data[23] << 16) | (data[24] << 24);
                int w = (bits & 0x3FFF) + 1;
                int h = ((bits >> 14) & 0x3FFF) + 1;
                return (w, h);
            }

            return (0, 0);
        }
    }
}
