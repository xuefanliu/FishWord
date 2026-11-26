using System.IO;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FishWord.Core.Models;
using FishWord.Core.Scheduling;
using FishWord.Infrastructure.Data;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace FishWord.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<int, Word> _wordById = new();
    private InterleaveScheduler? _scheduler;
    private InterleaveScheduler? _reviewScheduler;
    private MediaPlayer? _player;
    private SpeechSynthesizer? _tts; // 系统 TTS 兜底（懒加载，无音频文件的词用）
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private WordRepository? _wordRepo;
    private ProgressRepository? _progressRepo;
    private string _today = "";
    private IReadOnlyList<Word>? _bookWords;
    private List<KnownWordRow>? _knownWords;
    private string _knownTab = "user"; // 熟词来源 tab：'user' / 'system'（我的熟词在左，默认选中）

    private int _currentGoal;
    private int _currentWordId = -1;
    private int _currentPass;
    private int _completed;
    private bool _remembered = true;
    private bool _revealed;
    private bool _reviewing;
    private bool _sessionComplete; // 学习/复习会话完成态：只显示「完成！」，隐藏全部内容与按钮
    private int _reviewTotal;
    private int _reviewDone;
    private readonly HashSet<int> _reviewedToday = new();

    private bool _hasMeaning;
    private bool _hasExample;
    private bool _hasMorph;
    private bool _hasRoot;
    private bool _hasDerv;
    private bool _hasEtymology;

    /// <summary>详情页内容 Tab：例句 / 派生 / 词根 / 词源，恒显示四个，点击切换，默认例句。</summary>
    private enum DetailTab { Example, Deriv, Root, Etym }
    private DetailTab _activeTab = DetailTab.Example;

    // 设置 / 全局热键 / 跨天
    private const int HotKeyId = 1;
    private HwndSource? _hwndSource;
    private bool _capturingHotKey;
    private string _hotKey = "Ctrl+Alt+W";
    private int _goal = 10;
    private int _currentBookId = 1;
    private IReadOnlyList<BookInfo> _books = Array.Empty<BookInfo>();
    private DispatcherTimer? _dayTimer;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            ToggleCollapsed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ResourceLocator.EnsureUserData(); // 首启把种子 db/音频复制到用户数据目录（幂等）
            string? db = ResourceLocator.Find("resources/fishword.db");
            if (db is null)
            {
                SpellingText.Text = "找不到 fishword.db";
                PhoneticText.Text = "请确认 resources/fishword.db 存在";
                return;
            }

            _wordRepo = new WordRepository(db);
            _progressRepo = new ProgressRepository(db);
            _today = DateTime.Now.ToString("yyyy-MM-dd");
            _currentBookId = _progressRepo.GetCurrentBookId();

            InitializeBasic505();
            RegisterHotKeyFromSettings();
            StartDayTimer();
            InitContextMenu();
            InitTrayIcon();
            ApplyTheme(_progressRepo.GetSetting("theme") ?? "dark");
            UpdateThemeButtons();

            ShowHome();
        }
        catch (Exception ex)
        {
            SpellingText.Text = "加载失败";
            PhoneticText.Text = ex.Message;
        }
    }

    /// <summary>账号初始化：首次运行时把 505 基础词标为系统熟词（幂等，settings 标志位保证只跑一次）。</summary>
    private void InitializeBasic505()
    {
        if (_progressRepo!.IsBasic505Marked())
            return;

        var spellings = LoadBasic505();
        if (spellings.Count == 0)
            return;

        _progressRepo.MarkBasicWords505(spellings);
        _progressRepo.SetBasic505Marked();
    }

    /// <summary>读取 505 基础词名单（resources/basic_505.json）。</summary>
    private static IReadOnlyList<string> LoadBasic505()
    {
        string? path = ResourceLocator.Find("resources/basic_505.json");
        if (path is null)
            return Array.Empty<string>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>从 settings 读快捷键并注册全局热键。</summary>
    private void RegisterHotKeyFromSettings()
    {
        _hotKey = _progressRepo!.GetSetting("hotkey") ?? "Ctrl+Alt+W";
        RegisterHotKey(_hotKey);
    }

    private void RegisterHotKey(string hotKey)
    {
        if (_hwndSource is null)
            return;

        UnregisterHotKey(_hwndSource.Handle, HotKeyId); // 幂等注销旧热键

        var (modifiers, vk) = ParseHotKey(hotKey);
        if (vk == 0)
            return;
        RegisterHotKey(_hwndSource.Handle, HotKeyId, modifiers, vk);
    }

    /// <summary>解析 "Ctrl+Alt+W" → (修饰键掩码, 虚拟键码)。</summary>
    private static (uint Modifiers, uint Vk) ParseHotKey(string hotKey)
    {
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
        uint modifiers = 0;
        uint vk = 0;

        foreach (var part in hotKey.Split('+'))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (Enum.TryParse<Key>(part.Trim(), true, out var key))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return (modifiers, vk);
    }

    private void ToggleCollapsed() => SetCollapsed(CollapsedView.Visibility != Visibility.Visible);

    private void StartDayTimer()
    {
        _dayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayTimer.Tick += (_, _) => CheckDayRollover();
        _dayTimer.Start();
    }

    /// <summary>跨天检测：系统日期变化时刷新为新一天（0 点自动刷新进度）。</summary>
    private void CheckDayRollover()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today == _today)
            return;

        _today = today;
        _scheduler = null;
        _reviewScheduler = null;
        ShowHome();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotKey)
            return;

        e.Handled = true;

        // Alt 组合键时 e.Key 是 Key.System，实际键在 e.SystemKey
        var main = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单独按修饰键不构成快捷键，等待主键
        if (main == Key.LeftCtrl || main == Key.RightCtrl ||
            main == Key.LeftAlt || main == Key.RightAlt ||
            main == Key.LeftShift || main == Key.RightShift ||
            main == Key.LWin || main == Key.RWin ||
            main == Key.None)
            return;

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(main.ToString());

        _hotKey = string.Join("+", parts);
        _capturingHotKey = false;
        HotKeyText.Text = _hotKey;
        HotKeyButton.Content = "设置";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _goal = _progressRepo!.GetStudyGoal();
        GoalText.Text = _goal.ToString();
        _hotKey = _progressRepo.GetSetting("hotkey") ?? "Ctrl+Alt+W";
        HotKeyText.Text = _hotKey;
        _capturingHotKey = false;
        HotKeyButton.Content = "设置";

        SettingsView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        StudyView.Visibility = Visibility.Collapsed;
        KnownView.Visibility = Visibility.Collapsed;
        BookSelectView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        ProgressText.Text = "";
    }

    private void GoalMinus_Click(object sender, RoutedEventArgs e)
    {
        _goal = Math.Max(1, _goal - 1);
        GoalText.Text = _goal.ToString();
    }

    private void GoalPlus_Click(object sender, RoutedEventArgs e)
    {
        _goal = Math.Min(999, _goal + 1);
        GoalText.Text = _goal.ToString();
    }

    private void HotKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotKey)
        {
            _capturingHotKey = false;
            HotKeyText.Text = _hotKey;
            HotKeyButton.Content = "设置";
            return;
        }

        _capturingHotKey = true;
        HotKeyText.Text = "请按组合键…";
        HotKeyButton.Content = "取消";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _progressRepo!.SetSetting("daily_goal", _goal.ToString());
        _progressRepo.SetSetting("hotkey", _hotKey);
        RegisterHotKey(_hotKey);
        ShowHome();
    }

    private void ShowHome()
    {
        HomeView.Visibility = Visibility.Visible;
        StudyView.Visibility = Visibility.Collapsed;
        KnownView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        BookSelectView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        ProgressText.Text = "";

        _reviewing = false;
        _reviewScheduler = null;

        HomeDateText.Text = DateTime.Now.ToString("yyyy-MM-dd");

        // 当前词书：显示简称 + 刷新列表数据
        _books = _wordRepo!.GetBooks();
        var current = _books.FirstOrDefault(b => b.Id == _currentBookId);
        CurrentBookButton.Content = current is null ? "词书" : ShortBookName(current.Name);

        var study = _progressRepo!.GetStudySummary(_today);
        StudyCountText.Text = $"{study.Completed} / {study.Goal}";
        BubbleProgressText.Text = $"{study.Completed}/{study.Goal}";

        // 按钮状态机（与 StartStudy 的队列裁剪逻辑对齐，修复「改小目标后永远无法再来一组」）：
        //   - 按新目标裁剪后仍有词可背 → 继续学习（有进行中）/ 去学习（纯新词）
        //   - 裁剪后无词可背 且 完成 ≥ 目标 → 再来一组
        //   - 其他 → 去学习
        var homePasses = _progressRepo.GetDailyPasses(_today);
        int effective = EffectiveRemainingCount(homePasses, study.Goal, study.Completed);
        bool inProgress = _progressRepo.HasInProgressStudy(_today);
        StudyButton.Content = effective > 0
            ? (inProgress ? "继续学习" : "去学习")
            : (study.Completed >= study.Goal ? "再来一组" : "去学习");

        var review = _progressRepo!.GetReviewSummary(_today);
        ReviewCountText.Text = $"{review.Completed} / {review.DueTotal}";
        if (review.DueTotal == 0)
        {
            ReviewButton.Content = "暂无复习";
            ReviewButton.IsEnabled = false;
        }
        else if (review.Completed >= review.DueTotal)
        {
            ReviewButton.Content = "已完成";
            ReviewButton.IsEnabled = false;
        }
        else
        {
            ReviewButton.Content = "去复习";
            ReviewButton.IsEnabled = true;
        }
    }

    /// <summary>按当前目标裁剪后的「有效待背词数」（与 StartStudy 的队列裁剪逻辑一致）：
    /// 进行中（pass 1/2）优先保留、未开始（pass 0）补足剩余名额，超出的不计入。</summary>
    private static int EffectiveRemainingCount(IReadOnlyDictionary<int, int> passes, int goal, int completed)
    {
        int need = Math.Max(0, goal - completed);
        int inProgress = passes.Count(p => p.Value is 1 or 2);
        int kept = Math.Min(inProgress, need);
        int fresh = passes.Count(p => p.Value == 0);
        kept += Math.Min(fresh, Math.Max(0, need - kept));
        return kept;
    }

    private void StartStudy(bool extraGroup)
    {
        _reviewing = false;
        _reviewScheduler = null;
        _sessionComplete = false;

        StudyView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        KnownView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        BookSelectView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;

        int goal = _progressRepo!.GetStudyGoal();
        var passes = _progressRepo!.GetDailyPasses(_today);
        var bookWords = _wordRepo!.GetBookWords(_currentBookId);
        _bookWords = bookWords;
        var knownIds = _progressRepo!.GetKnownWordIds();
        var learnedIds = _progressRepo!.GetLearnedWordIds();

        _wordById.Clear();
        foreach (var w in bookWords)
            _wordById[w.Id] = w;

        int completed = passes.Count(p => p.Value >= 3);

        // 队列组装：今日实际要背的量 = goal − 已完成（用户中途调小目标时按新目标裁剪队列，
        // 修复「10 改 5 后仍背到 10」的问题）。
        //   - 进行中的词（pass 1/2）优先保留：更接近完成的排前面；
        //   - 未开始（pass 0）按顺序补足剩余名额，多余的释放（与词书切换的释放逻辑一致）；
        //   - 进行中超出的词记录保留（进度不丢），只是今天不再安排；
        //   - 「再来一组」不受裁剪影响（追加语义）。
        var kept = new List<(int WordId, int Pass)>();
        var releaseIds = new List<int>();

        if (extraGroup)
        {
            // 「再来一组」只追加一整组新词（数量 = 当前目标 N）；被裁剪搁置的进行中词
            // （目标调小场景）今天不再安排——修复「目标 10→5 后点再来一组要学到 15/5」。
            // 正常场景（无进行中词时才会出现「再来一组」按钮）行为不变。
        }
        else
        {
            int need = Math.Max(0, goal - completed);
            foreach (var kv in passes.Where(p => p.Value == 2)
                     .Concat(passes.Where(p => p.Value == 1)))
            {
                if (kept.Count >= need)
                    break;
                kept.Add((kv.Key, kv.Value + 1));
            }
            foreach (var kv in passes.Where(p => p.Value == 0))
            {
                if (kept.Count >= need)
                {
                    releaseIds.Add(kv.Key); // 多余的未开始占位词释放
                    continue;
                }
                kept.Add((kv.Key, 1));
            }
        }

        var queue = kept;
        if (releaseIds.Count > 0)
            _progressRepo!.DeleteDailyWords(_today, releaseIds);

        // 需要补的新词数：
        //   - 再来一组：追加一整组 goal 个；
        //   - 否则：补满"当前组"到 goal（等价 goal - (completed%goal) - 学习中 - 未开始）。
        //     用 goal*(completed/goal+1) - 队列词数 表达：切换词书删掉了 pass=0 占位词，
        //     补词数会自动增大，把空位换成新词书的词；多组时按"当前组"而非累计队列算。
        int newWords = extraGroup
            ? goal
            : Math.Max(0, goal * (completed / goal + 1) - passes.Count);

        int added = 0;
        foreach (var w in bookWords)
        {
            if (added >= newWords)
                break;
            if (passes.ContainsKey(w.Id))
                continue;
            if (knownIds.Contains(w.Id))
                continue;
            if (learnedIds.Contains(w.Id))
                continue;
            _progressRepo!.EnqueueStudyWord(_today, w.Id, extraGroup);
            queue.Add((w.Id, 1));
            added++;
        }

        // 关键：切换词书后，queue 里可能含其他词书「学习中」（pass 1/2）的词，
        // 这些词不在当前词书 bookWords 里，需按 id 补加载进 _wordById，
        // 否则 ShowNext 取词时 _wordById 缺键崩溃。
        var missingIds = queue
            .Select(q => q.WordId)
            .Where(id => !_wordById.ContainsKey(id))
            .Distinct()
            .ToList();
        if (missingIds.Count > 0)
        {
            foreach (var w in _wordRepo!.GetWordsByIds(missingIds))
                _wordById[w.Id] = w;
        }

        _scheduler = new InterleaveScheduler();
        _scheduler.Seed(queue);

        _currentGoal = goal;
        _completed = completed;
        ProgressText.Text = $"{_completed}/{goal}";
        BubbleProgressText.Text = ProgressText.Text;
        ShowNext();
    }

    private void Study_Click(object sender, RoutedEventArgs e)
    {
        bool extra = StudyButton.Content?.ToString() == "再来一组";
        StartStudy(extra);
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        StartReview();
    }

    private void StartReview()
    {
        _sessionComplete = false;
        var dueWords = _progressRepo!.GetDueReviewWords(_today);
        if (dueWords.Count == 0)
        {
            MessageBox.Show("今天没有到期的复习单词。", "FishWord", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 今日累计进度：总数 = 已复习 + 剩余到期（恒定），已复习数来自 review_logs。
        var summary = _progressRepo!.GetReviewSummary(_today);
        _reviewTotal = summary.DueTotal;
        _reviewDone = summary.Completed;
        _reviewedToday.Clear();

        _wordById.Clear();
        foreach (var w in dueWords)
            _wordById[w.Id] = w;

        _reviewing = true;
        _scheduler = null;
        _reviewScheduler = new InterleaveScheduler(lag12: 2, lag23: 2, lagFail: 2, passCount: 2);
        _reviewScheduler.Seed(dueWords.Select(w => w.Id));

        StudyView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        KnownView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        BookSelectView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;

        ProgressText.Text = $"{_reviewDone}/{_reviewTotal}";
        BubbleProgressText.Text = ProgressText.Text;

        ShowNext();
    }

    private void KnownWords_Click(object sender, RoutedEventArgs e)
    {
        UpdateKnownTabButtons();
        RefreshKnownWords();
        KnownView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        StudyView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        BookSelectView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        ProgressText.Text = "";
    }

    private void RefreshKnownWords()
    {
        _knownWords = _progressRepo!.GetKnownWords(_knownTab).Select(w => new KnownWordRow(w)).ToList();
        KnownList.ItemsSource = _knownWords;
    }

    /// <summary>手风琴：点击单词区展开，再次点击收起，一次只展开一个。</summary>
    private void KnownRow_Toggle(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KnownWordRow row })
        {
            ToggleRow(row);
        }
    }

    private void ToggleRow(KnownWordRow row)
    {
        if (_knownWords is null)
            return;

        bool expand = !row.IsExpanded;
        foreach (var r in _knownWords)
            r.IsExpanded = false;
        row.IsExpanded = expand;
    }

    private void KnownAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KnownWordRow row })
        {
            PlayWordAudio(row.Word);
        }
    }

    private void KnownWord_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWordId < 0)
            return;

        int wordId = _currentWordId;

        if (_reviewing)
        {
            // 复习中标记熟词：写熟词标记（退出复习队列）+ 当前词自然出队，总数 -1
            _progressRepo!.MarkKnown(_today, wordId);
            _reviewTotal--;
            ShowNext();
            return;
        }

        if (_scheduler is null)
            return;

        // 标记熟词（从 daily_study 移除，不计完成数）
        _progressRepo!.MarkKnown(_today, wordId);

        // 补位：追加 1 个新词（不在队列且非熟词）
        var passes = _progressRepo.GetDailyPasses(_today);
        var knownIds = _progressRepo.GetKnownWordIds();
        var newWord = _bookWords?.FirstOrDefault(w => !passes.ContainsKey(w.Id) && !knownIds.Contains(w.Id));
        if (newWord is not null)
        {
            _progressRepo.EnqueueStudyWord(_today, newWord.Id, false);
            _scheduler.Add(newWord.Id, 1);
            _wordById[newWord.Id] = newWord;
        }

        // 直接跳到下一个词（不调用 Complete，当前词自然出队）
        ShowNext();
    }

    private void UnmarkKnown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KnownWordRow row })
        {
            _progressRepo!.UnmarkKnown(row.Word.Id);
            RefreshKnownWords();
        }
    }

    private void KnownTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && (tag == "system" || tag == "user"))
        {
            _knownTab = tag;
            UpdateKnownTabButtons();
            RefreshKnownWords();
        }
    }

    /// <summary>按 _knownTab 更新熟词列表顶部 tab 的选中态（颜色取主题资源）。</summary>
    private void UpdateKnownTabButtons()
    {
        var activeBg = GetBrush("Brush.TabSelected");
        var inactiveBg = GetBrush("Brush.CardStrongBg");
        var activeFg = GetBrush("Brush.ButtonFg");
        var dimFg = GetBrush("Brush.Text.Secondary");

        SystemTabButton.Background = _knownTab == "system" ? activeBg : inactiveBg;
        SystemTabButton.Foreground = _knownTab == "system" ? activeFg : dimFg;
        UserTabButton.Background = _knownTab == "system" ? inactiveBg : activeBg;
        UserTabButton.Foreground = _knownTab == "system" ? dimFg : activeFg;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowHome();

    /// <summary>主题名（"dark"/"light"）。</summary>
    private string _theme = "dark";

    /// <summary>替换应用级主题资源字典（MergedDictionaries 中 Themes/ 目录下的条目），XAML 的 DynamicResource 自动刷新。</summary>
    private void ApplyTheme(string theme)
    {
        _theme = theme == "light" ? "light" : "dark";
        string source = _theme == "light" ? "Themes/Light.xaml" : "Themes/Dark.xaml";

        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            string s = merged[i].Source?.OriginalString ?? "";
            if (s.Contains("/Themes/"))
                merged.RemoveAt(i);
        }
        merged.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });

        RebuildWordDetail();
    }

    /// <summary>主题切换后重建学习页动态构造的内容（例句/同根词 Inlines 用的是旧主题画刷）。</summary>
    private void RebuildWordDetail()
    {
        if (_currentWordId < 0 || !_wordById.TryGetValue(_currentWordId, out var w))
            return;
        BuildExamplesList(w);
        if (w.RootId > 0)
        {
            var root = _wordRepo!.GetRoot(w.RootId);
            if (root is not null &&
                !string.Equals(root.Root, w.Spelling, StringComparison.OrdinalIgnoreCase))
                BuildRootFamily(w, root.Root);
        }
        BuildDerivationFamily(w);
        UpdateView();
    }

    private void UpdateThemeButtons()
    {
        SetTabStyle(DarkThemeButton, _theme == "dark");
        SetTabStyle(LightThemeButton, _theme == "light");
    }

    private void DarkTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_theme != "dark")
        {
            ApplyTheme("dark");
            _progressRepo!.SetSetting("theme", "dark");
            UpdateThemeButtons();
        }
    }

    private void LightTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_theme != "light")
        {
            ApplyTheme("light");
            _progressRepo!.SetSetting("theme", "light");
            UpdateThemeButtons();
        }
    }

    /// <summary>词书简称（"雅思核心" → "雅思"），用于首页紧凑展示。</summary>
    private static string ShortBookName(string name) => name.Replace("核心", "");

    /// <summary>进入词书选择列表视图。</summary>
    private void ShowBookSelectView()
    {
        _books = _wordRepo!.GetBooks();
        var learned = _progressRepo!.GetBookLearnedCounts();
        BookList.ItemsSource = _books
            .Select(b => new BookRow(b, b.Id == _currentBookId, learned.GetValueOrDefault(b.Id)))
            .ToList();

        BookSelectView.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        StudyView.Visibility = Visibility.Collapsed;
        KnownView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        ProgressText.Text = "";
    }

    /// <summary>首页「学习」旁的当前词书按钮：进入词书选择。</summary>
    private void SwitchBook_Click(object sender, RoutedEventArgs e) => ShowBookSelectView();

    /// <summary>词书列表项点击：切换当前词书并回首页。</summary>
    private void BookSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BookRow row })
            return;

        if (row.Book.Id != _currentBookId)
        {
            _currentBookId = row.Book.Id;
            _progressRepo!.SetCurrentBookId(_currentBookId);
            // 释放当天"尚未开始"（pass=0）的占位词，切换到新词书后由 StartStudy 补满当前组
            _progressRepo.DeleteUnstartedDailyWords(_today);
        }

        ShowHome();
    }

    private void ShowNext()
    {
        var scheduler = _reviewing ? _reviewScheduler : _scheduler;
        if (scheduler is null)
            return;

        var (wordId, pass) = scheduler.Next();
        if (wordId < 0)
        {
            // 会话完成态：只显示「完成！」+ 今日统计，隐藏全部内容/Tab/按钮
            _sessionComplete = true;
            _currentWordId = -1;
            _revealed = false;
            SpellingText.Text = _reviewing ? "复习完成！" : "完成！";
            PhoneticText.Text = "";
            PassText.Text = "";
            MorphText.Text = "";
            MeaningText.Text = _reviewing
                ? $"今日已复习 {_reviewDone}/{_reviewTotal}"
                : $"今日已学 {_completed}/{_currentGoal}";
            UpdateView();
            return;
        }

        _sessionComplete = false;
        _currentWordId = wordId;
        _currentPass = pass;
        _remembered = true;
        _revealed = !_reviewing && pass == 1; // 学习遍 1 直接展示详情；复习总是先"仅单词+音标"

        var w = _wordById[wordId];
        SpellingText.Text = w.Spelling;
        PhoneticText.Text = string.IsNullOrEmpty(w.Phonetic) ? "" : $"/{w.Phonetic}/";
        PassText.Text = _reviewing ? $"复习 第 {pass} 遍" : $"第 {pass} 遍";

        // 释义
        _hasMeaning = !string.IsNullOrWhiteSpace(w.BasicMeaning);
        MeaningText.Text = w.BasicMeaning;

        // 例句（全部展示，每条例句带「·」悬挂缩进，目标单词加粗高亮）
        BuildExamplesList(w);

        // 语素拆分（≥2 个语素才算"拆开"，单语素词如 chord/run 不显示）
        _hasMorph = w.Mnemonics.Root.Count >= 2;
        if (_hasMorph)
            MorphText.Text = string.Join(" · ", w.Mnemonics.Root);

        // 词根（主词根 + 同根词；词根=单词本身 或 无同根词时整个板块隐藏，宁缺毋滥）
        _hasRoot = false;
        RootText.Text = "";
        FamilyText.Text = "";
        if (w.RootId > 0)
        {
            var root = _wordRepo!.GetRoot(w.RootId);
            if (root is not null &&
                !string.Equals(root.Root, w.Spelling, StringComparison.OrdinalIgnoreCase))
            {
                RootText.Text = FormatRootLine(root.Root, root.Gloss, w);
                BuildRootFamily(w, root.Root); // 内部按同根词数量决定 _hasRoot
            }
        }

        // 派生词（从最老的根词开始展示整个派生家族）
        BuildDerivationFamily(w);

        // 词源
        _hasEtymology = !string.IsNullOrEmpty(w.Mnemonics.Etymology);
        if (_hasEtymology)
            EtymologyText.Text = w.Mnemonics.Etymology!;

        // 每个新单词默认回到例句 Tab（无数据的板块内容显示"暂无"）
        _activeTab = DetailTab.Example;

        UpdateView();
        PlayCurrentWord(); // 每次跳转到单词自动播放语音
        ScrollContentToTop(); // 进入新单词强制回到顶部，先看到单词本身
    }

    /// <summary>单词内容区滚动到最顶部（布局异步，用 Dispatcher 兜底再滚一次）。</summary>
    private void ScrollContentToTop()
    {
        ContentScroller.ScrollToTop();
        Dispatcher.BeginInvoke(new Action(ContentScroller.ScrollToTop), DispatcherPriority.Loaded);
    }

    /// <summary>学习面板右键菜单：复制单词 / 释义 / 例句（悬浮窗文本默认不可选中，用菜单提供复制）。</summary>
    private void InitContextMenu()
    {
        var menu = new ContextMenu();

        var copyWord = new MenuItem { Header = "复制单词" };
        copyWord.Click += (_, _) => CopyText(_wordById.TryGetValue(_currentWordId, out var w) ? w.Spelling : null);
        menu.Items.Add(copyWord);

        var copyMeaning = new MenuItem { Header = "复制释义" };
        copyMeaning.Click += (_, _) => CopyText(MeaningText.Text);
        menu.Items.Add(copyMeaning);

        var copyExamples = new MenuItem { Header = "复制例句" };
        copyExamples.Click += (_, _) => CopyExamples();
        menu.Items.Add(copyExamples);

        ContentScroller.ContextMenu = menu;
    }

    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            // 移除显示层注入的软连字符（U+00AD），保证复制出来的文本干净
            Clipboard.SetText(text.Replace("\u00AD", ""));
        }
        catch
        {
            // 剪贴板被其他进程占用时忽略
        }
    }

    private void CopyExamples()
    {
        if (!_wordById.TryGetValue(_currentWordId, out var w))
            return;
        var lines = w.Examples
            .Where(e => !string.IsNullOrWhiteSpace(e.En))
            .Select(e => string.IsNullOrWhiteSpace(e.Cn) ? e.En! : $"{e.En}\n{e.Cn}")
            .ToList();
        if (lines.Count > 0)
            CopyText(string.Join("\n\n", lines));
    }

    /// <summary>同根词串记：主词根 + 同族单词列表（排除自身）。</summary>
    /// <summary>同根词列表：派生去重后每词一行，单词内的词根高亮；无同根词时整个词根板块隐藏。</summary>
    private void BuildRootFamily(Word w, string root)
    {
        _hasRoot = false;
        FamilyText.Inlines.Clear();

        var family = _wordRepo!.GetWordsByRoot(w.RootId, w.Id);
        if (family.Count == 0)
            return;

        // 派生去重：去掉「被集合内其他词派生」的词（保留基础词），再按词频取前 10。
        // 集合包含当前词自身——当前词若是基础词，其派生词也要被去重。
        var ids = family.Select(f => f.Id).Append(w.Id).ToList();
        var derivedChildren = _wordRepo.GetDerivationEdgesAmong(ids)
            .Select(e => e.Child)
            .ToHashSet();
        var shown = family.Where(f => !derivedChildren.Contains(f.Id)).Take(10).ToList();
        if (shown.Count == 0)
            return;

        var highlight = GetBrush("Brush.Highlight");
        var muted = GetBrush("Brush.Text.Muted");

        bool first = true;
        foreach (var f in shown)
        {
            if (!first)
                FamilyText.Inlines.Add(new LineBreak());
            first = false;

            // 单词部分：词根在单词里的位置高亮
            int idx = f.Spelling.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                FamilyText.Inlines.Add(new Run(f.Spelling[..idx]));
                FamilyText.Inlines.Add(new Run(f.Spelling.Substring(idx, root.Length))
                {
                    Foreground = highlight,
                    FontWeight = FontWeights.Bold,
                });
                FamilyText.Inlines.Add(new Run(f.Spelling[(idx + root.Length)..]));
            }
            else
            {
                FamilyText.Inlines.Add(new Run(f.Spelling));
            }

            // 词性 + 首段释义（弱色）
            var def = f.Definitions.FirstOrDefault();
            string meaning = FirstMeaningSegment(def?.MeaningCn ?? "");
            if (meaning.Length > 0)
                FamilyText.Inlines.Add(new Run("  " + meaning) { Foreground = muted });
        }

        _hasRoot = true;
    }

    /// <summary>按 key 取应用级笔刷资源（找不到时抛错，便于发现资源缺失）。</summary>
    private SolidColorBrush GetBrush(string key) =>
        (SolidColorBrush)TryFindResource(key) ?? throw new InvalidOperationException($"missing brush: {key}");

    /// <summary>派生词板块：向上追溯最老的根词，从根开始层级缩进展示整个派生家族（限 3 层、15 词）。</summary>
    private void BuildDerivationFamily(Word w)
    {
        _hasDerv = false;
        DervText.Text = "";

        // 向上找最老的根词（word_id 从 1 开始；FirstOrDefault 对 int 无匹配时返回 0 而非 null，
        // 必须显式判 0，否则无父词的单词会死循环卡死 UI）
        int rootId = w.Id;
        var visited = new HashSet<int> { w.Id };
        while (true)
        {
            var parents = _wordRepo!.GetParentWordIds(rootId);
            int next = parents.FirstOrDefault(p => !visited.Contains(p));
            if (next == 0)
                break;
            rootId = next;
            visited.Add(rootId);
        }

        // 从根 BFS 收集层级（限 3 层、总 15 词）
        var levels = new List<List<Word>>();
        var roots = _wordRepo.GetWordsByIds(new[] { rootId });
        if (roots.Count == 0)
            return;
        var current = new List<Word> { roots[0] };
        int total = 0;
        for (int depth = 0; depth < 3 && current.Count > 0; depth++)
        {
            levels.Add(current);
            total += current.Count;
            if (total >= 15)
                break;
            var next = new List<Word>();
            foreach (var c in current)
            {
                foreach (var kid in _wordRepo.GetDirectDerivations(c.Id))
                {
                    if (total + next.Count >= 15)
                        break;
                    next.Add(kid);
                }
                if (total + next.Count >= 15)
                    break;
            }
            current = next;
        }

        // 只有自己一层：没有派生词，不显示
        if (levels.Count == 1 && levels[0].Count == 1)
            return;

        var sb = new StringBuilder();
        for (int d = 0; d < levels.Count; d++)
        {
            string indent = new(' ', d * 2);
            foreach (var lw in levels[d])
                sb.AppendLine(indent + FormatFamilyWord(lw));
        }
        DervText.Text = sb.ToString().TrimEnd();
        _hasDerv = true;
    }

    /// <summary>词根行：优先用当前词语素条目里的含义（roots 表共享 gloss 可能被同形异义词根污染，如 tin「小」vs「保持」）。</summary>
    private static string FormatRootLine(string root, string? gloss, Word w)
    {
        string? own = null;
        foreach (var entry in w.Mnemonics.Root)
        {
            if (entry.StartsWith(root + "（") && entry.EndsWith("）"))
            {
                own = entry[(root.Length + 1)..^1];
                break;
            }
        }
        return own is { Length: > 0 } ? $"{root}（{own}）" : $"{root}（{gloss}）";
    }

    private static readonly string[] PosPrefixes =
    {
        "n.", "v.", "adj.", "adv.", "prep.", "conj.", "pron.", "int.",
        "abbr.", "num.", "art.", "a.", "vi.", "vt.", "pl.",
    };

    /// <summary>同根词/派生词行格式：单词 + 词性 + 首段释义。MeaningCn 自带词性前缀时不重复显示 Pos（ECDICT 原始 pos 是 "n:19/v:81" 频次格式，不适合直出）。</summary>
    private static string FormatFamilyWord(Word f)
    {
        var def = f.Definitions.FirstOrDefault();
        string meaning = FirstMeaningSegment(def?.MeaningCn ?? "");
        string line = f.Spelling;
        if (!HasPosPrefix(meaning))
        {
            var parsed = ParsePos(def?.Pos);
            if (parsed is not null)
                line += " " + parsed;
        }
        if (meaning.Length > 0)
            line += " " + meaning;
        return line;
    }

    private static bool HasPosPrefix(string s) =>
        PosPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>解析 ECDICT pos 原始格式（"n:19/v:81" → "n."）。</summary>
    private static string? ParsePos(string? pos)
    {
        if (string.IsNullOrWhiteSpace(pos))
            return null;
        var first = pos.Split('/')[0].Split(':')[0].Trim();
        return first.Length == 0 ? null : first + ".";
    }

    /// <summary>取释义第一段：换行当分隔符，按中英分号/逗号/顿号切分，去掉 [医] 这类领域标签，截断超长。</summary>
    private static string FirstMeaningSegment(string meaning)
    {
        if (string.IsNullOrWhiteSpace(meaning))
            return "";
        meaning = meaning.Replace('\r', ' ').Replace('\n', '；');
        foreach (var sep in new[] { '；', ';', '，', ',', '、' })
        {
            int i = meaning.IndexOf(sep);
            if (i > 0)
                meaning = meaning[..i];
        }
        meaning = Regex.Replace(meaning, @"\[[^\]]*\]", " ");
        meaning = Regex.Replace(meaning, @"\s+", " ").Trim();
        if (meaning.Length > 14)
            meaning = meaning[..14];
        return meaning.Trim(' ', '.', '，', ',').Trim();
    }

    /// <summary>构造例句列表：每条例句「·」悬挂缩进，当前单词加粗高亮（含词形变化）。</summary>
    private void BuildExamplesList(Word w)
    {
        ExamplesList.Items.Clear();
        var examples = w.Examples.Where(e => !string.IsNullOrWhiteSpace(e.En)).ToList();
        _hasExample = examples.Count > 0;
        if (!_hasExample)
            return;

        var normalFg = GetBrush("Brush.Text.Primary");
        var cnFg = GetBrush("Brush.Text.Secondary");
        var highlight = GetBrush("Brush.Highlight"); // 例句单词高亮，与同根词词根高亮统一
        // 匹配目标单词及其词形变化（如 content → contented / contents）；
        // 显示层的长词会注入软连字符（U+00AD），故字符间允许出现软连字符
        var soft = "\u00AD?";
        var body = string.Join(soft, Regex.Escape(w.Spelling).Select(c => c.ToString()));
        var pattern = new Regex($@"\b{body}\w*\b", RegexOptions.IgnoreCase);

        foreach (var ex in examples)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new TextBlock
            {
                Text = "·",
                FontSize = 12,
                Foreground = normalFg,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var panel = new StackPanel();
            Grid.SetColumn(panel, 1);

            // 英文例句：长词注入软连字符（断行时自动用连字符拆词填满行尾），目标单词高亮加粗
            var enTb = new TextBlock
            {
                FontSize = 12,
                Foreground = normalFg,
                TextWrapping = TextWrapping.Wrap,
            };
            string en = InsertSoftHyphens(ex.En!);
            int last = 0;
            foreach (Match m in pattern.Matches(en))
            {
                if (m.Index > last)
                    enTb.Inlines.Add(new Run(en[last..m.Index]));
                enTb.Inlines.Add(new Run(m.Value) { Foreground = highlight, FontWeight = FontWeights.Bold });
                last = m.Index + m.Length;
            }
            if (last < en.Length)
                enTb.Inlines.Add(new Run(en[last..]));
            panel.Children.Add(enTb);

            // 中文翻译（有则显示）
            if (!string.IsNullOrWhiteSpace(ex.Cn))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = ex.Cn,
                    FontSize = 11,
                    Foreground = cnFg,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            }

            row.Children.Add(panel);
            ExamplesList.Items.Add(row);
        }
    }

    /// <summary>给例句文本的「长词」注入软连字符（U+00AD）：TextBlock 断行时自动用连字符拆词填满行尾，不断行时不可见。</summary>
    private static string InsertSoftHyphens(string text)
    {
        var parts = text.Split(' ');
        for (int i = 0; i < parts.Length; i++)
            parts[i] = InsertSoftHyphenInWord(parts[i]);
        return string.Join(" ", parts);
    }

    /// <summary>单词长度 ≥ 8 时在中间（微调到元音后）插 1 个软连字符；长度 ≥ 12 时再补首尾两个断点。</summary>
    private static string InsertSoftHyphenInWord(string word)
    {
        if (word.Length < 8)
            return word;

        const string vowels = "aeiouyAEIOUY";
        int Adjust(int p)
        {
            for (int d = 0; d < 3; d++)
            {
                int q = p + d;
                if (q > 2 && q < word.Length - 2 && vowels.Contains(word[q - 1]))
                    return q;
            }
            return Math.Clamp(p, 3, word.Length - 3);
        }

        var positions = new List<int> { Adjust(word.Length / 2) };
        if (word.Length >= 12)
        {
            positions.Add(Adjust(word.Length / 3));
            positions.Add(Adjust(word.Length * 2 / 3));
        }

        string result = "";
        int last = 0;
        int prev = -3;
        foreach (var p in positions.Distinct().OrderBy(p => p))
        {
            if (p - prev < 3 || word.Length - p < 2)
                continue;
            result += word[last..p] + "\u00AD";
            last = p;
            prev = p;
        }
        result += word[last..];
        return result;
    }

    private void UpdateView()
    {
        // 会话完成态：只显示「完成！」+ 今日统计卡片，其余（音标/语素/Tab/内容卡片/🔊/熟词/按钮）全部隐藏
        if (_sessionComplete)
        {
            PhoneticText.Visibility = Visibility.Collapsed;
            MorphText.Visibility = Visibility.Collapsed;
            PassText.Visibility = Visibility.Collapsed;
            MeaningBox.Visibility = Visibility.Visible;
            DetailTabBar.Visibility = Visibility.Collapsed;
            ExampleBox.Visibility = Visibility.Collapsed;
            DervBox.Visibility = Visibility.Collapsed;
            RootBox.Visibility = Visibility.Collapsed;
            EtymBox.Visibility = Visibility.Collapsed;
            AudioButton.Visibility = Visibility.Collapsed;
            KnownWordButton.Visibility = Visibility.Collapsed;
            HideAllButtons();
            return;
        }

        AudioButton.Visibility = Visibility.Visible;
        KnownWordButton.Visibility = Visibility.Visible;

        // 头部：释义卡片、语素拆分小字（只在"查看后"显示）
        MeaningBox.Visibility = _revealed && _hasMeaning ? Visibility.Visible : Visibility.Collapsed;
        MorphText.Visibility = _revealed && _hasMorph ? Visibility.Visible : Visibility.Collapsed;

        // 详情 Tab 行：已揭示后恒显示四个 Tab；选中态蓝底白字
        DetailTabBar.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        SetTabStyle(TabExampleButton, _activeTab == DetailTab.Example);
        SetTabStyle(TabDerivButton, _activeTab == DetailTab.Deriv);
        SetTabStyle(TabRootButton, _activeTab == DetailTab.Root);
        SetTabStyle(TabEtymButton, _activeTab == DetailTab.Etym);

        // 内容卡片：显示当前 Tab 对应板块；无数据时显示"暂无"
        bool showExample = _revealed && _activeTab == DetailTab.Example;
        ExampleBox.Visibility = showExample ? Visibility.Visible : Visibility.Collapsed;
        ExampleEmptyText.Visibility = showExample && !_hasExample ? Visibility.Visible : Visibility.Collapsed;
        ExamplesList.Visibility = showExample && _hasExample ? Visibility.Visible : Visibility.Collapsed;

        bool showDeriv = _revealed && _activeTab == DetailTab.Deriv;
        DervBox.Visibility = showDeriv ? Visibility.Visible : Visibility.Collapsed;
        DervEmptyText.Visibility = showDeriv && !_hasDerv ? Visibility.Visible : Visibility.Collapsed;
        DervText.Visibility = showDeriv && _hasDerv ? Visibility.Visible : Visibility.Collapsed;

        bool showRoot = _revealed && _activeTab == DetailTab.Root;
        RootBox.Visibility = showRoot ? Visibility.Visible : Visibility.Collapsed;
        RootEmptyText.Visibility = showRoot && !_hasRoot ? Visibility.Visible : Visibility.Collapsed;
        RootText.Visibility = showRoot && _hasRoot ? Visibility.Visible : Visibility.Collapsed;
        FamilyText.Visibility = showRoot && _hasRoot ? Visibility.Visible : Visibility.Collapsed;

        bool showEtym = _revealed && _activeTab == DetailTab.Etym;
        EtymBox.Visibility = showEtym ? Visibility.Visible : Visibility.Collapsed;
        EtymEmptyText.Visibility = showEtym && !_hasEtymology ? Visibility.Visible : Visibility.Collapsed;
        EtymologyText.Visibility = showEtym && _hasEtymology ? Visibility.Visible : Visibility.Collapsed;

        // 学习：第 2 遍"查看"、第 3 遍"记住了/没记住"；复习：第 1 遍"查看"、第 2 遍"记住了/没记住"
        int viewPass = _reviewing ? 1 : 2;
        int assessPass = _reviewing ? 2 : 3;

        NextButton.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        ViewButton.Visibility = !_revealed && _currentPass == viewPass ? Visibility.Visible : Visibility.Collapsed;
        RememberButton.Visibility = !_revealed && _currentPass == assessPass ? Visibility.Visible : Visibility.Collapsed;
        ForgotButton.Visibility = !_revealed && _currentPass == assessPass ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>详情 Tab 按钮选中态：选中蓝底白字，未选中透明底弱字。</summary>
    private void SetTabStyle(Button btn, bool selected)
    {
        btn.Background = selected ? GetBrush("Brush.TabSelected") : GetBrush("Brush.TabUnselected");
        btn.Foreground = selected ? GetBrush("Brush.ButtonFg") : GetBrush("Brush.Text.Secondary");
    }

    private void TabExample_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = DetailTab.Example;
        UpdateView();
        ScrollContentToTop();
    }

    private void TabDeriv_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = DetailTab.Deriv;
        UpdateView();
        ScrollContentToTop();
    }

    private void TabRoot_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = DetailTab.Root;
        UpdateView();
        ScrollContentToTop();
    }

    private void TabEtym_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = DetailTab.Etym;
        UpdateView();
        ScrollContentToTop();
    }

    private void HideAllButtons()
    {
        NextButton.Visibility = ViewButton.Visibility = RememberButton.Visibility = ForgotButton.Visibility = Visibility.Collapsed;
    }

    private void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        _revealed = true;
        UpdateView();
        PlayCurrentWord(); // 跳详情再放一次语音
    }

    private void RememberButton_Click(object sender, RoutedEventArgs e)
    {
        _remembered = true;
        _revealed = true;
        UpdateView();
        PlayCurrentWord(); // 跳详情再放一次语音
    }

    private void ForgotButton_Click(object sender, RoutedEventArgs e)
    {
        _remembered = false;
        _revealed = true;
        UpdateView();
        PlayCurrentWord(); // 跳详情再放一次语音
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        var scheduler = _reviewing ? _reviewScheduler : _scheduler;
        if (scheduler is null || _currentWordId < 0)
            return;

        if (_reviewing)
        {
            // 复习：第 2 遍自评，回写 SM-2 调度；今日已复习数按"去重词数"累计
            if (_currentPass == 2)
            {
                _progressRepo?.RecordReview(_currentWordId, _remembered);
                if (_reviewedToday.Add(_currentWordId))
                    _reviewDone++;
            }
            scheduler.Complete(_currentWordId, _currentPass, _remembered);

            string progress = $"{_reviewDone}/{_reviewTotal}";
            ProgressText.Text = progress;
            BubbleProgressText.Text = progress;
        }
        else
        {
            // 学习：第 3 遍"记住"计完成 + 进入复习队列
            if (_currentPass == 3 && _remembered)
            {
                _completed++;
                _progressRepo?.InitMemoryState(_currentWordId);
            }

            // 回写当日遍数：第 3 遍"没记住"不算过完，保持 2
            int pass = (_currentPass == 3 && !_remembered) ? 2 : _currentPass;
            _progressRepo?.RecordStudyPass(_today, _currentWordId, pass);

            scheduler.Complete(_currentWordId, _currentPass, _remembered);

            string progress = $"{_completed}/{_currentGoal}";
            ProgressText.Text = progress;
            BubbleProgressText.Text = progress;
        }

        ShowNext();
    }

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWordId < 0 || !_wordById.TryGetValue(_currentWordId, out var w))
            return;

        PlayWordAudio(w);
    }

    /// <summary>播放当前单词语音（每次页面跳转到单词时自动调用）。</summary>
    private void PlayCurrentWord()
    {
        if (_currentWordId >= 0 && _wordById.TryGetValue(_currentWordId, out var w))
            PlayWordAudio(w);
    }

    /// <summary>播放单词发音：有音频文件用 MediaPlayer 播放；否则用 Windows 系统 TTS 兜底朗读（离线）。</summary>
    private void PlayWordAudio(Word w)
    {
        string? path = null;
        if (!string.IsNullOrEmpty(w.AudioPath))
            path = ResourceLocator.Find(Path.Combine("resources", w.AudioPath!));

        if (path is not null)
        {
            try
            {
                _player?.Stop();
                _player = new MediaPlayer();
                _player.Open(new Uri(path));
                _player.Play();
                return;
            }
            catch
            {
                // 播放失败 → 落入 TTS 兜底
            }
        }

        PlayTts(w.Spelling);
    }

    /// <summary>Windows 系统 TTS 朗读（兜底，覆盖无音频文件/音频加载失败的词）。</summary>
    private void PlayTts(string spelling)
    {
        try
        {
            _tts ??= new SpeechSynthesizer();
            _tts.SpeakAsyncCancelAll();
            _tts.SpeakAsync(spelling);
        }
        catch
        {
            // 无 TTS 引擎等异常，静默忽略
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 忽略拖拽异常 */ }
    }

    /// <summary>气泡按下：DragMove 拖动窗口；释放后位移极小视为单击 → 展开主窗口。</summary>
    private void Bubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out var down);
        try { DragMove(); } catch { /* 忽略拖拽异常 */ }
        GetCursorPos(out var up);
        if (Math.Abs(up.X - down.X) + Math.Abs(up.Y - down.Y) < SystemParameters.MinimumHorizontalDragDistance)
            SetCollapsed(false);
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => SetCollapsed(false);

    private void SetCollapsed(bool collapsed)
    {
        ExpandedView.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedView.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Width = collapsed ? 46 : 240;
        Height = collapsed ? 46 : 320;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SetCollapsed(true); // 收起为气泡（退出程序唯一入口：托盘菜单「退出」）

    /// <summary>系统托盘图标：常驻任务栏右下角，双击展开面板，菜单「退出」是唯一退出入口。</summary>
    private void InitTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "FishWord 摸鱼背单词",
            Visible = true,
        };
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.ico"));
            if (sri is not null)
                _trayIcon.Icon = new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            // 图标加载失败时托盘仍可用（无图标显示）
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowExpandedPanel();
    }

    /// <summary>展开为完整面板并前置（气泡被遮挡时双击托盘图标找回）。</summary>
    private void ShowExpandedPanel()
    {
        SetCollapsed(false);
        Activate();
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }
}
