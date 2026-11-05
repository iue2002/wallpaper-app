using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using App1.Config;
using App1.Models;

namespace App1.Services.Network
{
    /// <summary>
    /// 源请求闸门：统一控制「同时有几个源在请求」+「某个源坏了就先歇着」。
    ///
    /// 为什么要这个：
    ///   源从 8 个加到 11 个以后，如果全都同时发请求，会瞬间占满连接池和带宽，
    ///   结果不是更快，而是每个源都更慢（带宽是常量，抢的人越多每个人越少）。
    ///   所以并发数固定为一个预算值，多的排队，谁先完成谁先出图。
    ///
    ///   同时，某个源连续失败（比如 Seovx 证书坏、Paaa 返回空）会进入冷却，
    ///   冷却期内直接跳过，不再每次都等它超时。
    /// </summary>
    public static class SourceGate
    {
        private static readonly SemaphoreSlim Gate = new(
            AppConfig.MaxConcurrentSourceRequests,
            AppConfig.MaxConcurrentSourceRequests);

        private static readonly ConcurrentDictionary<string, int> ConsecutiveFailures = new();
        private static readonly ConcurrentDictionary<string, DateTime> CooldownUntil = new();

        /// <summary>当前是否处于冷却期</summary>
        public static bool IsCoolingDown(string key)
        {
            if (!CooldownUntil.TryGetValue(key, out var until)) return false;

            if (DateTime.Now < until) return true;

            // 冷却结束，清掉记录，给它一次重新试探的机会
            CooldownUntil.TryRemove(key, out _);
            ConsecutiveFailures[key] = 0;
            return false;
        }

        /// <summary>
        /// 在并发预算内执行一次源请求。
        /// 冷却中、超预算等待、异常，都会返回空列表，调用方不需要再写 try/catch。
        /// </summary>
        public static async Task<List<Wallpaper>> RunAsync(
            string key,
            Func<Task<List<Wallpaper>>> action,
            CancellationToken cancellationToken = default)
        {
            if (IsCoolingDown(key))
            {
                System.Diagnostics.Debug.WriteLine($"[源闸门] {key} 冷却中，跳过");
                return new List<Wallpaper>();
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await action().ConfigureAwait(false);

                // 只有「拿到东西」才算真正成功；空结果可能是暂时没有合格图，不清零也不累计
                if (result != null && result.Count > 0 && ConsecutiveFailures.TryGetValue(key, out var failures) && failures > 0)
                {
                    ConsecutiveFailures[key] = 0;
                }

                return result ?? new List<Wallpaper>();
            }
            catch (OperationCanceledException)
            {
                // 用户取消（切模式 / 关窗口）不算源故障
                return new List<Wallpaper>();
            }
            catch (Exception ex)
            {
                RegisterFailure(key, ex);
                return new List<Wallpaper>();
            }
            finally
            {
                Gate.Release();
            }
        }

        private static void RegisterFailure(string key, Exception ex)
        {
            var failures = ConsecutiveFailures.AddOrUpdate(key, 1, (_, current) => current + 1);
            System.Diagnostics.Debug.WriteLine($"[源闸门] {key} 失败 {failures} 次: {ex.GetType().Name} - {ex.Message}");

            if (failures >= AppConfig.SourceFailureThreshold)
            {
                var until = DateTime.Now.AddSeconds(AppConfig.SourceCooldownSeconds);
                CooldownUntil[key] = until;
                ConsecutiveFailures[key] = 0;
                System.Diagnostics.Debug.WriteLine(
                    $"[源闸门] {key} 连续失败达 {AppConfig.SourceFailureThreshold} 次，冷却 {AppConfig.SourceCooldownSeconds} 秒");
            }
        }
    }
}
