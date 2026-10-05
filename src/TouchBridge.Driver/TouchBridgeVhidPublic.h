/*++
TouchBridgeVhidPublic.h —— 驱动与用户态（TouchBridge.Receiver）共享的接口定义。

用户态用法：
    HANDLE h = CreateFile(L"\\\\.\\TouchBridgeVhid", GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    TB_VHID_FRAME frame = {0};
    frame.Count = 1;
    frame.Contacts[0].Flags        = TB_VHID_FLAG_TIP | TB_VHID_FLAG_INRANGE;
    frame.Contacts[0].X            = ...;   // 0..32767，归一化（= 像素 / 屏宽 * 32767）
    frame.Contacts[0].Y            = ...;   // 0..32767
    frame.Contacts[0].WidthMm100   = 9000;  // 90.00 mm
    frame.Contacts[0].HeightMm100  = 8000;
    frame.Contacts[0].Pressure     = 512;   // 0..1024
    DeviceIoControl(h, IOCTL_TB_VHID_SUBMIT, &frame, sizeof(frame), NULL, 0, &n, NULL);

抬手：frame.Count = 0 再发一次（所有槽位 Tip 关闭 → Windows 认为已抬手）。
--*/

#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#define TB_VHID_NT_NAME       L"\\Device\\TouchBridgeVhid"
#define TB_VHID_DOS_NAME      L"\\DosDevices\\TouchBridgeVhid"
#define TB_VHID_USER_NAME     L"\\\\.\\TouchBridgeVhid"

#define TB_VHID_MAX_CONTACTS  10      // 与报告描述符里的手指集合数一致
// Report ID(1) + 每指 12 字节 + Scan Time(2) + Contact Count(1)
#define TB_VHID_REPORT_BYTES  (1 + TB_VHID_MAX_CONTACTS * 12 + 3)

#define TB_VHID_FLAG_TIP      0x1     // Tip Switch
#define TB_VHID_FLAG_INRANGE  0x2     // In Range

typedef struct _TB_VHID_CONTACT {
    ULONG Flags;                 // TB_VHID_FLAG_*
    ULONG Id;                    // 预留：上报方给的手指编号（驱动内部按槽位号上报）
    ULONG X;                     // 0..32767（归一化）
    ULONG Y;                     // 0..32767（归一化）
    ULONG WidthMm100;            // 接触宽，单位 0.01 mm（例：9000 = 90.00 mm）
    ULONG HeightMm100;           // 接触高，单位 0.01 mm
    ULONG Pressure;              // 0..1024
} TB_VHID_CONTACT, *PTB_VHID_CONTACT;

typedef struct _TB_VHID_FRAME {
    ULONG           Count;                              // 触点个数（0 = 抬手）
    TB_VHID_CONTACT Contacts[TB_VHID_MAX_CONTACTS];
} TB_VHID_FRAME, *PTB_VHID_FRAME;

// 诊断信息：驱动把内部状态回填到 IOCTL 的输出缓冲（可选），供接收端打日志定位。
typedef struct _TB_VHID_DIAG {
    ULONG SubmitAttempts;   // 调用 VhfReadReportSubmit 的次数
    ULONG SubmitFailures;   // 其中失败的次数
    ULONG ReadyCallbacks;   // EvtVhfReadyForNextReadReport 被调用的次数
    ULONG FeatureRequests;  // 主机读 Feature 报告（Contact Count Maximum）的次数
    LONG  LastStatus;       // 最近一次 VhfReadReportSubmit 的 NTSTATUS（0 = STATUS_SUCCESS）
} TB_VHID_DIAG, *PTB_VHID_DIAG;

#define IOCTL_TB_VHID_SUBMIT \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_WRITE_DATA)

#ifdef __cplusplus
}
#endif