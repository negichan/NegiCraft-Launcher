using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace NegiCraftLauncher.App.Media;

/// <summary>
/// 视频背景在 WPF 侧的实现。
///
/// <para><b>为什么不用 <see cref="System.Windows.Controls.MediaElement"/></b>：主窗口是
/// <c>AllowsTransparency=True</c> 的分层窗，而 <c>MediaElement</c> 是<b>独立的子 HWND</b> ——
/// 它裁不进外壳的 22px 圆角，吃不到 <c>BlurEffect</c>，还会盖在侧栏上面。改用
/// <c>MediaPlayer</c> + <c>VideoDrawing</c> + <c>DrawingBrush</c>：视频变成普通的绘制内容，
/// 能被裁剪、能被 Effect、能正常参与合成。</para>
///
/// <para><b>为什么是控制器而不是控件</b>：同一段视频要画在两个地方 —— 主背景层（跟随用户调的模糊）
/// 和侧栏磨砂背板（固定模糊 18）。两处必须共用同一个 <see cref="Brush"/>，才能只解码一份、
/// 且画面严格对齐；所以这里把画刷暴露出去，由 <c>MainWindow</c> 分别挂到两个元素上。</para>
///
/// <para><b>性能</b>（本机实测，1180x720 窗口、软件渲染、<c>CompositionTarget.Rendering</c> 次数/秒）：
/// 720p 无模糊 81–94、720p 模糊 40 58–73、4K 66Mbps 无模糊 75–91、4K 66Mbps 模糊 40 65–71；
/// UI 线程回调延迟全程 &lt;2ms。分层窗里 <c>MediaPlayer</c> 这条路是够用的，不需要自己拿
/// Media Foundation 解码。</para>
/// </summary>
internal sealed class VideoBackgroundController : IDisposable
{
    private readonly MediaPlayer _player = new()
    {
        // 默认静音。WPF 在 Open 之前还是之后设都认 —— 实测（design/_audioprobe + _audiometer）
        // 设备峰值：静音 0.00000（静态）vs 不静音 0.12522（59 个不同值，活跃），
        // 且"只在 Open 前设"与"Open 后再断言"结果一致。所以下面 ApplySound() 的重复断言
        // 是防御性的，不是在补某个已知的丢失问题。
        IsMuted = true,
        Volume = 0,
        // 暂停时也要能出画面（切页面会暂停，回来得立刻有帧）。
        ScrubbingEnabled = true,
    };

    private readonly VideoDrawing _drawing;

    private string? _path;
    private bool _sound;
    private bool _disposed;

    public VideoBackgroundController()
    {
        // Rect 只是占位；真正的大小要等 MediaOpened 才知道（见 OnMediaOpened）。
        _drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, 16, 9) };

        Brush = new DrawingBrush(_drawing)
        {
            // 背景要铺满整壳并保持比例：多余的部分裁掉，不拉伸变形。
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };

        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
    }

    /// <summary>主背景层与侧栏背板共用的画刷。</summary>
    public Brush Brush { get; }

    /// <summary>
    /// 是否出声。<b>默认 <c>false</c>（静音）</b>。值没变时不做任何事，所以可以随便重复设。
    /// </summary>
    public bool Sound
    {
        get => _sound;
        set
        {
            if (_sound == value) return;
            _sound = value;
            ApplySound();
        }
    }

    /// <summary>
    /// 把 <see cref="Sound"/> 落到播放器上。<c>Volume</c> 和 <c>IsMuted</c> 一起设：
    /// 光靠 <c>IsMuted</c> 在某些驱动/音频会话上不够干净，<c>Volume=0</c> 是第二道保险。
    /// </summary>
    private void ApplySound()
    {
        if (_disposed) return;

        try
        {
            _player.IsMuted = !_sound;
            _player.Volume = _sound ? 1.0 : 0.0;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>诊断用：播放器此刻的状态（调试桥拿它确认开关有没有落到播放器上）。</summary>
    public string DebugState
    {
        get
        {
            try
            {
                return $"muted={_player.IsMuted} volume={_player.Volume:0.##} " +
                       $"hasAudio={_player.HasAudio} pos={_player.Position.TotalSeconds:0.0}s " +
                       $"source={(string.IsNullOrEmpty(_path) ? "n/a" : Path.GetFileName(_path))}";
            }
            catch (Exception ex)
            {
                return $"(unavailable: {ex.GetType().Name})";
            }
        }
    }

    /// <summary>当前是否挂着一个视频（打开失败后会被清掉）。</summary>
    public bool HasVideo => _path is not null;

    /// <summary>播放失败时抛出给视图，由它转成 <c>ShowBanner</c>。</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// 换片。传 <c>null</c> 或空串就是卸掉视频（回到生成图）。
    /// 路径没变就什么都不做 —— VM 的 <c>PropertyChanged</c> 会重复触发。
    /// </summary>
    public void Load(string? path)
    {
        if (_disposed) return;
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;

        _path = path;

        try
        {
            _player.Stop();
            _player.Close();
        }
        catch (Exception)
        {
            // Close 在没有片源时会抛；这不是错误。
        }

        if (string.IsNullOrEmpty(path)) return;

        try
        {
            _player.Open(new Uri(Path.GetFullPath(path!)));
            // 换片时 _sound 可能已经和上一次不同，Open 之后重落一遍（首次挂载时与字段初始化等价）。
            ApplySound();
        }
        catch (Exception ex)
        {
            _path = null;
            Failed?.Invoke($"视频打不开：{ex.Message}");
        }
    }

    /// <summary>
    /// 只在首页且窗口可见时才播。背景只在首页显示，切到设置页还继续解码 4K 是白烧 CPU。
    /// </summary>
    public void SetActive(bool active)
    {
        if (_disposed || _path is null) return;

        try
        {
            if (active) _player.Play();
            else _player.Pause();
        }
        catch (Exception)
        {
        }
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        // 必须等 MediaOpened 才知道真实分辨率；不设的话 VideoDrawing 会按占位的 16:9 拉伸，
        // UniformToFill 就跟着按错的宽高比裁切。
        if (_player.NaturalVideoWidth > 0)
        {
            _drawing.Rect = new Rect(0, 0, _player.NaturalVideoWidth, _player.NaturalVideoHeight);
        }

        // 防御性再落一遍：成本为零，且不依赖"DP 值一定全程有效"这种假设。
        ApplySound();

        try
        {
            _player.Play();
        }
        catch (Exception)
        {
        }
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        var message = e.ErrorException?.Message ?? "未知原因";
        _path = null;
        Failed?.Invoke($"视频播放失败：{message}");
    }

    /// <summary>MediaPlayer 没有内建循环，自己接上。</summary>
    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_path is null) return;

        try
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _player.MediaOpened -= OnMediaOpened;
        _player.MediaFailed -= OnMediaFailed;
        _player.MediaEnded -= OnMediaEnded;

        try
        {
            _player.Stop();
            _player.Close();
        }
        catch (Exception)
        {
        }
    }
}
