using AppKit;
using CoreGraphics;
using Foundation;

namespace Heartbeat.Desktop.Mac;

public sealed class MacWindow : NSWindow
{
    private readonly DesktopRuntime _runtime;
    private readonly MacSidebar _sidebar;
    private readonly MacConnectionView _connection;
    private readonly MacCollectionView _collection;
    private readonly NSTextField _sectionTitle;
    private readonly NSTextField _sectionSubtitle;
    private readonly NSStackView _messageBar;
    private readonly NSTextField _messageLabel;
    private readonly NSTimer _timer;
    private readonly List<NSObject> _observers = [];
    private bool _performing;
    private bool _quitting;
    private string? _actionError;
    private string? _shownMessage;
    private string _currentSection = string.Empty;

    public MacWindow(DesktopRuntime runtime)
        : base(new CGRect(0, 0, 740, 740),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false)
    {
        _runtime = runtime;
        Title = "Heartbeat Dev";
        TitleVisibility = NSWindowTitleVisibility.Hidden;
        TitlebarAppearsTransparent = true;
        MinSize = new CGSize(720, 740);
        Center();

        _sidebar = new MacSidebar();
        _sidebar.AddItem("collection", "采集状态", "waveform.path.ecg");
        _sidebar.AddItem("connection", "连接设置", "gearshape");
        _sidebar.SelectionChanged += id => SwitchSection(id);
        _sidebar.TimelineActivated += OpenReplay;
        _collection = new MacCollectionView(runtime, PerformAsync);
        _connection = new MacConnectionView(runtime, PerformAsync);
        _sectionTitle = MacControls.Label("采集状态", MacControls.Title2Font);
        _sectionSubtitle = MacControls.Secondary("关闭窗口后继续运行，可从菜单栏重新打开。");
        _messageLabel = MacControls.Label(string.Empty, MacControls.CalloutFont);
        _messageLabel.MaximumNumberOfLines = 2;
        var messageIcon = MacControls.Symbol("exclamationmark.triangle", "错误", 16);
        messageIcon.ContentTintColor = NSColor.SystemOrange;
        _messageBar = MacControls.HorizontalStack(8, messageIcon, _messageLabel);
        _messageBar.Hidden = true;
        _messageBar.AlphaValue = 0;

        InstallToolbar();
        InstallContentLayout();
        SwitchSection(runtime.Settings is null ? "connection" : "collection", false);
        WindowShouldClose = _ =>
        {
            OrderOut(null);
            Refresh();
            return false;
        };
        _timer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1), _ => Refresh());
        Observe(NSWindow.DidMiniaturizeNotification);
        Observe(NSWindow.DidDeminiaturizeNotification);
        Observe(new NSString("NSWindowDidChangeOcclusionStateNotification"));
        _observers.Add(NSWorkspace.SharedWorkspace.NotificationCenter.AddObserver(
            new NSString("NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification"),
            _ => BeginInvokeOnMainThread(Refresh)));
        Refresh();
    }

    private void Observe(NSString name) =>
        _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(name, _ => Refresh(), this));

    private void InstallToolbar()
    {
        Toolbar = new NSToolbar("heartbeat.navigation")
        {
            Delegate = new SidebarToolbar(this),
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
        };
        ToolbarStyle = NSWindowToolbarStyle.UnifiedCompact;
    }

    private void InstallContentLayout()
    {
        var content = new MacSurface(NSColor.WindowBackground);
        var right = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var column = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var header = MacControls.VerticalStack(6, _sectionTitle, _sectionSubtitle);
        column.AddSubview(header);
        column.AddSubview(_collection);
        column.AddSubview(_connection);
        column.AddSubview(_messageBar);
        right.AddSubview(column);
        content.AddSubview(_sidebar);
        content.AddSubview(right);
        NSLayoutConstraint.ActivateConstraints([
            _sidebar.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _sidebar.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _sidebar.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            right.LeadingAnchor.ConstraintEqualTo(_sidebar.TrailingAnchor),
            right.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            right.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            right.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            column.CenterXAnchor.ConstraintEqualTo(right.CenterXAnchor),
            column.LeadingAnchor.ConstraintGreaterThanOrEqualTo(right.LeadingAnchor, 24),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(560),
            column.TopAnchor.ConstraintEqualTo(right.TopAnchor, 20),
            column.BottomAnchor.ConstraintEqualTo(right.BottomAnchor, -14),
            header.LeadingAnchor.ConstraintEqualTo(column.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(column.TrailingAnchor),
            header.TopAnchor.ConstraintEqualTo(column.TopAnchor),
            _messageBar.LeadingAnchor.ConstraintEqualTo(column.LeadingAnchor),
            _messageBar.TrailingAnchor.ConstraintEqualTo(column.TrailingAnchor),
            _messageBar.BottomAnchor.ConstraintEqualTo(column.BottomAnchor),
            _messageBar.HeightAnchor.ConstraintEqualTo(36),
        ]);
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo(right.WidthAnchor, 1, -48);
        preferredWidth.Priority = 249;
        preferredWidth.Active = true;
        foreach (var view in new NSView[] { _collection, _connection })
        {
            NSLayoutConstraint.ActivateConstraints([
                view.LeadingAnchor.ConstraintEqualTo(header.LeadingAnchor),
                view.TrailingAnchor.ConstraintEqualTo(header.TrailingAnchor),
                view.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 20),
                view.BottomAnchor.ConstraintLessThanOrEqualTo(_messageBar.TopAnchor, -10),
            ]);
        }
        ContentView = content;
    }

    private void SwitchSection(string id, bool animated = true)
    {
        if (_currentSection == id) return;
        _currentSection = id;
        _sidebar.Select(id);
        var connection = id == "connection";
        _sectionTitle.StringValue = connection ? "连接设置" : "采集状态";
        _sectionSubtitle.StringValue = connection
            ? "验证并保存连接后即可开始采集。"
            : "关闭窗口后继续运行，可从菜单栏重新打开。";
        // Resign the outgoing field editor before hiding its owning page.
        MakeFirstResponder(null);
        MacAnimation.SetVisible(_collection, !connection, animated);
        MacAnimation.SetVisible(_connection, connection, animated, () =>
        {
            if (connection) MakeFirstResponder(_connection.InitialFocus);
            NSAccessibility.PostNotification(_sectionTitle, new NSString("AXLayoutChanged"));
        });
        RefreshIfReady();
    }

    private void RefreshIfReady()
    {
        if (_timer is not null) Refresh();
    }

    public void ToggleSidebar() => _sidebar.ToggleCollapsed();

    public async Task PerformAsync(Func<Task> action)
    {
        if (_quitting || _performing) return;
        _performing = true;
        _actionError = null;
        Refresh();
        string? error = null;
        try { await Task.Run(action); }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            BeginInvokeOnMainThread(() =>
            {
                _actionError = error;
                _performing = false;
                if (!_quitting) Refresh();
            });
        }
    }

    private void OpenReplay()
    {
        if (_runtime.Settings is not { } settings) return;
        using var url = new NSUrl(settings.WebUrl.AbsoluteUri);
        if (!NSWorkspace.SharedWorkspace.OpenUrl(url))
        {
            _actionError = "无法打开默认浏览器。";
            Refresh();
        }
    }

    private void Refresh()
    {
        var enabled = !_performing && !_runtime.IsBusy && !_quitting;
        var animate = !_quitting && IsVisible && !IsMiniaturized
            && (OcclusionState & NSWindowOcclusionState.Visible) != 0;
        _connection.Refresh(_runtime.Settings, enabled);
        _collection.Refresh(_runtime, enabled, animate && _currentSection == "collection");
        _sidebar.SetTimelineEnabled(_runtime.Settings is not null);
        var message = _actionError ?? _runtime.Error;
        if (message == _shownMessage) return;
        _shownMessage = message;
        if (message is not null)
        {
            _messageLabel.StringValue = message;
            _messageLabel.ToolTip = message;
        }
        MacAnimation.SetVisible(_messageBar, message is not null, animate);
    }

    public void PrepareToQuit()
    {
        _quitting = true;
        _timer.Invalidate();
        foreach (var observer in _observers) observer.Dispose();
        _observers.Clear();
        Refresh();
    }

    private sealed class SidebarToolbar(MacWindow owner) : NSToolbarDelegate
    {
        private const string Toggle = "sidebar-toggle";
        public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => [Toggle];
        public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => [Toggle];
        public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
        {
            var button = MacControls.Button("", owner.ToggleSidebar, "sidebar.left");
            button.ToolTip = "收起 / 展开侧边栏（⌥⌘S）";
            button.AccessibilityTitle = "收起或展开侧边栏";
            button.ImagePosition = NSCellImagePosition.ImageOnly;
            return new NSToolbarItem(Toggle) { Label = "侧边栏", View = button };
        }
    }
}
