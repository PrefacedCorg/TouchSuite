/*++
TouchBridgeVhid.c —— TouchBridge 的虚拟触摸屏驱动（KMDF + VHF）

架构（与 Uu 远程 的 uuycinput.sys 同类）：
    TouchBridge.Receiver(用户态) --IOCTL--> 本驱动 --VhfReadReportSubmit--> HID 栈
                                                                             │
                              Windows 认成「HID 标准触摸屏」→ WM_POINTER / WPF 触摸
                              同时 OpenInput(原始HID) 能直接读到我们定义的报告：
                              W/H 按 0.01 mm 声明，压感 0..1024 —— 面积/压感都是真值。

报告描述符要点：
    单个顶层集合 Touch Screen(0x0D:0x04)，带 Report ID(1)，10 个并行槽位；报告共 144 字节（含 ID 字节）。
    字段按 HID 规范「按位顺排」（不是每槽 14 字节对齐）：
        偏移 0   : Contact Identifier  10 × 8 bit
        偏移 80  : Tip Switch          10 × 1 bit
        偏移 90  : In Range            10 × 1 bit
        偏移 100 : Confidence          10 × 1 bit
        偏移 110 : 填充                10 × 5 bit
        偏移 160 : X                   10 × 16 bit
        偏移 320 : Y                   10 × 16 bit
        偏移 480 : Width               10 × 16 bit
        偏移 640 : Height              10 × 16 bit
        偏移 800 : Azimuth             10 × 16 bit   ← 0..359 度
        偏移 960 : Tip Pressure        10 × 16 bit
        偏移 1120: Scan Time            1 × 16 bit   ← 报告级（可选）
        偏移 1136: Contact Count        1 × 8 bit    ← 报告级（必需！缺它 Windows 不认触摸屏）
    （以上偏移均不含开头那个 Report ID 字节；落在缓冲里时整体后移 1 字节）
    X/Y 逻辑量程 0..32767（归一化）；Width/Height 单位 = 厘米×10^-3 = 0.01 mm；Azimuth 单位 = 度。

    注意：Contact Count(0x0D:0x54) 是微软规定的报告级「必需用法」，且由 Windows 主机严格执行；
    "Any device that does not report all mandatory usages ... will be non-functional as a
     Windows Touchscreen device." —— 只用 Feature 的 Contact Count Maximum(0x55) 是不够的，
     输入报告里必须真的带一个 Contact Count 字段。
--*/

#include <ntddk.h>
#include <wdf.h>
#include <vhf.h>

#include "TouchBridgeVhidPublic.h"

#define TB_VHID_VENDOR_ID   0x1234
#define TB_VHID_PRODUCT_ID  0x0001
#define TB_VHID_VERSION     0x0100

// ------------------------------------------------------------------ 报告描述符
//
// 每个触点是一个「Finger」逻辑集合（0x0D:0x22），各声明：
//   Contact Identifier(8b) + TipSwitch(1b) + InRange(1b) + Confidence(1b) + 填充(5b)
//   + X(16b) + Y(16b) + Width(16b) + Height(16b) + Azimuth(16b) + TipPressure(16b)
// 全部按位顺排后，每根手指恰好 14 字节（含位填充后自然字节对齐）。
// 整个应用程序集合带 Report ID(1)，报告 = [0x01][指0..指9][ScanTime 2B][ContactCount 1B]。
// 这是 Windows HID 触摸驱动的通用（推荐）写法。

#define TB_FINGER_COUNT 10
#define TB_FINGER_BYTES 14

// "空闲"判定阈值：距上一帧超过这么久，就认为设备停止过上报，
// 下一帧按 Scan Time 规范重新作为基准（从 0 起算）。50 ms ≈ 3~6 个正常帧间隔。
#define TB_IDLE_GAP_100NS  (50ULL * 10000ULL)

// 待投递报告的小队列深度。
// 关键：**抬手帧绝不能被后续帧覆盖** —— 若主机还没读走"抬手"那一帧，用户就已经按下第二笔，
// 覆盖掉抬手帧会让主机看不到"断开"，Windows 便把两笔当成同一个连续触点（批注软件里表现为
// 两笔之间连一条线）。所以按顺序排小队，主机每来读一次就取队首送一帧。
#define TB_PENDING_MAX 4

// 报告缓冲内的字节偏移（含开头的 Report ID 字节）
#define TB_SCAN_TIME_OFFSET     (1 + TB_FINGER_COUNT * TB_FINGER_BYTES)       // Scan Time，2 字节
#define TB_CONTACT_COUNT_OFFSET (TB_SCAN_TIME_OFFSET + 2)                     // Contact Count，1 字节

static const UCHAR g_FingerBlock[] =
{
    0x09, 0x22,                     //   Usage (Finger)
    0xA1, 0x02,                     //   Collection (Logical)
    0x09, 0x51,                     //     Usage (Contact Identifier)
    0x15, 0x00,                     //     Logical Minimum (0)
    0x25, 0x09,                     //     Logical Maximum (9)
    0x75, 0x08,                     //     Report Size (8)
    0x95, 0x01,                     //     Report Count (1)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x09, 0x42,                     //     Usage (Tip Switch)
    0x15, 0x00,                     //     Logical Minimum (0)
    0x25, 0x01,                     //     Logical Maximum (1)
    0x75, 0x01,                     //     Report Size (1)
    0x95, 0x01,                     //     Report Count (1)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x09, 0x32,                     //     Usage (In Range)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x09, 0x47,                     //     Usage (Confidence)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x75, 0x05,                     //     Report Size (5)
    0x95, 0x01,                     //     Report Count (1)
    0x81, 0x03,                     //     Input (Const) ← 填充，使本手指凑满整字节
    // 每指块开头复位全局状态：否则第 2 指起 X/Y 会继承上一指 TipPressure 的
    // 物理量程(0..1024)与量纲，能力表/校验工具里看着会乱（X/Y 本就无量纲）。
    0x65, 0x00,                     //     Unit (None)
    0x55, 0x00,                     //     Unit Exponent (0)
    0x36, 0x00, 0x00,               //     Physical Minimum (0)
    0x46, 0x00, 0x00,               //     Physical Maximum (0)
    0x05, 0x01,                     //     Usage Page (Generic Desktop)
    0x09, 0x30,                     //     Usage (X)
    0x09, 0x31,                     //     Usage (Y)
    0x16, 0x00, 0x00,               //     Logical Minimum (0)
    0x26, 0xFF, 0x7F,               //     Logical Maximum (32767)
    0x75, 0x10,                     //     Report Size (16)
    0x95, 0x02,                     //     Report Count (2)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x05, 0x0D,                     //     Usage Page (Digitizers)
    0x09, 0x48,                     //     Usage (Width)
    0x09, 0x49,                     //     Usage (Height)
    0x65, 0x11,                     //     Unit (SI Linear: centimeter)
    0x55, 0x0D,                     //     Unit Exponent (-3) → 10^-3 cm = 0.01 mm
    0x36, 0x00, 0x00,               //     Physical Minimum (0)
    0x46, 0xFF, 0x7F,               //     Physical Maximum (32767)
    0x16, 0x00, 0x00,               //     Logical Minimum (0)
    0x26, 0xFF, 0x7F,               //     Logical Maximum (32767)
    0x75, 0x10,                     //     Report Size (16)
    0x95, 0x02,                     //     Report Count (2)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x05, 0x0D,                     //     Usage Page (Digitizers)
    0x65, 0x00,                     //     Unit (None)      ← 角度：HID 无法精确表达"度"，按惯例留 None
    0x55, 0x00,                     //     Unit Exponent (0)
    0x09, 0x3F,                     //     Usage (Azimuth)  接触朝向，绕 Z 轴逆时针，0..359 度
    0x15, 0x00,                     //     Logical Minimum (0)
    0x26, 0x67, 0x01,               //     Logical Maximum (359)
    0x36, 0x00, 0x00,               //     Physical Minimum (0)
    0x46, 0x68, 0x01,               //     Physical Maximum (360)   ← 微软 HID 校验要求物理范围 0..360 度
    0x75, 0x10,                     //     Report Size (16)
    0x95, 0x01,                     //     Report Count (1)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0x05, 0x0D,                     //     Usage Page (Digitizers)
    0x65, 0x00,                     //     Unit (None)
    0x55, 0x00,                     //     Unit Exponent (0)
    0x36, 0x00, 0x00,               //     Physical Minimum (0)
    0x46, 0x00, 0x04,               //     Physical Maximum (1024)
    0x09, 0x30,                     //     Usage (Tip Pressure)
    0x15, 0x00,                     //     Logical Minimum (0)
    0x26, 0x00, 0x04,               //     Logical Maximum (1024)
    0x75, 0x10,                     //     Report Size (16)
    0x95, 0x01,                     //     Report Count (1)
    0x81, 0x02,                     //     Input (Data,Var,Abs)
    0xC0                            //   End Collection (Logical)
};

static UCHAR g_ReportDescriptor[2048];
static ULONG g_ReportDescriptorLength;

// 组装完整描述符：头 + 10 × 手指块 + 尾
static VOID TbBuildDescriptor(VOID)
{
    static const UCHAR header[] =
    {
        0x05, 0x0D,                 // Usage Page (Digitizers)
        0x09, 0x04,                 // Usage (Touch Screen)
        0xA1, 0x01,                 // Collection (Application)
        0x85, 0x01,                 //   Report ID (1)

        // 最大同时触点数（Feature）——告知系统本设备支持 10 点
        0x05, 0x0D,                 //   Usage Page (Digitizers)
        0x09, 0x55,                 //   Usage (Contact Count Maximum)
        0x15, 0x00,                 //   Logical Minimum (0)
        0x25, 0x0A,                 //   Logical Maximum (10)
        0x75, 0x08,                 //   Report Size (8)
        0x95, 0x01,                 //   Report Count (1)
        0xB1, 0x02,                 //   Feature (Data,Var,Abs)
    };

    ULONG n = 0;
    int i;

    if (g_ReportDescriptorLength > 0)
        return;                                     // 已构建

    RtlCopyMemory(g_ReportDescriptor + n, header, sizeof(header));
    n += sizeof(header);

    for (i = 0; i < TB_FINGER_COUNT; i++)
    {
        RtlCopyMemory(g_ReportDescriptor + n, g_FingerBlock, sizeof(g_FingerBlock));
        n += sizeof(g_FingerBlock);
    }

    // 报告级用法（在手指集合之外、Application 集合之内）。
    // 顺序 = 报告里的字段顺序：先 Scan Time(16b)，再 Contact Count(8b)。
    {
        static const UCHAR reportLevel[] =
        {
            // Scan Time（0x0D:0x56）—— 每帧相对扫描时间，单位 100 µs（可选，但官方样例有）
            0x05, 0x0D,                 // Usage Page (Digitizers)
            0x55, 0x0C,                 //   Unit Exponent (-4)
            0x66, 0x01, 0x10,           //   Unit (Seconds)
            0x47, 0xFF, 0xFF, 0x00, 0x00,   //   Physical Maximum (65535)
            0x27, 0xFF, 0xFF, 0x00, 0x00,   //   Logical Maximum (65535)
            0x75, 0x10,                 //   Report Size (16)
            0x95, 0x01,                 //   Report Count (1)
            0x09, 0x56,                 //   Usage (Scan Time)
            0x81, 0x02,                 //   Input (Data,Var,Abs)

            // Contact Count（0x0D:0x54）—— 本报告里的触点总数（报告级【必需】用法）
            0x09, 0x54,                 //   Usage (Contact Count)
            0x15, 0x00,                 //   Logical Minimum (0)
            0x25, 0x0A,                 //   Logical Maximum (10)
            0x75, 0x08,                 //   Report Size (8)
            0x95, 0x01,                 //   Report Count (1)
            0x81, 0x02,                 //   Input (Data,Var,Abs)
        };
        RtlCopyMemory(g_ReportDescriptor + n, reportLevel, sizeof(reportLevel));
        n += sizeof(reportLevel);
    }

    g_ReportDescriptor[n++] = 0xC0;                 // End Collection (Application)
    g_ReportDescriptorLength = n;
}

// ------------------------------------------------------------------ 设备上下文
// 每个槽位"最后一次 move"的数据。抬手帧必须沿用它的坐标 ——
// 微软《Button state transitions》明确要求：
//   手指离开表面那一包里的 X/Y，必须与最后一次 move 包相同
//   （悬停设备还要求 out-of-range 包也沿用相同坐标）。
// 若抬手帧把坐标清零，(0,0) 会被笔迹引擎当成笔迹的落点，
// 表现为"上一笔抬起处与下一笔按下处被连成一条线"（各种批注软件都会中招）。
typedef struct _TB_SLOT
{
    TB_VHID_CONTACT Data;       // 该槽位最后一次上报的完整数据
    BOOLEAN         HasData;    // 是否曾经有过数据（决定是否需要输出"沿用坐标"）
} TB_SLOT;

typedef struct _TB_DEVICE_CONTEXT
{
    VHFHANDLE  Vhf;
    KSPIN_LOCK Lock;
    BOOLEAN    Ready;        // 系统已消化上一份报告，可以再提交
    UCHAR      Latest[TB_VHID_REPORT_BYTES];
    // 待投递队列（保证顺序：抬手帧不会被覆盖丢掉）
    UCHAR      Pending[TB_PENDING_MAX][TB_VHID_REPORT_BYTES];
    ULONG      PendingCount;
    volatile LONG SubmitAttempts;   // 诊断：VhfReadReportSubmit 调用次数
    volatile LONG SubmitFailures;   // 诊断：VhfReadReportSubmit 失败次数
    volatile LONG ReadyCallbacks;   // 诊断：EvtVhfReadyForNextReadReport 调用次数
    volatile LONG LastSubmitStatus; // 诊断：最近一次 VhfReadReportSubmit 的 NTSTATUS
    ULONG64 ScanBase100ns;          // Scan Time 基准时刻；0 = 尚未起算（下一帧为基准）
    ULONG64 LastFrame100ns;         // 上一帧提交时刻（判断"空闲"用）
    volatile LONG FeatureRequests;  // 诊断：主机读取 Feature 报告的次数
    TB_SLOT         Slots[TB_VHID_MAX_CONTACTS];   // 各槽位最后一次 move 的数据（抬手帧沿用其坐标）
} TB_DEVICE_CONTEXT, *PTB_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(TB_DEVICE_CONTEXT, TbGetContext)

// ------------------------------------------------------------------ 声明
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD                 TbEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP            TbEvtDeviceCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL        TbEvtIoDeviceControl;
EVT_VHF_READY_FOR_NEXT_READ_REPORT        TbEvtVhfReadyForNextReadReport;
EVT_VHF_ASYNC_OPERATION                   TbEvtVhfGetFeature;

// ------------------------------------------------------------------ 小工具
static ULONG TbMin(_In_ ULONG a, _In_ ULONG b)
{
    return (a < b) ? a : b;
}

static VOID TbPut16(_Out_writes_bytes_(2) UCHAR* p, _In_ ULONG v)
{
    p[0] = (UCHAR)(v & 0xFF);
    p[1] = (UCHAR)((v >> 8) & 0xFF);
}

// 把用户态的一帧触点编成 HID 报告。
// 布局：[0]=Report ID(1)，随后 10 个手指块（各 14 字节），最后 ScanTime(2B) + ContactCount(1B)。
// 注意：**槽位数据不再受 Count 限制** —— 未按下的槽位由调用方（TbQueueFrame）填入
// "最后一次 move 的坐标 + Tip/InRange=0"，这是微软对抬手帧的硬性要求（见 TB_SLOT 注释）。
static VOID TbBuildReport(
    _In_ const TB_VHID_FRAME* frame,
    _In_ ULONG scanTime,
    _Out_writes_bytes_(TB_VHID_REPORT_BYTES) UCHAR* report)
{
    ULONG i;

    RtlZeroMemory(report, TB_VHID_REPORT_BYTES);
    report[0] = 0x01;                                      // Report ID

    for (i = 0; i < TB_FINGER_COUNT; i++)
    {
        UCHAR* f = report + 1 + (i * TB_FINGER_BYTES);
        const TB_VHID_CONTACT* c = &frame->Contacts[i];
        UCHAR bits = 0x00;

        // Confidence 只在"真实按下"时为 1（抬手后沿用的坐标不算接触）
        if (c->Flags & TB_VHID_FLAG_TIP)
        {
            bits = 0x05;                                   // Tip + Confidence
            if (c->Flags & TB_VHID_FLAG_INRANGE)
                bits |= 0x02;                              // InRange（与 Tip 一起置位，见非悬停设备要求）
        }

        f[0] = (UCHAR)i;                                   // Contact Identifier（槽位固定）
        f[1] = bits;                                       // TipSwitch/InRange/Confidence(+填充)
        TbPut16(f + 2,  TbMin(c->X, 32767));
        TbPut16(f + 4,  TbMin(c->Y, 32767));
        TbPut16(f + 6,  TbMin(c->WidthMm100, 32767));
        TbPut16(f + 8,  TbMin(c->HeightMm100, 32767));
        TbPut16(f + 10, TbMin(c->AzimuthDeg, 359));        // Azimuth（0..359 度）
        TbPut16(f + 12, TbMin(c->Pressure, 1024));
    }

    // ---- 报告级字段（必须在所有手指字段之后）----
    // Scan Time：每帧相对扫描时间，单位 100 µs（这里用单调递增的帧计数近似）
    TbPut16(report + TB_SCAN_TIME_OFFSET, scanTime & 0xFFFF);
    // Contact Count：本帧【真实按下】的触点总数（报告级【必需】用法，缺它 Windows 不认触摸屏）
    report[TB_CONTACT_COUNT_OFFSET] = (UCHAR)TbMin(frame->Count, TB_FINGER_COUNT);
}

// 有报告待发且系统就绪时就提交；调用方不必持有锁
static VOID TbTrySubmit(_In_ PTB_DEVICE_CONTEXT ctx)
{
    UCHAR   report[TB_VHID_REPORT_BYTES];
    KIRQL   oldIrql;
    BOOLEAN submit = FALSE;
    ULONG   i;

    KeAcquireSpinLock(&ctx->Lock, &oldIrql);
    if (ctx->Vhf != NULL && ctx->Ready && ctx->PendingCount > 0)
    {
        RtlCopyMemory(report, ctx->Pending[0], sizeof(report));      // 取队首（最旧的一帧，顺序保证）
        for (i = 1; i < ctx->PendingCount; i++)                      // 整体前移出队
            RtlCopyMemory(ctx->Pending[i - 1], ctx->Pending[i], sizeof(report));
        ctx->PendingCount--;
        ctx->Ready      = FALSE;         // 等 EvtVhfReadyForNextReadReport 再放开
        submit = TRUE;
    }
    KeReleaseSpinLock(&ctx->Lock, oldIrql);

    if (submit)
    {
        HID_XFER_PACKET packet;
        NTSTATUS status;

        RtlZeroMemory(&packet, sizeof(packet));
        packet.reportBuffer    = report;
        packet.reportBufferLen = sizeof(report);
        packet.reportId        = 0x01;   // 必须与 report[0] 里的 Report ID 一致（描述符声明了 Report ID 1）

        InterlockedIncrement(&ctx->SubmitAttempts);
        status = VhfReadReportSubmit(ctx->Vhf, &packet);
        InterlockedExchange(&ctx->LastSubmitStatus, (LONG)status);

        if (!NT_SUCCESS(status))
        {
            InterlockedIncrement(&ctx->SubmitFailures);
            KeAcquireSpinLock(&ctx->Lock, &oldIrql);
            ctx->Ready = TRUE;           // 这帧丢了，等下一帧
            KeReleaseSpinLock(&ctx->Lock, oldIrql);
        }
    }
}

static VOID TbQueueFrame(_In_ PTB_DEVICE_CONTEXT ctx, _In_ const TB_VHID_FRAME* frame)
{
    KIRQL oldIrql;
    TB_VHID_FRAME merged;        // 合并后的帧（324B，栈上）
    ULONG i, pressed = 0;

    KeAcquireSpinLock(&ctx->Lock, &oldIrql);

    // 逐槽位合并：按下的照抄并记住；**抬手/未使用的槽位沿用最后一次 move 的坐标**（只清 Tip/InRange）——
    // 微软要求抬笔包的 X/Y 必须与最后一次 move 相同，否则 (0,0) 会让笔迹引擎把两笔连成一条线。
    for (i = 0; i < TB_VHID_MAX_CONTACTS; i++)
    {
        TB_VHID_CONTACT* out = &merged.Contacts[i];

        if (i < frame->Count && (frame->Contacts[i].Flags & TB_VHID_FLAG_TIP))
        {
            *out = frame->Contacts[i];
            ctx->Slots[i].Data    = *out;      // 记下最后一次 move 的数据
            ctx->Slots[i].HasData = TRUE;
            pressed++;
        }
        else if (ctx->Slots[i].HasData)
        {
            *out = ctx->Slots[i].Data;         // 坐标沿用，Tip/InRange 清 0
            out->Flags = 0;
        }
        else
        {
            RtlZeroMemory(out, sizeof(*out));
        }
    }
    merged.Count = pressed;                    // ContactCount 只算真实按下的触点

    // Scan Time（0x0D:0x56）—— 严格按 USB-IF HUTRR83 / MS《Supported usages in digitizer
    // report descriptors》实现：
    //   · 单位固定 100 µs（描述符里已按 10^-4 s 声明），**增量必须反映扫描频率**，
    //     即按真实经过时间递增（不是"每帧 +1"）；
    //   · "一段空闲之后重新开始上报数据的第一帧"作为**基准**（该帧的值 ≈ 0），
    //     所以停顿后再画一笔会从 0 重新起算；帧不断则连续累加；
    //   · 同一帧内所有触点共享同一个值；16 位字段自然回绕（规范允许，不必重置）。
    {
        ULONG64 now100ns = KeQueryInterruptTime();
        if (ctx->ScanBase100ns == 0
            || (now100ns - ctx->LastFrame100ns) > TB_IDLE_GAP_100NS)   // 距上一帧超过 TB_IDLE_GAP 视为空闲
        {
            ctx->ScanBase100ns = now100ns;      // 以本帧为基准 → 本帧值 ≈ 0
        }
        ctx->LastFrame100ns = now100ns;

        ULONG scanTime = (ULONG)((now100ns - ctx->ScanBase100ns) / 1000ULL);   // 100ns → 100µs
        TbBuildReport(&merged, scanTime, ctx->Latest);
    }

    // 入队（不覆盖未投递的帧）：主机每读一次取队首送一帧；队满才丢最旧的一帧
    if (ctx->PendingCount < TB_PENDING_MAX)
    {
        RtlCopyMemory(ctx->Pending[ctx->PendingCount], ctx->Latest, TB_VHID_REPORT_BYTES);
        ctx->PendingCount++;
    }
    else
    {
        for (i = 1; i < TB_PENDING_MAX; i++)
            RtlCopyMemory(ctx->Pending[i - 1], ctx->Pending[i], TB_VHID_REPORT_BYTES);
        RtlCopyMemory(ctx->Pending[TB_PENDING_MAX - 1], ctx->Latest, TB_VHID_REPORT_BYTES);
    }

    KeReleaseSpinLock(&ctx->Lock, oldIrql);

    TbTrySubmit(ctx);
}

// ------------------------------------------------------------------ VHF 回调
VOID TbEvtVhfReadyForNextReadReport(_In_ PVOID VhfClientContext)
{
    PTB_DEVICE_CONTEXT ctx = (PTB_DEVICE_CONTEXT)VhfClientContext;
    KIRQL oldIrql;

    InterlockedIncrement(&ctx->ReadyCallbacks);

    KeAcquireSpinLock(&ctx->Lock, &oldIrql);
    ctx->Ready = TRUE;
    KeReleaseSpinLock(&ctx->Lock, oldIrql);

    TbTrySubmit(ctx);
}

// 主机（HID 类驱动 / 触摸 Digitizer）读 Feature 报告时被调用。
// 本设备描述符里只声明了一个 Feature 报告：Report ID(1) = Contact Count Maximum(0x0D:0x55)。
// 若不实现该回调，VHF 会直接以 STATUS_NOT_SUPPORTED 完成请求（官方文档），
// 系统就拿不到"最大触点数"，触摸 Digitizer 可能无法完成初始化。
VOID TbEvtVhfGetFeature(
    _In_ PVOID VhfClientContext,
    _In_ VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID VhfOperationContext,
    _In_ PHID_XFER_PACKET HidTransferPacket)
{
    NTSTATUS status = STATUS_NOT_SUPPORTED;
    PTB_DEVICE_CONTEXT ctx = (PTB_DEVICE_CONTEXT)VhfClientContext;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    InterlockedIncrement(&ctx->FeatureRequests);

    if (HidTransferPacket != NULL &&
        HidTransferPacket->reportId == 0x01 &&
        HidTransferPacket->reportBuffer != NULL &&
        HidTransferPacket->reportBufferLen >= 2)
    {
        // HID 线上格式：报告的第一个字节是 Report ID，其后才是数据。
        HidTransferPacket->reportBuffer[0] = 0x01;                       // Report ID
        HidTransferPacket->reportBuffer[1] = (UCHAR)TB_FINGER_COUNT;     // Contact Count Maximum = 10
        status = STATUS_SUCCESS;
    }

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}

// ------------------------------------------------------------------ IOCTL
VOID TbEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    UNREFERENCED_PARAMETER(InputBufferLength);

    if (IoControlCode == IOCTL_TB_VHID_SUBMIT)
    {
        PVOID  buffer = NULL;
        size_t length = 0;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(TB_VHID_FRAME), &buffer, &length);
        if (NT_SUCCESS(status))
        {
            PTB_DEVICE_CONTEXT ctx = TbGetContext(WdfIoQueueGetDevice(Queue));
            TbQueueFrame(ctx, (const TB_VHID_FRAME*)buffer);

            // 可选：把驱动内部状态回填到输出缓冲，供接收端打日志定位。
            if (OutputBufferLength >= sizeof(TB_VHID_DIAG))
            {
                PVOID  out = NULL;
                size_t outLen = 0;

                if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(TB_VHID_DIAG), &out, &outLen)))
                {
                    PTB_VHID_DIAG diag = (PTB_VHID_DIAG)out;
                    diag->SubmitAttempts = (ULONG)InterlockedCompareExchange(&ctx->SubmitAttempts, 0, 0);
                    diag->SubmitFailures = (ULONG)InterlockedCompareExchange(&ctx->SubmitFailures, 0, 0);
                    diag->ReadyCallbacks = (ULONG)InterlockedCompareExchange(&ctx->ReadyCallbacks, 0, 0);
                    diag->FeatureRequests = (ULONG)InterlockedCompareExchange(&ctx->FeatureRequests, 0, 0);
                    diag->LastStatus     = (LONG)InterlockedCompareExchange(&ctx->LastSubmitStatus, 0, 0);
                    WdfRequestSetInformation(Request, sizeof(TB_VHID_DIAG));
                }
            }

            status = STATUS_SUCCESS;
        }
    }
    else if (IoControlCode == IOCTL_TB_VHID_GET_DESCRIPTOR)
    {
        // 把驱动正在用的报告描述符原样回给用户态（调试/对照用，见 TouchBridgeVhidPublic.h）
        PVOID  out = NULL;
        size_t outLen = 0;

        if (g_ReportDescriptorLength == 0)
        {
            status = STATUS_DEVICE_NOT_READY;           // 设备还没初始化完
        }
        else if (OutputBufferLength < g_ReportDescriptorLength)
        {
            status = STATUS_BUFFER_TOO_SMALL;
        }
        else if (NT_SUCCESS(status = WdfRequestRetrieveOutputBuffer(Request, g_ReportDescriptorLength, &out, &outLen)))
        {
            RtlCopyMemory(out, g_ReportDescriptor, g_ReportDescriptorLength);
            WdfRequestSetInformation(Request, g_ReportDescriptorLength);
            status = STATUS_SUCCESS;
        }
    }

    WdfRequestComplete(Request, status);
}

// ------------------------------------------------------------------ 设备生命周期
VOID TbEvtDeviceCleanup(_In_ WDFOBJECT Object)
{
    PTB_DEVICE_CONTEXT ctx = TbGetContext((WDFDEVICE)Object);

    if (ctx->Vhf != NULL)
    {
        VhfDelete(ctx->Vhf, TRUE);       // 必须先于设备对象销毁
        ctx->Vhf = NULL;
    }
}

NTSTATUS TbEvtDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS             status;
    WDFDEVICE            device;
    WDF_OBJECT_ATTRIBUTES attributes;
    PTB_DEVICE_CONTEXT   ctx;
    VHF_CONFIG           vhfConfig;
    WDF_IO_QUEUE_CONFIG  queueConfig;
    UNICODE_STRING       name;

    UNREFERENCED_PARAMETER(Driver);

    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);

    RtlInitUnicodeString(&name, TB_VHID_NT_NAME);
    status = WdfDeviceInitAssignName(DeviceInit, &name);
    if (!NT_SUCCESS(status))
        return status;

    // 安全描述符：系统/管理员全权，其余用户只给「读」。
    // 目的：让非管理员的调试工具（TouchSuite.HidDump）也能取报告描述符
    //   —— IOCTL_TB_VHID_GET_DESCRIPTOR 是 FILE_ANY_ACCESS，只读句柄即可调用；
    //   —— IOCTL_TB_VHID_SUBMIT 需要 FILE_WRITE_DATA，非管理员依旧打不开（注入仍要管理员）。
    {
        UNICODE_STRING sddl;
        RtlInitUnicodeString(&sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;WD)");
        status = WdfDeviceInitAssignSDDLString(DeviceInit, &sddl);
        if (!NT_SUCCESS(status))
            return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, TB_DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = TbEvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
        return status;

    ctx = TbGetContext(device);
    KeInitializeSpinLock(&ctx->Lock);
    ctx->Ready      = FALSE;
    ctx->PendingCount = 0;                             // 待投递队列为空
    ctx->ScanBase100ns = 0;                            // Scan Time：下一帧作为基准（从 0 起算）
    ctx->LastFrame100ns = 0;
    ctx->SubmitAttempts   = 0;
    ctx->SubmitFailures   = 0;
    ctx->ReadyCallbacks   = 0;
    ctx->LastSubmitStatus = 0;
    ctx->FeatureRequests  = 0;
    RtlZeroMemory(ctx->Latest, sizeof(ctx->Latest));
    RtlZeroMemory(ctx->Slots, sizeof(ctx->Slots));      // 清空"最后一次 move"记录（抬手沿用坐标用）

    RtlInitUnicodeString(&name, TB_VHID_DOS_NAME);
    status = WdfDeviceCreateSymbolicLink(device, &name);
    if (!NT_SUCCESS(status))
        return status;

    // ---- 虚拟 HID（VHF）----
    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        (USHORT)g_ReportDescriptorLength,
        g_ReportDescriptor);

    vhfConfig.VendorID                    = TB_VHID_VENDOR_ID;
    vhfConfig.ProductID                   = TB_VHID_PRODUCT_ID;
    vhfConfig.VersionNumber               = TB_VHID_VERSION;
    vhfConfig.VhfClientContext            = ctx;
    vhfConfig.EvtVhfReadyForNextReadReport = TbEvtVhfReadyForNextReadReport;
    vhfConfig.EvtVhfAsyncOperationGetFeature = TbEvtVhfGetFeature;

    status = VhfCreate(&vhfConfig, &ctx->Vhf);
    if (!NT_SUCCESS(status))
        return status;

    // ---- 默认队列：收用户态的 IOCTL ----
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = TbEvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status))
        return status;

    status = VhfStart(ctx->Vhf);
    if (!NT_SUCCESS(status))
        return status;

    {
        KIRQL oldIrql;                            // 初始化阶段无并发，加锁只是形式
        KeAcquireSpinLock(&ctx->Lock, &oldIrql);
        ctx->Ready = TRUE;                        // 允许第一份报告
        KeReleaseSpinLock(&ctx->Lock, oldIrql);
    }

    return STATUS_SUCCESS;
}

// ------------------------------------------------------------------ 入口
NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;

    TbBuildDescriptor();                          // 先备好报告描述符

    WDF_DRIVER_CONFIG_INIT(&config, TbEvtDeviceAdd);

    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}