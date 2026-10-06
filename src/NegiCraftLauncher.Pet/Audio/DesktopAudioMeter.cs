using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace NegiCraftLauncher.Pet.Audio;

/// <summary>
/// 监听 Windows 默认音频渲染设备混音器电平，提取实时音量能量与节拍率。
/// 采用 Windows Core Audio (WASAPI) 原生 IAudioMeterInformation 硬件峰值表，
/// 零后台线程、零格式协商、0% CPU 占用。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopAudioMeter : IDisposable
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private Timer? _pump;

    private float _rawPeak;
    private float _prevPeak;
    private float _envelope;
    private float _peakTracker = 0.05f;
    private float _silenceTimer;

    private double _lastBeatTime;
    private float _beatHz;

    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>瞬时原始硬件峰值 (0..1)。</summary>
    public float RawPeak => _rawPeak;

    /// <summary>AGC 归一化综合能量 (0..1)。用于驱动摆动幅度。</summary>
    public float Energy { get; private set; }

    /// <summary>兼容保留：底鼓能量（与 Energy 一致）。</summary>
    public float KickEnergy => Energy;

    /// <summary>估算的节拍率 (Hz，每秒几拍)。长时间无节拍时归零。</summary>
    public float BeatHz => _beatHz;

    /// <summary>换算后的每分钟节拍数 (BPM)。</summary>
    public float Bpm => _beatHz > 0 ? _beatHz * 60f : 0f;

    /// <summary>兼容保留：拍内相位。</summary>
    public float BeatPhase => 0f;

    /// <summary>兼容保留：置信度。</summary>
    public float Confidence => _beatHz > 0 ? 1f : 0f;

    /// <summary>兼容保留：是否检测到底鼓脉冲。</summary>
    public bool KickDetected => _rawPeak > 0.08f;

    /// <summary>兼容保留：是否使用环回裸流抓取。</summary>
    public bool IsLoopbackActive => false;

    /// <summary>音量计当前是否正在后台运行。</summary>
    public bool Running => _pump is not null;

    /// <summary>若启动失败，记录失败异常信息；成功为 null。</summary>
    public string? Fault { get; private set; }

    /// <summary>启动音频监听。</summary>
    public void Start()
    {
        if (Running) return;

        try
        {
            InitCom();
            _pump = new Timer(Pump, null, 20, 20);
            Fault = null;
        }
        catch (Exception ex)
        {
            Fault = ex.Message;
            Dispose();
        }
    }

    private void InitCom()
    {
        var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
        if (type is null) throw new InvalidOperationException("无法获取 MMDeviceEnumerator COM 类型");

        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));

        var meterIid = IidIAudioMeterInformation;
        Marshal.ThrowExceptionForHR(device.Activate(ref meterIid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var oMeterPtr));
        _meter = (IAudioMeterInformation)Marshal.GetObjectForIUnknown(oMeterPtr);
    }

    private void Pump(object? _)
    {
        var meter = _meter;
        if (meter is null) return;

        int hr = meter.GetPeakValue(out float p);
        if (hr < 0)
        {
            // 音频设备可能被切换，尝试重新连接
            try
            {
                InitCom();
            }
            catch
            {
                Energy = 0;
                _beatHz = 0;
                return;
            }
        }

        _rawPeak = p;
        var nowSec = _sw.Elapsed.TotalSeconds;

        // 快攻 (0.35)、缓衰 (0.06) 动态包络
        _envelope += (p > _envelope ? 0.35f : 0.06f) * (p - _envelope);

        // AGC 峰值追踪（衰减半衰期约 1.5 秒）
        _peakTracker = Math.Max(_envelope, _peakTracker * 0.996f);

        // 静音判定：持续 0.3 秒小于 0.005 视作静音
        if (p < 0.005f) _silenceTimer += 0.02f;
        else _silenceTimer = 0;

        bool isSilent = _silenceTimer > 0.3f;
        Energy = isSilent ? 0f : Math.Clamp(_envelope / Math.Max(0.04f, _peakTracker), 0f, 1f);

        // 节拍检测（电平突增）
        float diff = p - _prevPeak;
        double sinceLastBeat = nowSec - _lastBeatTime;

        if (diff > 0.06f && p > 0.08f && sinceLastBeat >= 0.22)
        {
            if (sinceLastBeat <= 1.6)
            {
                float curHz = (float)(1.0 / sinceLastBeat);
                _beatHz = _beatHz <= 0 ? curHz : (0.65f * _beatHz + 0.35f * curHz);
            }
            _lastBeatTime = nowSec;
        }
        else if (sinceLastBeat > 2.5)
        {
            _beatHz = 0;
        }

        _prevPeak = p;
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        _pump?.Dispose();
        _pump = null;
        _meter = null;
        _enumerator = null;
        Energy = 0;
        _beatHz = 0;
    }
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);
}

[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}
