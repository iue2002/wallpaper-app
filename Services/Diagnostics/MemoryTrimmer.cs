using System;
using System.Runtime.InteropServices;

namespace App1.Services.Diagnostics
{
    /// <summary>
    /// 主动回收内存。
    ///
    /// 用途只有一个：主窗口关闭、界面全部销毁之后，让进程真正瘦回去。
    /// 平时不要调 —— 强制 GC 会打断 JIT 和缓存预热，属于用性能换数字的操作。
    ///
    /// 两步缺一不可：
    ///   1. GC.Collect + WaitForPendingFinalizers：托管堆里的 Window / XAML 包装对象
    ///      要跑完终结器才会释放对应的 COM 对象。只 Collect 不等终结器，native 侧还挂着。
    ///   2. EmptyWorkingSet：GC 释放的内存会退回进程堆，但 Windows 不一定马上把
    ///      物理页交还，任务管理器里看到的仍是高峰值。把工作集换出一次，
    ///      数字才会立刻降下来。
    /// </summary>
    internal static class MemoryTrimmer
    {
        // 关窗口、换壁纸后都可能触发，撞在一起时只让一个跑：
        // 两个阻塞式全量 GC 并行没有任何收益，只会把线程池占住。
        private static int _running;

        /// <summary>
        /// 回收一次。blocking 的强制 GC + 工作集修剪。
        /// </summary>
        /// <param name="reason">只用于日志，方便对照是哪一步之后降下来的。</param>
        public static void Compact(string reason)
        {
            if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1)
            {
                AppLog.Write($"[内存] 已有回收在进行，跳过({reason})");
                return;
            }

            try
            {
                CompactCore(reason);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _running, 0);
            }
        }

        private static void CompactCore(string reason)
        {
            var before = GC.GetTotalMemory(forceFullCollection: false);

            try
            {
                // 大对象堆装的是大数组/大字符串，位图本身在 native 侧，
                // 但图片流、解码缓冲常常落在 LOH 上，压一次有意义。
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[内存] GC 失败({reason}): {ex.Message}");
            }

            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                EmptyWorkingSet(process.Handle);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[内存] 修剪工作集失败({reason}): {ex.Message}");
            }

            var after = GC.GetTotalMemory(forceFullCollection: false);
            AppLog.Write($"[内存] 已回收({reason}) 托管堆 {before / 1024 / 1024}MB → {after / 1024 / 1024}MB");
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
    }
}
