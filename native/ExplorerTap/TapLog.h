#pragma once

// 极简日志：只在需要排查时开（共享内存里 DebugLog=1）。
// 注入到 explorer 之后没有调试器可用，日志是唯一的证据来源。
void TapLogEnable(bool enable);
void TapLog(const wchar_t* format, ...);
