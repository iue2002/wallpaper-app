#pragma once

#include <guiddef.h>

// 本模块自己的 TAP 类标识。InitializeXamlDiagnosticsEx 会拿这个 CLSID 去
// 我们 DLL 的 DllGetClassObject 里要工厂，所以两边必须是同一个值。
// {7E2B9C41-3D8A-4F62-9B57-1C4E8D2A6F30}
static const GUID CLSID_XiaoKExplorerTap =
{ 0x7e2b9c41, 0x3d8a, 0x4f62, { 0x9b, 0x57, 0x1c, 0x4e, 0x8d, 0x2a, 0x6f, 0x30 } };
