using System;
using System.Collections.Generic;

namespace XmaBrowser.Ui;

/// <summary>
/// 加载进度模型（纯逻辑，可离线自测）。
///
/// 为什么要有它：走浏览器通道取一页要 2–4 秒，中间还有「起浏览器 ≈1 秒 → 打开页面 ≈2 秒」
/// 这种明显的分段。以前界面只有一句「正在读取 xxx 的详情…」——用户看到的是「卡住了」，
/// 而不是「还有 1.5 秒」。
///
/// 做法：每个阶段给一个**预估耗时**，进度 = 已完成阶段的预估之和 + 当前阶段已用时间
/// （在当前阶段的预估处封顶，超时也不会让条子冲过头）；全部走完就是 1。
/// 进度**只增不减**，所以不会来回跳。
/// </summary>
public sealed class LoadProgress
{
    public sealed record Stage(string Name, int EstimatedMs);

    private readonly object _gate = new();
    private readonly IReadOnlyList<Stage> _stages;
    private readonly int _totalMs;

    private int _index = -1;                      // -1 = 还没开始
    private DateTime _beganUtc = DateTime.MinValue;
    private DateTime _stageStartUtc = DateTime.MinValue;
    private string _note = string.Empty;
    private string _title = string.Empty;
    private bool _done;
    private bool _failed;

    public LoadProgress(IReadOnlyList<Stage> stages, string title = "")
    {
        _stages = stages is { Count: > 0 } ? stages : new[] { new Stage("加载", 1000) };
        _title = title;
        var total = 0;
        foreach (var s in _stages) total += Math.Max(1, s.EstimatedMs);
        _totalMs = total;
    }

    /// <summary>详情加载的分段与预估（数字来自本机实测：会话预热 746 ms、通道取页 1–2.5s、取图 1–3s）。</summary>
    public static LoadProgress ForDetail(string title = "") => new(new[]
    {
        new Stage("检查缓存", 30),
        new Stage("准备浏览器", 760),
        new Stage("打开页面", 1800),
        new Stage("解析内容", 200),
        new Stage("加载预览图", 1000),
    }, title);

    /// <summary>搜索的分段与预估。</summary>
    public static LoadProgress ForSearch(string title = "") => new(new[]
    {
        new Stage("检查缓存", 30),
        new Stage("请求站点", 2200),
        new Stage("解析结果", 200),
    }, title);

    public string Title => _title;
    public int StageCount => _stages.Count;
    public int EstimatedTotalMs => _totalMs;

    public string LastStageName
    {
        get { lock (_gate) return _index >= 0 && _index < _stages.Count ? _stages[_index].Name : string.Empty; }
    }

    public void Begin()
    {
        lock (_gate)
        {
            _index = 0;
            _beganUtc = DateTime.UtcNow;
            _stageStartUtc = _beganUtc;
            _done = false;
            _failed = false;
            _note = string.Empty;
        }
    }

    /// <summary>进入第 index 个阶段（只前进；重复或倒退的调用会被忽略，只更新备注）。</summary>
    public void Enter(int index, string? note = null)
    {
        lock (_gate)
        {
            if (_done || _failed) return;
            if (_beganUtc == DateTime.MinValue)
            {
                _beganUtc = DateTime.UtcNow;
                _stageStartUtc = _beganUtc;
            }
            if (index <= _index)
            {
                if (note is not null) _note = note;
                return;
            }
            _index = Math.Min(index, _stages.Count - 1);
            _stageStartUtc = DateTime.UtcNow;
            _note = note ?? string.Empty;
        }
    }

    /// <summary>只改备注（比如「直连被拒 → 改走浏览器通道」），不动进度。</summary>
    public void SetNote(string note)
    {
        lock (_gate) _note = note ?? string.Empty;
    }

    public void Complete()
    {
        lock (_gate)
        {
            _done = true;
            _failed = false;
            _index = _stages.Count - 1;
        }
    }

    public void Fail(string reason)
    {
        lock (_gate)
        {
            _failed = true;
            _done = false;
            _note = reason ?? string.Empty;
        }
    }

    public bool Done { get { lock (_gate) return _done; } }
    public bool Failed { get { lock (_gate) return _failed; } }
    public string Note { get { lock (_gate) return _note; } }
    public bool Running { get { lock (_gate) return _beganUtc != DateTime.MinValue && !_done && !_failed; } }

    public long ElapsedMs
    {
        get
        {
            lock (_gate)
                return _beganUtc == DateTime.MinValue ? 0 : (long)(DateTime.UtcNow - _beganUtc).TotalMilliseconds;
        }
    }

    /// <summary>0..1。只增不减；完成时正好 1。</summary>
    public float Fraction
    {
        get
        {
            lock (_gate)
            {
                if (_done) return 1f;
                if (_beganUtc == DateTime.MinValue || _index < 0) return 0f;

                long doneMs = 0;
                for (var i = 0; i < _index; i++) doneMs += Math.Max(1, _stages[i].EstimatedMs);

                var curEst = Math.Max(1, _stages[_index].EstimatedMs);
                var inStage = (DateTime.UtcNow - _stageStartUtc).TotalMilliseconds;
                if (inStage > curEst) inStage = curEst;          // 超时不让条子冲过头

                var f = (float)((doneMs + inStage) / _totalMs);
                if (f < 0f) return 0f;
                return f > 0.99f ? 0.99f : f;
            }
        }
    }

    /// <summary>给界面用的一行字：「打开页面…（浏览器通道） 已用 2.1s / 约 3.9s」。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_failed) return string.IsNullOrEmpty(_note) ? "加载失败" : "加载失败：" + _note;
            if (_done)
            {
                var totalS = _beganUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _beganUtc).TotalSeconds;
                return $"完成　用时 {totalS:0.0}s";
            }
            if (_index < 0) return "准备中…";

            var name = _index < _stages.Count ? _stages[_index].Name : "加载";
            var elapsedS = _beganUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _beganUtc).TotalSeconds;
            var estS = _totalMs / 1000.0;
            var extra = string.IsNullOrEmpty(_note) ? string.Empty : $"（{_note}）";
            return $"{name}…{extra}　已用 {elapsedS:0.0}s / 约 {estS:0.0}s";
        }
    }
}
