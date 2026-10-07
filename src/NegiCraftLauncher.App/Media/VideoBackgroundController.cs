using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using NegiCraftLauncher.Raster;

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
    private int _volume;
    private bool _disposed;

    public VideoBackgroundController()
    {
        // Rect 只是占位；真正的大小要等 MediaOpened 才知道（见 OnMediaOpened）。
        _drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, 16, 9) };

        _brush = new DrawingBrush(_drawing)
        {
            // 取景全在 brush 上算（见 <see cref="SetFrame" />）：Viewbox = "从视频里取哪一块"，
            // Viewport = "画到元素矩形的哪一块"。以前这里是 Stretch=UniformToFill + 居中，
            // 等价于"取最大能放进矩形的那块"，没有平移的余地。
            //
            // ⚠️ 两个 Units 必须显式写成 Absolute —— TileBrush 默认是 RelativeToBoundingBox，
            //    喂像素数进去会被当成比例（这条在 Skin/Rendering/SkinGpuViewport.cs:139-166 踩过）。
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
        };

        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;

        // 画中画那格的第二支画刷：主层那支的 Viewbox 是"窗口看得见的那块"，而画中画要的是
        // <b>整帧</b> —— 一个 brush 只有一套几何，没法两处共用，所以再建一支。
        //
        // ⚠️ 两支画刷必须共用<b>同一个 VideoDrawing 实例</b>。另建一个 VideoDrawing 挂同一个
        //    MediaPlayer 是画不出来的（实测小窗只剩描边）：WPF 的视频渲染只认那一个绘制目标。
        //    共用 Drawing 则各自按自己的 Viewbox 光栅化，解码仍然只有一次。
        _pipBrush = new DrawingBrush(_drawing)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = _drawing.Rect,
        };
    }

    private readonly DrawingBrush _pipBrush;

    /// <summary>
    /// 画中画用的画刷。调用方把 <see cref="SetPipViewport" /> 设成格子里那块图片矩形即可，
    /// Viewbox 跟着 MediaOpened 换成真实帧尺寸。
    /// </summary>
    public Brush PipBrush => _pipBrush;

    /// <summary>画中画：把整帧画进这个矩形（chip 里算好的那块图片区域）。</summary>
    public void SetPipViewport(Rect viewport) => _pipBrush.Viewport = viewport;

    /// <summary>
    /// 主背景层与侧栏背板共用的画刷。对外只给 <see cref="Brush"/> 这个基类型
    /// （两处元素只需要 Fill），取景要写的 Viewport/Viewbox 在 <see cref="_brush"/> 上。
    /// </summary>
    public Brush Brush => _brush;

    private readonly DrawingBrush _brush;

    private double _frameWidth, _frameHeight, _panX, _panY = 0, _zoom = 100;

    /// <summary>
    /// 换取景（平移 + 缩放），参数与图片那条路完全同一套（数学在
    /// <c>Raster/BackgroundFrame.cs</c>，所以图片和视频不会各摆各的）。
    ///
    /// <para>几何只落在这一个 brush 上，而主背景层与侧栏背板共用它 ⇒ 改一处两处同步。
    /// ⚠️ 前提是两个元素的矩形都等于壳矩形（<c>MainWindow.ApplyVideoBackground</c> 把同一个
    /// brush 挂给两处）。用 UniformToFill 时这个前提被掩盖着 —— 换成绝对 Viewport 之后
    /// 一旦哪天侧栏不再是壳尺寸，两处就会分叉。</para>
    /// </summary>
    public void SetFrame(double width, double height, double panX, double panY, double zoom)
    {
        _frameWidth = width;
        _frameHeight = height;
        _panX = panX;
        _panY = panY;
        _zoom = zoom;
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        var nat = _drawing.Rect.Size;
        if (_frameWidth <= 0 || _frameHeight <= 0 || nat.Width <= 0 || nat.Height <= 0) return;

        var box = BackgroundFrame.ComputeVideoViewbox(
            _frameWidth, _frameHeight, nat.Width, nat.Height, _panX, _panY, _zoom);

        // 取景框与元素同宽高比 ⇒ Stretch=Fill 拉过去也不会变形；Viewport 就是壳矩形。
        _brush.Viewport = new Rect(0, 0, _frameWidth, _frameHeight);
        _brush.Viewbox = new Rect(box.X, box.Y, box.Width, box.Height);
    }

    /// <summary>
    /// 音量，<c>0</c>–<c>100</c>；<b><c>0</c> 就是静音</b>。值没变时不做任何事，所以可以随便重复设。
    ///
    /// <para>⚠️ 静音<b>没有第二个开关</b>。以前这里另有一个 <c>Sound</c> 布尔，和音量各管各的：
    /// 音量条拖到 0 会顺手把它关掉，可把音量条拖回去时没人把它打开 —— 结果就是"音量 40 了还是没声，
    /// 得去点一下喇叭"。一个状态两份记录，就一定会分叉。</para>
    /// </summary>
    public int Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_volume == clamped) return;
            _volume = clamped;
            ApplySound();
        }
    }

    /// <summary>
    /// 把 <see cref="Volume"/> 落到播放器上。<c>IsMuted</c> 和 <c>Volume</c> 一起设：
    /// 光靠 <c>IsMuted</c> 在某些驱动/音频会话上不够干净，<c>Volume=0</c> 是第二道保险。
    /// </summary>
    private void ApplySound()
    {
        if (_disposed) return;

        try
        {
            _player.IsMuted = _volume == 0;
            _player.Volume = _volume / 100.0;
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
                return $"muted={_player.IsMuted} volume={_player.Volume:0.##} want={_volume} " +
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

    /// <summary>
    /// 视频的原生分辨率。<c>MediaOpened</c> 之前是构造里那个 16:9 占位 —— 拖动平移要按它算余量，
    /// 所以拿到真实尺寸前拖出来的量会略偏，属于可接受的短暂状态。
    /// </summary>
    public Size NaturalSize => _drawing.Rect.Size;

    /// <summary>播放失败时抛出给视图，由它转成 <c>ShowBanner</c>。</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// 媒体已打开、原生尺寸已可信。<b>只在这一刻</b>取景和画中画的余量才算得对
    /// （在那之前 _drawing.Rect 是 16:9 占位），所以视图要借这个时机重算一遍。
    /// </summary>
    public event Action? Opened;

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
            // Open 会重开一条音频会话，音量得照着当前值再落一遍（含"0 = 静音"那道 IsMuted）。
            ApplySound();
        }
        catch (Exception ex)
        {
            _path = null;
            Failed?.Invoke($"视频打不开：{ex.Message}");
        }
    }

    /// <summary>
    /// 播放器该不该在跑。窗口收进托盘时停，摆回屏幕就恢复 —— 不再跟"在哪一页"有关，
    /// 背景现在每一页都在。
    /// </summary>
    public void SetActive(bool active)
    {
        // 意图先记下来，再判"有没有片"：不然收进托盘时才 Load 完的视频会因为
        // _wanted 还停在初值而偷偷开播。
        _wanted = active;
        if (_disposed || _path is null) return;

        try
        {
            // 拖动期间（<see cref="_frozen" />）任何"该播"的指令都只记下意图，不真去 Play：
            // 不然一个 600ms 的属性变化就能把刚按停的画面又放开。
            if (active && !_frozen) _player.Play();
            else _player.Pause();
        }
        catch (Exception)
        {
        }
    }

    private bool _wanted = true;
    private bool _frozen;

    /// <summary>
    /// 拖动窗口时把播放器按停。移动窗口不脏化可视树，所以"没有新帧"就等于"这块不再重画"——
    /// 连带压在它上面的两层 <c>BlurEffect</c> 也不再每帧跑。
    ///
    /// <para>没有做"当前帧快照"那一步：那要重新对齐几何（<c>Viewbox</c> 是按帧原生尺寸算的）
    /// 还要一次性分配整幅 RTB，而按停已经让 WPF 根本不重绘这块。<c>ScrubbingEnabled=true</c>
    /// 保证暂停时最后那一帧仍然在屏上。</para>
    /// </summary>
    public void FreezeForDrag()
    {
        if (_frozen || _disposed || _path is null) return;

        _frozen = true;
        try
        {
            _player.Pause();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>松手恢复。只有按停前"该播"才真播回去。</summary>
    public void ResumeAfterDrag()
    {
        if (!_frozen) return;

        _frozen = false;
        if (_disposed || _path is null) return;

        try
        {
            if (_wanted) _player.Play();
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
            // 画中画那支跟着换成真实帧尺寸（它的 Viewbox 一直是"整帧"）。
            _pipBrush.Viewbox = _drawing.Rect;
            // 原生尺寸现在才可信，取景按它重算一遍（之前是按 16:9 占位算的）。
            ApplyFrame();
            Opened?.Invoke();
        }

        // 防御性再落一遍：成本为零，且不依赖"DP 值一定全程有效"这种假设。
        ApplySound();

        // 首播也走 SetActive：不然一次 MediaOpened 会绕过"窗口收进托盘"和"正在拖动"两道门。
        SetActive(_wanted);
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
            // 循环重启也不能绕过那两道门（收进托盘 / 正在拖动）。松手时 ResumeAfterDrag 会从 0 接着播。
            if (_wanted && !_frozen) _player.Play();
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
