using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;
using App1.Services.Diagnostics;

namespace App1.Services.Network
{
    /// <summary>源失败的类型。冷却时长按类型决定，不搞一刀切。</summary>
    public enum SourceFailureKind
    {
        Unknown = 0,
        RateLimited,    // 429，被限流
        Unauthorized,   // 401/403，key 失效或被拒
        ServerError,    // 5xx，对面挂了
        Timeout,        // 超时
        Network,        // 连不上、DNS、断网
    }

    public readonly record struct SourceStatus(
        string Key,
        int ConsecutiveFailures,
        DateTime? CoolingUntil,
        string? LastError);

    /// <summary>
    /// 源闸门：并发预算 + 熔断。
    ///
    /// 干两件事：
    ///   1. 同一时刻最多几个源在请求。源有十几个，全放出去只会互相抢带宽，谁都不快。
    ///   2. 某个源坏了先歇着。但「坏了」要分类型：
    ///      被限流还按几十秒去重试，等于继续挨罚；key 失效了，重试一万次也没用。
    ///
    /// 注意：判定失败靠各源在 catch 里主动 ReportFailure。
    /// 之前那版只看 RunAsync 有没有捕到异常，可各源内部都把异常吞了、返回空列表，
    /// 失败计数永远攒不起来 —— 熔断等于没接上。
    /// </summary>
    public static class SourceGate
    {
        private static readonly SemaphoreSlim Gate = new(
            AppConfig.MaxConcurrentSourceRequests,
            AppConfig.MaxConcurrentSourceRequests);

        private static readonly ConcurrentDictionary<string, Health> Healths = new();

        private sealed class Health
        {
            public int Failures;
            public int CircuitOpens;
            public DateTime CoolingUntil = DateTime.MinValue;
            public string? LastError;
        }

        /// <summary>当前是否处于冷却期。</summary>
        public static bool IsCoolingDown(string key)
        {
            if (!Healths.TryGetValue(key, out var health))
            {
                return false;
            }

            lock (health)
            {
                return DateTime.Now < health.CoolingUntil;
            }
        }

        /// <summary>给日志和界面看的快照。</summary>
        public static List<SourceStatus> Snapshot()
        {
            var list = new List<SourceStatus>();
            foreach (var pair in Healths)
            {
                lock (pair.Value)
                {
                    list.Add(new SourceStatus(
                        pair.Key,
                        pair.Value.Failures,
                        pair.Value.CoolingUntil > DateTime.Now ? pair.Value.CoolingUntil : null,
                        pair.Value.LastError));
                }
            }

            return list;
        }

        /// <summary>
        /// 在并发预算内跑一次源请求。冷却中、超预算、出错都返回空列表，
        /// 调用方不用自己写 try/catch。
        /// </summary>
        public static async Task<List<Wallpaper>> RunAsync(
            string key,
            Func<Task<List<Wallpaper>>> action,
            CancellationToken cancellationToken = default)
        {
            if (IsCoolingDown(key))
            {
                AppLog.Write($"[源闸门] {key} 冷却中，跳过");
                return new List<Wallpaper>();
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await action().ConfigureAwait(false);

                // 拿到东西就算这次通了：清零，重新给它信用
                if (result != null && result.Count > 0)
                {
                    NoteSuccess(key);
                }

                return result ?? new List<Wallpaper>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户取消（切模式 / 关窗口）不算源故障
                return new List<Wallpaper>();
            }
            catch (OperationCanceledException)
            {
                // 没被取消却抛取消 = HttpClient 超时。
                // 以前这里和「用户取消」混在一起，卡住的源永远进不了冷却，
                // 每次都要再陪它等一遍超时。
                ReportFailure(key, SourceFailureKind.Timeout, "请求超时");
                return new List<Wallpaper>();
            }
            catch (Exception ex)
            {
                ReportFailure(key, ex);
                return new List<Wallpaper>();
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>源内部 catch 到异常时调用（它们不往外抛）。</summary>
        public static void ReportFailure(string key, Exception ex)
        {
            ReportFailure(key, Classify(ex), ex.Message);
        }

        /// <summary>源明确拿到数据时调用，把累计的失败清掉。</summary>
        public static void ReportSuccess(string key)
        {
            NoteSuccess(key);
        }

        private static void NoteSuccess(string key)
        {
            if (!Healths.TryGetValue(key, out var health))
            {
                return;
            }

            lock (health)
            {
                health.Failures = 0;
                health.CircuitOpens = 0;
                health.CoolingUntil = DateTime.MinValue;
                health.LastError = null;
            }
        }

        private static void ReportFailure(string key, SourceFailureKind kind, string message)
        {
            var health = Healths.GetOrAdd(key, _ => new Health());

            TimeSpan cooldown;
            int failures;

            lock (health)
            {
                health.Failures++;
                health.LastError = $"{kind}: {message}";
                failures = health.Failures;

                if (failures < AppConfig.SourceFailureThreshold)
                {
                    AppLog.Write($"[源闸门] {key} 失败({kind}) {failures}/{AppConfig.SourceFailureThreshold}：{message}");
                    return;
                }

                health.Failures = 0;
                health.CircuitOpens++;

                cooldown = CooldownFor(kind, health.CircuitOpens);
                health.CoolingUntil = DateTime.Now.Add(cooldown);
            }

            AppLog.Write(
                $"[源闸门] {key} 连续失败 {AppConfig.SourceFailureThreshold} 次({kind})，" +
                $"冷却 {(int)cooldown.TotalSeconds} 秒：{message}");
        }

        /// <summary>
        /// 按失败类型定冷却时长，再用连续熔断次数做指数退避（有上限）。
        /// 被限流和 key 失效都不能按秒级重试，那只会让情况更糟。
        /// </summary>
        private static TimeSpan CooldownFor(SourceFailureKind kind, int circuitOpens)
        {
            var baseSeconds = kind switch
            {
                SourceFailureKind.RateLimited => 600,     // 10 分钟起步
                SourceFailureKind.Unauthorized => 3600,   // 一小时，等用户换 key
                SourceFailureKind.ServerError => 120,
                SourceFailureKind.Timeout => 45,
                SourceFailureKind.Network => 60,
                _ => Math.Max(30, AppConfig.SourceCooldownSeconds),
            };

            var cap = kind switch
            {
                SourceFailureKind.RateLimited => 3600,
                SourceFailureKind.Unauthorized => 3600,
                SourceFailureKind.ServerError => 1800,
                _ => 600,
            };

            // 2^(n-1)，最多翻 4 次
            var shift = Math.Min(Math.Max(circuitOpens - 1, 0), 4);
            var seconds = (long)baseSeconds << shift;

            return TimeSpan.FromSeconds(Math.Min(seconds, cap));
        }

        private static SourceFailureKind Classify(Exception ex)
        {
            if (ex is HttpRequestException http)
            {
                if (http.StatusCode is { } code)
                {
                    var value = (int)code;
                    if (value == 429)
                    {
                        return SourceFailureKind.RateLimited;
                    }

                    if (value == 401 || value == 403)
                    {
                        return SourceFailureKind.Unauthorized;
                    }

                    if (value >= 500)
                    {
                        return SourceFailureKind.ServerError;
                    }
                }

                return http.StatusCode == HttpStatusCode.Unauthorized ||
                       http.StatusCode == HttpStatusCode.Forbidden
                    ? SourceFailureKind.Unauthorized
                    : SourceFailureKind.Network;
            }

            if (ex is TaskCanceledException || ex is TimeoutException)
            {
                return SourceFailureKind.Timeout;
            }

            if (ex is SocketException || ex.InnerException is SocketException)
            {
                return SourceFailureKind.Network;
            }

            return SourceFailureKind.Unknown;
        }
    }
}
