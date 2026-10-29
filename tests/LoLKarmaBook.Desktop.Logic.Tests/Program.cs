using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Security;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Desktop.ViewModels;
using LoLKarmaBook.Storage;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;
using System.Text;
using LoLKarmaBook.Client;
using LoLKarmaBook.Hexdata;

var tests = new (string Name, Func<Task> Run)[]
{
    ("信息状态首次显示和关闭均通知提示条可见性", () =>
    {
        var status = new StatusMessageViewModel();
        var visibilityUpdates = new List<bool>();
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusMessageViewModel.HasMessage))
                visibilityUpdates.Add(status.HasMessage);
        };
        status.Show("首次信息提示", StatusSeverity.Info);
        Require(status.Severity == StatusSeverity.Info && visibilityUpdates.SequenceEqual(new[] { true }),
            "首次 Info 消息没有通知 HasMessage，提示条无法显示。");
        visibilityUpdates.Clear();
        status.Clear();
        Require(status.Severity == StatusSeverity.Info && visibilityUpdates.SequenceEqual(new[] { false }),
            "关闭 Info 消息没有通知 HasMessage，会残留空白提示条。");
        return Task.CompletedTask;
    }),
    ("名单选中高亮随玩家切换、实时重排和清空同步", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("selection-owner", "选择状态测试");
        MatchSession session = LiveSession(new SyntheticProvider().GetMatchA(), "selection-match", 1);
        client.Snapshot = Connected(account, session);
        await main.RefreshClientAsync();
        RosterRowViewModel[] rows = main.Current.Allies.Concat(main.Current.Enemies).ToArray();
        RosterRowViewModel first = rows.First(r => r.CanReceiveNote);
        RosterRowViewModel second = rows.First(r => r.CanReceiveNote && r != first);
        Require(rows.All(r => !r.IsSelected), "初始名单不应高亮任何玩家。");
        main.Current.RowSelectedCommand.Execute(first);
        Require(first.IsSelected && rows.Count(r => r.IsSelected) == 1, "点击玩家未同步唯一选中状态。");
        main.Current.RowSelectedCommand.Execute(second);
        Require(!first.IsSelected && second.IsSelected && rows.Count(r => r.IsSelected) == 1,
            "切换玩家后旧高亮未清除或新高亮未显示。");
        client.Snapshot = Connected(account, LiveSession(session, "selection-match", 2, reverse: true));
        await main.RefreshClientAsync();
        Require(ReferenceEquals(main.Current.SelectedParticipant, second) && second.IsSelected,
            "实时名单重排丢失了选中高亮。");
        main.Current.SelectedParticipant = null;
        Require(!main.Current.HasSelectedParticipant && rows.All(r => !r.IsSelected), "清空选择后仍遗留高亮。");
        main.Current.SelectedParticipant = second;
        await main.Current.ApplyLiveSnapshotAsync(null);
        Require(!main.Current.HasSelectedParticipant && !second.IsSelected, "清空名单后旧行仍保留选中状态。");
    })),
    ("实时账号自动关联、轮询保留草稿、断线禁写与账号空间隔离", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount firstAccount = Account("owner-one", "玩家甲");
        MatchSession original = LiveSession(new SyntheticProvider().GetMatchA(), "live-one", 1);
        client.Snapshot = Connected(firstAccount, original);
        await main.RefreshClientAsync();
        Require(main.CurrentProfileId == firstAccount.ProfileKey, "没有自动关联已登录账号的独立空间。");
        Require(main.ClientAccountText.Contains("玩家甲", StringComparison.Ordinal), "账号未显示在连接卡片。");
        var selected = main.Current.Allies.Single(r => r.ParticipantKey == SyntheticScenario.MatchAXParticipantKey);
        main.Current.SelectedParticipant = selected;
        main.Current.Categories.First().IsSelected = true;
        main.Current.NoteBody = "实时输入过程中不得因刷新丢失";
        client.Snapshot = Connected(firstAccount, LiveSession(original, "live-one", 2, reverse: true));
        await main.RefreshClientAsync();
        Require(ReferenceEquals(main.Current.SelectedParticipant, selected), "刷新重排后编辑目标被重建。");
        Require(main.Current.NoteBody == "实时输入过程中不得因刷新丢失" && main.Current.Categories.First().IsSelected,
            "刷新清空了输入或类别。");
        client.Snapshot = new ClientSnapshot(ClientConnectionStatus.CredentialsUnavailable, "parameters unavailable", null,
            null, GamePhase.Disconnected, DateTimeOffset.UtcNow);
        await main.RefreshClientAsync();
        Require(!main.Current.SaveCommand.CanExecute(null), "断线后仍可向过期对局写入记录。");
        Require(main.ClientStatusText.Contains("相同权限", StringComparison.Ordinal), "缺少连接参数时未提示客户端权限问题。");
        Require(main.Current.NoteBody.Length > 0, "断线清空了草稿。");
        client.Snapshot = Connected(firstAccount, original);
        await main.RefreshClientAsync();
        Require(main.Current.SaveCommand.CanExecute(null), "重新连接同一对局后无法继续编辑。");

        ClientAccount secondAccount = Account("owner-two", "玩家乙");
        client.Snapshot = Connected(secondAccount, LiveSession(original, "live-two", 1));
        await main.RefreshClientAsync();
        Require(main.CurrentProfileId == secondAccount.ProfileKey && (await main.Notebook.ListNotesAsync()).Count == 0,
            "不同账号混用了同一笔记空间。");
        client.Snapshot = Connected(firstAccount, original);
        await main.RefreshClientAsync();
        Require(main.Current.NoteBody == "实时输入过程中不得因刷新丢失", "切回账号时未恢复其独立草稿。");
    })),
    ("实时再遇创建持久收据且重复轮询和阶段切换只通知一次", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("reminder-owner", "提醒测试");
        client.Snapshot = Connected(account, null);
        await main.RefreshClientAsync();
        await main.Notebook.SaveNoteAsync(new SyntheticProvider().GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("historical-live-check", [NoteCategory.VerbalAbuse]));
        MatchSession session = LiveSession(new SyntheticProvider().GetMatchB(), "live-reminder", 1);
        client.Snapshot = Connected(account, session);
        await main.RefreshClientAsync();
        Require(main.Status.IsWarning && main.Status.Message.Contains("再次相遇", StringComparison.Ordinal), "首次再遇没有可见提醒。");
        Require(main.Current.Reminders.Count == 1, "实时历史卡片没有显示。");
        Require((await main.Notebook.EvaluateRemindersAsync(session)).Reminders.Count == 0, "首次检测没有持久化提醒收据。");
        main.Status.Clear();
        client.Snapshot = Connected(account, LiveSession(session, "live-reminder", 2));
        await main.RefreshClientAsync();
        Require(!main.Status.HasMessage && main.Current.Reminders.Count == 1, "重复轮询重复通知或清除了历史卡片。");
        client.Snapshot = Connected(account, LiveSession(session, "live-reminder", 3, external: "lcu:test:123"));
        await main.RefreshClientAsync();
        Require(!main.Status.HasMessage, "选人转入游戏获得matchId后重复通知。");
    })),
    ("再次相遇会触发右下角提示且只触发一次", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("toast-owner", "提示测试");
        client.Snapshot = Connected(account, null);
        await main.RefreshClientAsync();
        await main.Notebook.SaveNoteAsync(new SyntheticProvider().GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("toast-history", [NoteCategory.VerbalAbuse]));
        var announced = new List<IReadOnlyList<string>>();
        main.RemindersDetected += names => announced.Add(names);
        MatchSession session = LiveSession(new SyntheticProvider().GetMatchB(), "toast-reminder", 1);
        client.Snapshot = Connected(account, session);
        await main.RefreshClientAsync();
        Require(announced.Count == 1, "首次再遇没有触发右下角提示。");
        Require(announced[0].Count == 1 && !string.IsNullOrWhiteSpace(announced[0][0]),
            "提示里的玩家名不正确。");
        client.Snapshot = Connected(account, LiveSession(session, "toast-reminder", 2));
        await main.RefreshClientAsync();
        Require(announced.Count == 1, "重复轮询又弹了一次提示。");
    })),    ("赛后窗口只为有历史本局弹出且记录保存到当前空间", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-owner", "赛后测试");
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "eog-match", 1));
        await main.RefreshClientAsync();
        client.Snapshot = new ClientSnapshot(ClientConnectionStatus.Connected, "正在实时检测当前对局。", account,
            LiveSession(template, "eog-match", 2, phase: GamePhase.Finished), GamePhase.Finished, DateTimeOffset.UtcNow);
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen, "没有历史记录的对局也弹出了赛后窗口。");
        Require(main.HasMatchSnapshot && main.Current.CanMarkLastMatch, "赛后名单没有保留，无法手动标记。");
        Require(main.MarkLastMatchCommand.CanExecute(null), "赛后手动标记入口不可用。");
        await Run(main.MarkLastMatchCommand);
        Require(main.IsEndOfGameOpen && main.EndOfGame?.Players.Count > 0, "手动标记没有打开可选玩家的窗口。");
        EndOfGameViewModel window = main.EndOfGame!;
        window.SelectedPlayer = window.Players.First();
        window.Categories.First().IsSelected = true;
        await Run(window.SaveCommand);
        Require((await main.Notebook.ListNotesAsync()).Count == 1, "赛后窗口没有保存记录。");
        main.DismissEndOfGame();
        Require(!main.IsEndOfGameOpen, "关闭赛后窗口后仍保持打开状态。");

        {
            using MainViewModel.WorkspaceLease l = await main.AcquireWorkspaceAsync();
            await l.Notebook.SaveNoteAsync(new SyntheticProvider().GetMatchB(), SyntheticScenario.MatchBRenamedXParticipantKey,
                new NoteDraft("eog-history", [NoteCategory.VerbalAbuse]));
        }
        client.Snapshot = Connected(account, LiveSession(template, "eog-match-2", 3));
        await main.RefreshClientAsync();
        client.Snapshot = new ClientSnapshot(ClientConnectionStatus.Connected, "正在实时检测当前对局。", account,
            LiveSession(template, "eog-match-2", 4, phase: GamePhase.Finished), GamePhase.Finished, DateTimeOffset.UtcNow);
        await main.RefreshClientAsync();
        Require(main.IsEndOfGameOpen && main.EndOfGame!.Reminders.Count == 1, "遇到已记录玩家时没有弹出再遇记录。");
        main.DismissEndOfGame();
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen, "同一局重复轮询再次弹出了赛后窗口。");
    })),
    ("手动赛后窗口不被同一局活动阶段轮询关闭或重建", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-manual-refresh", "手动标记测试");
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "manual-stable", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel window = main.EndOfGame!;
        window.SelectedPlayer = window.Players.First();
        window.Categories.First().IsSelected = true;
        window.Body = "轮询时正在输入的赛后备注";
        int closeEvents = 0;
        main.EndOfGameChanged += next => { if (next is null) closeEvents++; };
        foreach (GamePhase phase in new[] { GamePhase.ChampionSelect, GamePhase.Loading, GamePhase.InGame })
        {
            client.Snapshot = Connected(account, LiveSession(template, "manual-stable", 2, phase: phase));
            await main.RefreshClientAsync();
        }
        Require(main.IsEndOfGameOpen && ReferenceEquals(main.EndOfGame, window) && closeEvents == 0,
            "同一局的活动阶段刷新关闭或重建了用户主动打开的窗口。");
        Require(window.Body == "轮询时正在输入的赛后备注" && window.Categories.First().IsSelected,
            "轮询丢失了手动标记的草稿。");
    })),
    ("下一局选人时标记上一局仍使用已结束名单与保存来源", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-previous-match", "上一局来源测试");
        client.Snapshot = Connected(account, LiveSession(new SyntheticProvider().GetMatchA(), "completed-old", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        client.Snapshot = Connected(account, LiveSession(new SyntheticProvider().GetMatchB(), "selecting-new", 2));
        await main.RefreshClientAsync();
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel window = main.EndOfGame!;
        Require(window.Players.All(p => p.LocalSessionId == "completed-old"), "上一局入口指向了正在选人的新局。");
        client.Snapshot = Connected(account, LiveSession(new SyntheticProvider().GetMatchB(), "selecting-new", 3, phase: GamePhase.InGame));
        await main.RefreshClientAsync();
        Require(ReferenceEquals(main.EndOfGame, window), "新局同一场的阶段刷新关闭了手动上一局窗口。");
        window.SelectedPlayer = window.Players.First();
        window.Categories.First().IsSelected = true;
        await Run(window.SaveCommand);
        StoredNote note = (await main.Notebook.ListNotesAsync()).Single();
        Require(note.SourceLocalSessionId == "completed-old" && note.ExternalMatchId == "lcu:test:completed-old",
            "赛后记录被写入正在进行的新局。");
    })),
    ("赛后入口与行为勾选会刷新界面命令状态", () => WithLiveMain(async (main, client) =>
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        main.EndOfGameNow = () => now;
        IAsyncCommand markCommand = main.Current.MarkLastMatchCommand;
        Require(!markCommand.CanExecute(null) && !main.Current.CanMarkLastMatch, "尚无名单时赛后入口已启用。");
        var markStates = new List<bool>();
        var visibleStates = new List<bool>();
        markCommand.CanExecuteChanged += (_, _) => markStates.Add(markCommand.CanExecute(null));
        main.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentMatchViewModel.CanMarkLastMatch))
                visibleStates.Add(main.Current.CanMarkLastMatch);
        };

        ClientAccount account = Account("eog-ui-owner", "赛后界面测试");
        client.Snapshot = Connected(account,
            LiveSession(new SyntheticProvider().GetMatchA(), "eog-ui-match", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        Require(markStates.LastOrDefault() && visibleStates.LastOrDefault(),
            "首次获得名单后未通知界面显示并启用赛后入口。");
        await Run(markCommand);

        EndOfGameViewModel window = main.EndOfGame!;
        window.SelectedPlayer = window.Players.First();
        var saveStates = new List<bool>();
        window.SaveCommand.CanExecuteChanged += (_, _) => saveStates.Add(window.SaveCommand.CanExecute(null));
        CategoryChoiceViewModel category = window.Categories.First();
        category.IsSelected = true; // Same path as the WPF CheckBox's two-way binding.
        Require(saveStates.Count == 1 && saveStates[^1], "选中玩家再勾选行为后，保存按钮未收到启用通知。");
        category.IsSelected = false;
        Require(saveStates.Count == 2 && !saveStates[^1], "取消最后一个行为后，保存按钮未收到禁用通知。");
        category.IsSelected = true;
        await Run(window.SaveCommand);
        Require((await main.Notebook.ListNotesAsync()).Count == 1 && !window.SaveCommand.CanExecute(null),
            "赛后保存未落库，或清空行为后仍允许重复保存。");

        client.Snapshot = Connected(account, null);
        await main.RefreshClientAsync();
        await main.RefreshClientAsync();
        Require(markStates[^1] && visibleStates[^1] && main.IsEndOfGameOpen,
            "大厅或短暂空快照误删了仍在标记窗口内的已结束名单。");
        now = now.AddMinutes(21);
        await main.RefreshClientAsync();
        Require(!markStates[^1] && !visibleStates[^1] && !markCommand.CanExecute(null),
            "20分钟窗口失效后未通知界面隐藏并禁用赛后入口。");
        Require(!main.IsEndOfGameOpen, "标记窗口到期后仍保持打开。");
    })),
    ("未观测到已结束局时不把当前选人或游戏冒充上一局", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-no-completed", "未结束局测试");
        foreach (GamePhase phase in new[] { GamePhase.ChampionSelect, GamePhase.Loading, GamePhase.InGame })
        {
            client.Snapshot = Connected(account, LiveSession(new SyntheticProvider().GetMatchA(), "only-current", 1, phase: phase));
            await main.RefreshClientAsync();
            Require(!main.HasMatchSnapshot && !main.Current.CanMarkLastMatch && !main.MarkLastMatchCommand.CanExecute(null),
                "未结束的当前局被当作上一局开放标记。");
        }
        Require(!await main.RequestEndOfGameAsync() && main.Status.Message.Contains("已结束对局", StringComparison.Ordinal),
            "没有结束名单时未给出清楚提示。");
    })),
    ("上一局20分钟从首次结束计时且重复结束轮询不会续期", () => WithLiveMain(async (main, client) =>
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        main.EndOfGameNow = () => now;
        ClientAccount account = Account("eog-expiry", "标记到期测试");
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "expires-once", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel window = main.EndOfGame!;
        window.SelectedPlayer = window.Players.First();
        window.Categories.First().IsSelected = true;
        now = now.AddMinutes(19);
        client.Snapshot = Connected(account, LiveSession(template, "expires-once", 2, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        Require(main.Current.CanMarkLastMatch && ReferenceEquals(main.EndOfGame, window), "20分钟内重复结束观察破坏了手动窗口。");
        now = now.AddMinutes(1);
        await main.RefreshClientAsync();
        Require(!main.HasMatchSnapshot && !main.IsEndOfGameOpen && !window.SaveCommand.CanExecute(null),
            "重复结束轮询延长了20分钟窗口或保留了旧窗口保存入口。");
        Require(!await main.RequestEndOfGameAsync(), "过期名单仍可通过手动请求打开。");
        await main.RefreshClientAsync();
        Require(!main.Current.CanMarkLastMatch, "过期对局在下一次相同结束轮询时重新获得有效期。");
    })),
    ("新局边界及账号空间切换关闭旧赛后窗并禁用旧保存命令", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-boundary", "边界测试");
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "boundary-old", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel old = main.EndOfGame!;
        old.SelectedPlayer = old.Players.First();
        old.Categories.First().IsSelected = true;
        client.Snapshot = Connected(account, LiveSession(template, "boundary-new", 2));
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen && !old.SaveCommand.CanExecute(null), "进入真正新局未关闭旧窗口或禁用其保存命令。");
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel reopened = main.EndOfGame!;
        await main.RefreshClientAsync();
        Require(ReferenceEquals(main.EndOfGame, reopened), "在新局内重新主动打开的上一局窗口又被轮询关闭。");
        // The demo space is intentionally hidden from the picker now, so the test
        // switches to a freshly created ordinary space instead.
        var otherSpace = new ProfileDescriptor(
            "eog-boundary-other-space", "边界测试空间", "测试用空间", isDemo: false);
        main.Profiles.Add(otherSpace);
        await main.SwitchProfileAsync(otherSpace);
        Require(!main.IsEndOfGameOpen && !main.HasMatchSnapshot && !reopened.SaveCommand.CanExecute(null),
            "切换空间保留了属于旧空间的赛后窗口。");
        await Run(main.RealtimeModeCommand);
        client.Snapshot = Connected(account, LiveSession(template, "boundary-new", 3, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        await Run(main.MarkLastMatchCommand);
        ClientAccount otherAccount = Account("eog-boundary-other", "其他账号");
        client.Snapshot = Connected(otherAccount, LiveSession(template, "other-current", 1));
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen && !main.HasMatchSnapshot && main.CurrentProfileId == otherAccount.ProfileKey,
            "账号切换后保留了旧账号上一局名单。");
        Require((await main.Notebook.ListNotesAsync()).Count == 0, "关闭边界窗口时意外写入了记录。");
    })),
    ("自动赛后窗按阶段关闭且主动打开后可保持到用户关闭", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("eog-prompt-kind", "弹窗来源测试");
        client.Snapshot = Connected(account, null);
        await main.RefreshClientAsync();
        await main.Notebook.SaveNoteAsync(new SyntheticProvider().GetMatchB(), SyntheticScenario.MatchBRenamedXParticipantKey,
            new NoteDraft("prompt-kind-history", [NoteCategory.VerbalAbuse]));
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "prompt-kind-one", 1, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        Require(main.IsEndOfGameOpen, "有历史的结束对局没有自动弹窗。");
        client.Snapshot = Connected(account, null);
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen && main.Current.CanMarkLastMatch, "自动窗离开赛后没有关闭，或错误清空了上一局。");
        await Run(main.MarkLastMatchCommand);
        EndOfGameViewModel manual = main.EndOfGame!;
        client.Snapshot = Connected(account, LiveSession(template, "prompt-kind-one", 2, phase: GamePhase.InGame));
        await main.RefreshClientAsync();
        Require(ReferenceEquals(main.EndOfGame, manual), "手动重新打开的窗口仍按自动窗规则误关。");
        main.DismissEndOfGame();
        client.Snapshot = Connected(account, LiveSession(template, "prompt-kind-one", 3, phase: GamePhase.Finished));
        await main.RefreshClientAsync();
        Require(!main.IsEndOfGameOpen, "用户关闭后同一结束局被重复自动打开。");
    })),
    ("演示空间不被实时账号覆盖且可一键返回实时", () => WithLiveMain(async (main, client) =>
    {
        await main.SwitchProfileAsync(DemoProfile(main));
        await Run(main.Current.ScenarioBCommand);
        client.Snapshot = Connected(Account("live-after-demo", "实时账号"), LiveSession(new SyntheticProvider().GetMatchA(), "live-after-demo", 1));
        await main.RefreshClientAsync();
        Require(main.IsDemoProfile && main.Current.IsScenarioB, "实时检测覆盖了用户主动选择的演示空间。");
        await Run(main.RealtimeModeCommand);
        Require(!main.IsDemoProfile && main.Current.Current?.SourceKind == SourceKind.Live, "实时对局按钮未恢复自动账号模式。");
    })),
    ("实时刷新不并发且停止时释放客户端", () => WithLiveMain(async (main, client) =>
    {
        client.DelayMilliseconds = 40;
        await Task.WhenAll(main.RefreshClientAsync(), main.RefreshClientAsync(), main.RefreshClientAsync());
        Require(client.PollCount == 1 && client.MaxConcurrent == 1, "手动检测与自动检测可能重叠。");
        main.StopRealtime();
        Require(client.IsDisposed, "停止实时检测后没有释放客户端。");
    })),
    ("不同实时对局各自保护草稿且切回时恢复", () => WithLiveMain(async (main, client) =>
    {
        ClientAccount account = Account("draft-context-owner", "草稿测试");
        MatchSession template = new SyntheticProvider().GetMatchA();
        client.Snapshot = Connected(account, LiveSession(template, "draft-match-one", 1));
        await main.RefreshClientAsync();
        main.Current.SelectedParticipant = main.Current.Allies.First(r => r.CanReceiveNote);
        main.Current.Categories.First().IsSelected = true;
        main.Current.NoteBody = "第一局未提交的草稿";
        client.Snapshot = Connected(account, LiveSession(template, "draft-match-two", 2));
        await main.RefreshClientAsync();
        Require(main.Current.NoteBody.Length == 0, "第二局意外继承了第一局的编辑内容。");
        main.Current.SelectedParticipant = main.Current.Allies.First(r => r.CanReceiveNote);
        main.Current.Categories.First().IsSelected = true;
        main.Current.NoteBody = "第二局另一个草稿";
        main.Current.FlushDraftOnClose();
        client.Snapshot = Connected(account, LiveSession(template, "draft-match-one", 3));
        await main.RefreshClientAsync();
        Require(main.Current.NoteBody == "第一局未提交的草稿", "第二局自动保存覆盖了第一局的受保护草稿。");
        Require(main.Current.SelectedParticipant?.IsSelected == true,
            "恢复草稿绕过手动选择时未恢复名单高亮。");
    })),
    ("标签搜索选择会随记录保存且编辑时恢复", () => WithMain(async (main, _, _) =>
    {
        await Run(main.Current.ScenarioACommand);
        main.Current.SelectedParticipant = main.Current.Allies.First(row => row.CanReceiveNote);
        main.Current.Categories.First().IsSelected = true;
        main.Current.TagQuery = "狐狸";
        Require(main.Current.TagSuggestions.Count > 0, "民间叫法没有搜到英雄候选。");
        main.Current.AcceptTopTagCommand.Execute(null);
        main.Current.TagQuery = "无尽";
        Require(main.Current.TagSuggestions.Count > 0, "装备名没有搜到候选。");
        main.Current.AcceptTopTagCommand.Execute(null);
        Require(main.Current.Tags.Count == 2, "选择后标签数量不对。");

        await Run(main.Current.SaveCommand);
        IReadOnlyList<StoredNote> notes = await main.Notebook.ListNotesAsync();
        Require(notes.Count == 1 && notes[0].Tags.Count == 2, "标签没有随记录一起写入。");
        Require(notes[0].Tags.Any(tag => tag.OfficialName == "九尾妖狐" && tag.Kind == TagKind.Champion),
            "英雄标签没有保存为官方条目。");

        bool opened = await main.Current.BeginEditAsync(notes[0]);
        Require(opened && main.Current.Tags.Count == 2, "编辑记录时没有恢复已保存的标签。");
        main.Current.RemoveTagCommand.Execute(main.Current.Tags.Last());
        await Run(main.Current.SaveCommand);
        IReadOnlyList<StoredNote> updated = await main.Notebook.ListNotesAsync();
        Require(updated.Count == 1 && updated[0].Tags.Count == 1, "移除标签后没有更新记录。");
    })),
    ("启动自动检测且停止后不再继续轮询", async () =>
    {
        var client = new FakeClient();
        await WithMain(async (main, _, _) =>
        {
            main.Start();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (client.PollCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Require(client.PollCount == 1, "启动没有自动检测客户端。");
            main.StopRealtime();
            await Task.Delay(2100);
            Require(client.PollCount == 1 && client.IsDisposed, "停止后自动循环仍继续调用客户端。");
        }, client, enableRealtime: true);
    }),
    ("旧设置保留偏好并移除声明确认字段", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "LoLKarmaBook-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{"schemaVersion":1,"lastProfileId":"synthetic-demo","defaultReminderDays":90,"disclaimerDismissed":true}""");
            DesktopSettings settings = DesktopSettings.Load(path);
            Require(settings.LastProfileId == "synthetic-demo" && settings.DefaultReminderDays == 90, "旧设置中的偏好未保留。");
            settings.Save(path);
            Require(!File.ReadAllText(path).Contains("disclaimerDismissed", StringComparison.Ordinal), "声明确认字段仍被写入。");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }),
    ("JSON 读取接受字节边界并保留 BOM 解码", () =>
    {
        using var boundary = new RepeatingReadStream(PerformanceJson.MaxBytes);
        Require(BoundedJsonFile.ReadText(boundary).Length == PerformanceJson.MaxBytes, "5 MiB 边界文件被拒绝或截断。");
        byte[] utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{\"备注\":\"测试\"}")).ToArray();
        using var bom = new MemoryStream(utf16);
        Require(BoundedJsonFile.ReadText(bom) == "{\"备注\":\"测试\"}", "BOM 编码未被正确识别。");
        return Task.CompletedTask;
    }),
    ("JSON 读取限制增长输入且只读上限加一字节", () =>
    {
        using var growing = new RepeatingReadStream(long.MaxValue);
        try
        {
            _ = BoundedJsonFile.ReadText(growing);
            throw new InvalidOperationException("超大输入未被拒绝。");
        }
        catch (ScoringValidationException ex)
        {
            Require(ex.Code == "scoring.file-too-large", "超限错误类型不正确。");
            Require(growing.BytesRead == PerformanceJson.MaxBytes + 1, "输入被超量读取。");
        }
        return Task.CompletedTask;
    }),
    ("导入文件选择器在调用方 UI 同步上下文执行", () => WithMain(async (main, _, _) =>
    {
        var expectedContext = new SynchronizationContext();
        SynchronizationContext? observedContext = null;
        main.Performance.PickJsonFile = _ =>
        {
            observedContext = SynchronizationContext.Current;
            var demo = LoLKarmaBook.Scoring.SyntheticScenarioGenerator.CreatePerformanceDemo();
            return ("fixture.json", PerformanceJson.Export(new PerformanceDataset { Source = "live", Matches = demo.Matches }));
        };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task import;
        try
        {
            SynchronizationContext.SetSynchronizationContext(expectedContext);
            import = Run(main.Performance.ImportCommand);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        await import;
        Require(ReferenceEquals(expectedContext, observedContext), "文件选择器被移到了后台线程。");
        Require(main.Performance.Score is { GameCount: 12 }, "有效文件未完成计算：" + main.Performance.ErrorText);
        Require(main.Performance.DatasetMeta.Contains("live", StringComparison.Ordinal), "导入来源标签未保留到界面。");
    })),
    ("估算 ELO 需要队伍名单：有名单才显示，无名单只显示表现分", () => WithMain(async (main, _, _) =>
    {
        // Without a roster the page still scores performance but shows no estimate.
        main.Performance.PickJsonFile = _ => ("performance-only.json",
            PerformanceJson.Export(new PerformanceDataset
            {
                Source = "live",
                Matches = LoLKarmaBook.Scoring.SyntheticScenarioGenerator.CreatePerformanceDemo().Matches
            }));
        await Run(main.Performance.ImportCommand);
        Require(main.Performance.Score is { GameCount: 12 }, "无名单数据未完成表现分：" + main.Performance.ErrorText);
        Require(!main.Performance.HasEstimate, "没有队伍名单却显示了估算 ELO。");

        // With the roster the estimate appears, rounded to 0.05 and inside the gauge.
        var (dataset, _) = LoLKarmaBook.Scoring.SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo();
        main.Performance.PickJsonFile = _ => ("with-roster.json", PerformanceJson.Export(new PerformanceDataset
        {
            Source = "live", Matches = dataset.Matches, Roster = dataset.Roster, PlayerId = dataset.PlayerId
        }));
        await Run(main.Performance.ImportCommand);
        Require(main.Performance.HasEstimate, "有名单却没有估算 ELO：" + main.Performance.ErrorText);
        Require(main.Performance.Estimate!.Rating is > 1000 and < 2000, "估算分数超出合理区间：" + main.Performance.EstimateText);
        Require(main.Performance.DatasetMeta.Contains("live", StringComparison.Ordinal), "导入来源标签未保留。");
        Require(main.Performance.EstimateDetailText.Contains("团队 Elo", StringComparison.Ordinal), "估算未展示构成。");
    })),
    ("桌面保存 A 局并在 B 局识别同一账号", () => WithMain(async (main, _, _) =>
    {
        await Run(main.Current.ScenarioACommand);
        main.Current.SelectedParticipant = main.Current.Allies.Single(r => r.ParticipantKey == SyntheticScenario.MatchAXParticipantKey);
        main.Current.Categories.First().IsSelected = true;
        main.Current.NoteBody = "监督测试：只保存在临时合成空间";
        await Run(main.Current.SaveCommand);
        Require((await main.Notebook.ListNotesAsync()).Count == 1, "点击保存没有持久化记录。");
        await Run(main.Current.ScenarioBCommand);
        Require(main.Current.Reminders.Count == 1, "B 局应只匹配一个改名账号。");
    })),
    ("快速切换空间后选择器与数据库一致", () => WithMain(async (main, _, _) =>
    {
        using var lease = await main.AcquireWorkspaceAsync();
        var first = main.SwitchProfileAsync(DemoProfile(main));
        var second = main.SwitchProfileAsync(main.Profiles.Single(p => p.Id == ProfileCatalog.DefaultProfileId));
        lease.Dispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Require(main.IsProfileReady, "切换后空间未打开。");
        Require(main.SelectedProfile?.Id == main.CurrentProfileId, "选择器显示的空间与实际库不一致。");
        Require(main.CurrentProfileId == ProfileCatalog.DefaultProfileId, "最后一次切换请求未生效。");
    })),
    ("恢复不同 HMAC 密钥的同空间备份后重新识别", () => WithMain(async (main, root, protector) =>
    {
        var backup = Path.Combine(root, "other-generation.db");
        await using (var other = await SqliteProfileStore.OpenDatabaseAsync(backup, main.CurrentProfileId, protector))
        {
            await new NotebookService(other).SaveNoteAsync(new SyntheticProvider().GetMatchA(),
                SyntheticScenario.MatchAXParticipantKey, new NoteDraft("restored", [NoteCategory.AfkLeave]));
        }
        string lookupKey;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key='identity_lookup_key'";
            lookupKey = (string)command.ExecuteScalar()!;
        }
        bool observedValidation = false;
        protector.BeforeUnprotect = value =>
        {
            if (value != lookupKey || observedValidation) return;
            observedValidation = true;
            Require(!main.IsProfileReady, "校验待恢复备份时，目标库仍处于打开状态。");
            Require(main.IsMaintenanceBusy && !main.CanChangeProfile, "恢复期间未锁定空间切换。");
        };
        main.Settings.PickBackupFile = _ => backup;
        Confirmation.Handler = (_, _, _) => true;
        await Run(main.Settings.RestoreSnapshotCommand);
        protector.BeforeUnprotect = null;
        Require(observedValidation, "未观察到待恢复备份的解密校验。");
        Require(main.Settings.MaintenanceSeverity == "success", "恢复未成功：" + main.Settings.MaintenanceMessage);
        await Run(main.Current.ScenarioBCommand);
        Require(main.Current.Reminders.Count == 1, "恢复后使用了旧的身份查找密钥。");
    })),
    ("维护期间拒绝空间选择且保持实际选择一致", () => WithMain(async (main, _, _) =>
    {
        var original = main.CurrentProfileId;
        await main.RunMaintenanceAsync("测试维护", _ =>
        {
            main.SelectedProfile = DemoProfile(main);
            Require(main.SelectedProfile?.Id == original && main.CurrentProfileId == original,
                "维护期间选择器与数据库不一致。");
            return Task.CompletedTask;
        });
        Require(!main.IsMaintenanceBusy, "维护状态未释放。");
    })),
    ("拒绝其他空间备份且保留原记录", () => WithMain(async (main, root, protector) =>
    {
        await main.Notebook.SaveNoteAsync(new SyntheticProvider().GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("keep", [NoteCategory.VerbalAbuse]));
        var backup = Path.Combine(root, "wrong-profile.db");
        await using (var other = await SqliteProfileStore.OpenDatabaseAsync(backup, "other-space", protector)) { }
        main.Settings.PickBackupFile = _ => backup;
        Confirmation.Handler = (_, _, _) => true;
        await Run(main.Settings.RestoreSnapshotCommand);
        Require(main.Settings.MaintenanceSeverity == "error", "跨空间恢复未被拒绝。");
        Require(main.IsProfileReady && (await main.Notebook.ListNotesAsync()).Count == 1, "拒绝恢复后原记录不可用。");
    })),
    ("提醒有效期可以手填天数并支持永久", () => WithMain((main, _, _) =>
    {
        SettingsViewModel settings = main.Settings;
        Require(settings.DefaultReminderDays == 365 && settings.ReminderDaysText == "365", "默认值不是 365 天。");
        settings.ReminderDaysText = "45";
        Require(settings.DefaultReminderDays == 45, "手填天数没有生效。");
        settings.ReminderDaysText = "abc";
        Require(settings.DefaultReminderDays == 45, "非法输入改写了天数。");
        Require(settings.ReminderDaysHint.Contains("1–3650", StringComparison.Ordinal), "非法输入没有提示范围。");
        settings.IsPermanentReminder = true;
        Require(settings.DefaultReminderDays == 0 && !settings.CanEditReminderDays, "永久没有把天数置 0 或没有禁用输入。");
        Require(settings.ReminderDaysText.Length == 0, "永久时仍显示天数。");
        settings.IsPermanentReminder = false;
        Require(settings.DefaultReminderDays == 365 && settings.ReminderDaysText == "365", "取消永久没有回到默认 365 天。");
        settings.ReminderDaysText = "100";
        settings.SavePreferencesCommand.Execute(null);
        Require(main.PersistedSettings.DefaultReminderDays == 100, "保存后偏好没有更新。");
        return Task.CompletedTask;
    })),
    ("Hexdata 登录、查分和双方名单解析", async () =>
    {
        var handler = new HexdataStub();
        using var client = new HexdataClient(handler);
        await client.RequestCodeAsync("Player@Example.com");
        string token = await client.VerifyCodeAsync("Player@Example.com", "123456");
        Require(token == "token-1", "验证码登录没有返回会话令牌。");
        HexdataAccount? account = await client.GetAccountAsync(token);
        Require(account?.Email == "player@example.com" && account.IsActive, "账号信息解析不正确。");
        Require(await client.GetAccountAsync("expired") is null, "失效令牌没有被识别。");
        HexdataReport report = await client.QueryReportAsync(token, "Name#1234", useBoundIdentity: false);
        Require(report.Estimate?.Score == 1234, "海斗分没有解析出来。");
        Require(report.Estimate!.Percentile == 62.5, "百分位换算不正确。");
        Require(report.RecentMatches.Count == 1 && report.RecentMatches[0].TargetWin == true, "最近对局没有解析。");
        Require(report.RecentMatches[0].Teams.Count == 2
                && report.RecentMatches[0].Teams.All(t => t.Participants.Count == 5),
            "双方名单没有解析。");
        Require(handler.LastReportBody!.Contains("\"gameId\":\"Name#1234\"", StringComparison.Ordinal),
            "查询没有带上游戏 ID。");
    }),
    ("Hexdata 每份报告都会备份并可离线读取", () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "hexdata-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new HexdataReportCache(root);
            HexdataReport report = HexdataReport.Parse(HexdataStub.ReportJson);
            HexdataSavedReport saved = cache.Save(report, "我的账号", DateTimeOffset.UtcNow);
            Require(File.Exists(Path.Combine(cache.ReportsDirectory, saved.FileName)), "备份文件没有写出。");
            IReadOnlyList<HexdataSavedReport> list = cache.List();
            Require(list.Count == 1 && list[0].Score == 1234, "索引没有记录分数。");
            HexdataReport? reloaded = cache.Load(saved.FileName);
            Require(reloaded is not null && reloaded.RecentMatches.Count == 1, "离线读取备份失败。");
            Require(reloaded!.RecentMatches[0].Teams[0].Participants.Count == 5, "离线备份丢了双方名单。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }),
    ("Hexdata 历史列表一次只展开一条并绘制走势", async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "hexdata-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new HexdataReportCache(root);
            HexdataReport report = HexdataReport.Parse(HexdataStub.ReportJson);
            var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            cache.Save(report, "我的账号", start);
            cache.Save(report, "我的账号", start.AddDays(1));
            cache.Save(report, "我的账号", start.AddDays(2));

            using var protector = new DpapiTextProtector();
            var vm = new HexdataViewModel(root, protector);
            Require(vm.History.Count == 3, "本地备份没有全部进入历史列表。");
            Require(vm.History.All(row => !row.IsExpanded), "历史条目默认应全部收起。");
            Require(vm.Chart.HasPoints && vm.Chart.Dots.Count == 3, "三条分数没有绘制成走势。");
            Require(vm.Chart.HasLine, "多条记录没有连成折线。");
            Require(vm.Chart.XLabels.Count >= 2, "横轴没有记录时间刻度。");
            Require(vm.Chart.YLabels.Count >= 1, "纵轴没有分数刻度。");
            Require(vm.Chart.Dots.All(dot => dot.Tooltip.Contains("海斗分 1234", StringComparison.Ordinal)),
                "圆点悬停提示没有分数。");
            Require(vm.Chart.Dots[0].Tooltip.Contains("2026-", StringComparison.Ordinal),
                "圆点悬停提示没有记录时间。");
            Require(vm.Chart.MinimumText.Contains('1', StringComparison.Ordinal), "走势缺少最低分。");

            await Run(vm.History[0].ToggleCommand);
            Require(vm.History[0].IsExpanded, "点击条目没有展开。");
            await Run(vm.History[1].ToggleCommand);
            Require(vm.History[1].IsExpanded && !vm.History[0].IsExpanded, "展开另一条时应自动收起上一条。");
            await Run(vm.History[1].ToggleCommand);
            Require(!vm.History[1].IsExpanded, "再次点击没有折叠。");

            string before = vm.ChartScopeText;
            vm.ToggleChartScopeCommand.Execute(null);
            Require(vm.ChartScopeText != before, "切换统计范围没有生效。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }),
    ("标签目录缓存在 userdata/tags 下并可离线复用", async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "tag-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new TagCatalogService(root);
            Require(service.CachePath == Path.Combine(root, "tags", "catalog.json"),
                "标签缓存没有放在 userdata/tags 下。");
            Require(service.Entries.Count > 500, "内置标签目录没有加载。");
            Require(service.Search("狐狸").Any(entry => entry.OfficialName == "九尾妖狐"),
                "内置目录无法用民间叫法搜索。");

            bool refreshed = await service.RefreshAsync(new FakeTagSource());
            Require(refreshed && File.Exists(service.CachePath), "实时目录没有写入缓存。");
            var reopened = new TagCatalogService(root);
            Require(reopened.Entries.Any(entry => entry.OfficialName == "测试英雄"),
                "下次启动没有从缓存恢复实时目录。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }),
    ("Hexdata 单条记录显示一个圆点且不画折线", async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "hexdata-one-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new HexdataReportCache(root);
            HexdataReport report = HexdataReport.Parse(HexdataStub.ReportJson);
            cache.Save(report, "我的账号", new DateTimeOffset(2026, 10, 5, 8, 30, 0, TimeSpan.Zero));
            using var protector = new DpapiTextProtector();
            var vm = new HexdataViewModel(root, protector);
            Require(vm.Chart.HasPoints && vm.Chart.Dots.Count == 1, "单条记录没有画出圆点。");
            Require(!vm.Chart.HasLine, "单条记录不应绘制折线。");
            Require(vm.Chart.XLabels.Count == 1 && vm.Chart.YLabels.Count == 1,
                "单条记录的坐标刻度不完整。");
            Require(vm.Chart.Dots[0].Tooltip.Contains("1234", StringComparison.Ordinal), "单点提示没有分数。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        await Task.CompletedTask;
    }),
    ("设置页打开空间后显示数据库路径且维护命令可用", () => WithMain((main, _, _) =>
    {
        main.SelectTab("settings");
        Require(main.Settings.CurrentDatabasePath.Contains("lolkarmabook.db", StringComparison.OrdinalIgnoreCase),
            "设置页没有显示已经打开的数据库路径。");
        Require(main.Settings.ExportSnapshotCommand.CanExecute(null), "打开空间后导出快照仍不可用。");
        Require(main.Settings.RestoreSnapshotCommand.CanExecute(null), "打开空间后从快照恢复仍不可用。");
        Require(main.Settings.ClearProfileCommand.CanExecute(null), "打开空间后清空当前空间仍不可用。");
        return Task.CompletedTask;
    })),
    ("Hexdata 查询记录里的英雄名显示为中文", async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "hexdata-cn-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string json = """
            {"report":{"resolvedGameId":"Name#1234","regionKey":"hn1","recentMatches":[
              {"gameId":"g1","startedAt":1793120996467,
               "targetMatchSummary":{"championId":103,"championName":"Ahri","kills":1,"deaths":2,"assists":3},
               "teams":[{"side":"blue","estimateCoverage":{"scored":1,"total":1},
                 "participants":[{"publicGameId":"P1#1","championId":103,"championName":"Ahri","isTarget":true}]}]}]}}
            """;
            var cache = new HexdataReportCache(root);
            cache.Save(HexdataReport.Parse(json), "我的账号", new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            using var protector = new DpapiTextProtector();
            var tags = new TagCatalogService(root);
            var vm = new HexdataViewModel(root, protector, tags: tags);
            HexdataHistoryRowViewModel row = vm.History.Single();
            Require(row.Matches[0].ChampionText == "九尾妖狐",
                $"查询记录里的英雄名没有转成中文：{row.Matches[0].ChampionText}");
            Require(row.Matches[0].Teams[0].Participants[0].Champion == "九尾妖狐",
                "双方名单里的英雄名没有转成中文。");
            Require(row.Matches[0].Teams.Count == 1, "对局没有保留双方名单。");
            row.Matches[0].ToggleCommand.Execute(null);
            Require(row.Matches[0].IsExpanded, "对局行没有展开双方名单。");
            Require(row.Matches[0].ChevronText == "▼", "展开后箭头没有变化。");
            string expectedWhen = DateTimeOffset.FromUnixTimeMilliseconds(1793120996467)
                .ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Require(row.Matches[0].WhenText == expectedWhen,
                $"毫秒时间戳没有解析成对局时间：{row.Matches[0].WhenText}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        await Task.CompletedTask;
    }),
    ("记录在没有对局名单时也能单独编辑", () => WithMain(async (main, _, _) =>
    {
        try
        {
            var scenario = new SyntheticScenario();
            StoredNote note = await main.Notebook.SaveNoteAsync(scenario.MatchA, SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft("offline-edit-1", new[] { NoteCategory.OddBuild }, "原始备注"));
            var edit = new NoteEditViewModel(main, note);
            edit.Body = "改过的备注";
            edit.Categories.First(c => c.Category == NoteCategory.OddBuild).IsSelected = true;
            await Run(edit.SaveCommand);
            Require(edit.WasSaved, "单独编辑没有保存成功。");
            StoredNote? reloaded = await main.Notebook.GetNoteAsync(note.NoteId);
            Require(reloaded is not null && reloaded.Body == "改过的备注" && reloaded.Revision == note.Revision + 1,
                "单独编辑没有写入备注或版本。");
        }
        catch (Exception ex)
        {
            Require(false, ex.ToString());
        }
    })),    ("设置写入失败不会显示成功", () => WithMain((main, _, _) =>
    {
        var path = main.Paths.SettingsFile;
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path); // reliably prevents replacing settings even under root.
        main.Settings.SavePreferences();
        Require(main.Status.Severity is StatusSeverity.Warning or StatusSeverity.Error, "保存失败后显示了成功状态。");
        return Task.CompletedTask;
    })),
    ("中国大陆免 Key 读取客户端，队列和输入按请求快照固定", () => WithMain(async (main, _, _) =>
    {
        var history = new FakeHistoryService();
        main.Performance.CreateLocalHistoryService = () => history;
        main.Performance.CreateRiotClient = _ => throw new InvalidOperationException("大陆查询不应使用 Riot Key。");
        Require(main.Performance.IsMainlandPlatform && main.Performance.IsRiotPlatform == false, "新用户没有默认选择中国大陆。");
        Require(main.Performance.ApiKey.Length == 0 && main.Performance.CanFetchRiot, "大陆查询仍需要 Key 或 Riot ID。");
        main.PersistedSettings.ClientDirectory = @"D:\LOL-client";
        main.Performance.SelectedQueue = PerformanceViewModel.Queues.Single(q => q.QueueId == 2400);
        main.Performance.SelectedCount = PerformanceViewModel.Counts.Single(c => c.Count == 10);
        Task fetch = Run(main.Performance.FetchRiotCommand);
        await history.Started.Task;
        Require(!main.Performance.CanEditLookup, "查询中输入区没有禁用。");
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");
        main.Performance.SelectedQueue = PerformanceViewModel.Queues.Single(q => q.QueueId == 420);
        main.Performance.SelectedCount = PerformanceViewModel.Counts.Single(c => c.Count == 50);
        main.PersistedSettings.ClientDirectory = @"D:\another-client";
        history.Completion.SetResult(LocalHistorySuccess("lcu-history:HN1:2400"));
        await fetch;
        Require(history.Directory == @"D:\LOL-client" && history.Count == 10 && history.QueueId == 2400,
            "查询使用了等待期间变化后的目录、局数或队列。");
        Require(main.Performance.Score is { GameCount: 3 } && main.Performance.Estimate is not null, "客户端对局没有进入同一评分流程。");
        Require(main.Performance.RiotSourceText.Contains("中国大陆", StringComparison.Ordinal)
                && main.Performance.RiotSourceText.Contains("海克斯大乱斗", StringComparison.Ordinal), "结果没有标明本次固定的大陆和海克斯队列。");
        Require(history.IsDisposed && main.Performance.CanEditLookup, "查询结束没有释放客户端或恢复输入。");
    })),
    ("客户端账号切换时不应用历史结果", () => WithMain(async (main, _, _) =>
    {
        var history = new FakeHistoryService();
        main.Performance.CreateLocalHistoryService = () => history;
        Task fetch = Run(main.Performance.FetchRiotCommand);
        await history.Started.Task;
        history.Completion.SetResult(new LcuHistoryResult(LcuHistoryStatus.AccountChanged, "changed", Account("other", "其他账号"),
            LocalHistorySuccess("lcu-history:HN1:all").Dataset, 20, 0));
        await fetch;
        Require(!main.Performance.HasResult && main.Performance.ErrorText.Contains("账号发生变化", StringComparison.Ordinal),
            "账号变化后仍应用了旧账号的查询结果。");
    })),
    ("取消客户端查询后丢弃迟到结果", () => WithMain(async (main, _, _) =>
    {
        var history = new FakeHistoryService();
        main.Performance.CreateLocalHistoryService = () => history;
        Task fetch = Run(main.Performance.FetchRiotCommand);
        await history.Started.Task;
        main.Performance.CancelRiotCommand.Execute(null);
        Require(history.Token.IsCancellationRequested, "取消没有传递给客户端请求。");
        history.Completion.SetResult(LocalHistorySuccess("lcu-history:HN1:all"));
        await fetch;
        Require(!main.Performance.HasResult && main.Status.Message.Contains("取消", StringComparison.Ordinal), "取消后迟到的结果覆盖了页面。");
        Require(history.IsDisposed && !main.Performance.IsFetching, "取消后查询资源未释放。");
    })),
    ("退出后忽略取消的客户端迟到结果不更新页面或写回设置", async () =>
    {
        foreach (bool existingSettings in new[] { false, true })
        {
            await WithMain(async (main, _, _) =>
            {
                await Run(main.Performance.LoadDemoCommand);
                var originalEstimate = main.Performance.Estimate;
                string originalPath = main.Performance.LastImportPath;
                string settingsPath = main.RiotKeys.FilePath;
                if (existingSettings)
                    main.RiotKeys.Save(null, new RiotLookupPreferences(null, "KR", "Existing#KR1", 10, 440));
                byte[]? originalSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                var history = new FakeHistoryService(); // Completion deliberately ignores the cancellation token.
                main.Performance.CreateLocalHistoryService = () => history;

                Task fetch = Run(main.Performance.FetchRiotCommand);
                await history.Started.Task;
                await main.DisposeAsync();
                bool canceledOnExit = history.Token.IsCancellationRequested;
                history.Completion.SetResult(LocalHistorySuccess("lcu-history:HN1:2400"));
                await fetch;

                Require(canceledOnExit, "退出没有取消正在进行的客户端历史查询。");
                Require(main.Performance.LastImportPath == originalPath
                        && ReferenceEquals(main.Performance.Estimate, originalEstimate),
                    "退出后迟到的成功结果更新了路径或估算分。");
                Require(originalSettings is null ? !File.Exists(settingsPath)
                        : File.Exists(settingsPath) && File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings),
                    existingSettings ? "退出后迟到的查询改写了旧目录的 Riot 设置。" : "退出后迟到的查询在旧目录创建了 Riot 设置。");
                Require(!File.Exists(settingsPath + ".tmp"), "退出后迟到的查询留下了旧目录设置临时文件。");
                Require(history.IsDisposed && !main.Performance.IsFetching, "退出后的查询没有结束或释放客户端。");
            });
        }
    }),
    ("外服海克斯队列保持请求快照并进入评分", () => WithMain(async (main, _, _) =>
    {
        var stub = new RiotStub(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/accounts/by-riot-id/", StringComparison.Ordinal))
            {
                main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.IsMainland);
                main.Performance.SelectedQueue = PerformanceViewModel.Queues.Single(q => q.QueueId == 440);
                main.Performance.RiotIdText = "Changed#EUW";
                return RiotStub.Json("""{"puuid":"OWNER-PUUID","gameName":"Tester","tagLine":"KR1"}""");
            }
            if (path.EndsWith("/ids", StringComparison.Ordinal)) return RiotStub.Json("""["KR_2400"]""");
            return RiotStub.Json(RiotStub.Match("KR_2400", "OWNER-PUUID", mapId: 12, queueId: 2400));
        });
        main.Performance.CreateRiotClient = _ => new LoLKarmaBook.Riot.RiotApiClient(stub, (_, _) => Task.CompletedTask);
        main.Performance.ApiKey = "RGAPI-hextech-test";
        main.Performance.RiotIdText = "Tester#KR1";
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");
        main.Performance.SelectedQueue = PerformanceViewModel.Queues.Single(q => q.QueueId == 2400);
        await Run(main.Performance.FetchRiotCommand);
        Require(main.Performance.Score is { GameCount: 1 }, "外服海克斯对局没有进入评分：" + main.Performance.ErrorText);
        Require(stub.Requests.All(r => r.RequestUri!.Host == "asia.api.riotgames.com")
                && stub.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/ids", StringComparison.Ordinal)).RequestUri!.Query.Contains("queue=2400", StringComparison.Ordinal),
            "等待期间区服或队列变化污染了已发起的请求。");
        Require(main.Performance.RiotSourceText.Contains("韩服", StringComparison.Ordinal)
                && main.Performance.RiotSourceText.Contains("海克斯大乱斗", StringComparison.Ordinal), "外服结果错误标注为改变后的筛选条件。");
    })),
    ("Riot 查询：填 Key 后拉取对局并计算估算 ELO，Key 不落入日志或界面文本", () => WithMain(async (main, root, _) =>
    {
        const string key = "RGAPI-01234567-89ab-cdef-0123-456789abcdef";
        var stub = new RiotStub(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/accounts/by-riot-id/", StringComparison.Ordinal))
            {
                return RiotStub.Json("""{"puuid":"OWNER-PUUID","gameName":"Hide on bush","tagLine":"KR1"}""");
            }

            if (path.EndsWith("/ids", StringComparison.Ordinal))
            {
                return RiotStub.Json("""["KR_1","KR_2","KR_3"]""");
            }

            string id = path[(path.LastIndexOf('/') + 1)..];
            return RiotStub.Json(RiotStub.Match(id, "OWNER-PUUID", win: id != "KR_2"));
        });

        // The raw stubbed client must build its own response content, so the
        // HttpClient ceiling stays at its default rather than matching the 20 s
        // per-request setting exactly.
        main.Performance.CreateRiotClient = _ => new LoLKarmaBook.Riot.RiotApiClient(stub,
            (_, _) => Task.CompletedTask);
        main.Performance.ApiKey = key;
        main.Performance.RiotIdText = "Hide on bush#KR1"; // 未勾选「记住 Key」
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");

        await Run(main.Performance.FetchRiotCommand);

        Require(main.Performance.ErrorText.Length == 0, "Riot 查询报错：" + main.Performance.ErrorText);
        Require(main.Performance.Score is { GameCount: 3 }, "Riot 返回的 3 局没有进入评分。");
        Require(main.Performance.Estimate is not null, "有队伍名单却没有估算 ELO 分。");
        Require(main.Performance.DatasetSource.Contains("Hide on bush#KR1", StringComparison.Ordinal),
            "结果未标注查询的 Riot ID。");
        Require(stub.Requests.All(r => r.RequestUri!.Host == "asia.api.riotgames.com"), "KR 区没有请求到 asia 集群。");
        Require(stub.Requests.All(r => !r.RequestUri!.Query.Contains("queue", StringComparison.Ordinal)
                                       || r.RequestUri.Query.Contains("queue=420", StringComparison.Ordinal)),
            "队列筛选没有传达到请求里。");
        foreach (var request in stub.Requests)
        {
            Require(request.Headers.TryGetValues("X-Riot-Token", out var values) && values.Single() == key,
                "Key 没有通过 X-Riot-Token 发送。");
        }

        string log = File.ReadAllText(LogFile(root));
        Require(!log.Contains(key, StringComparison.Ordinal), "Riot Key 出现在日志里。");
        Require(!main.Status.Message.Contains(key, StringComparison.Ordinal)
                && !main.Performance.DatasetMeta.Contains(key, StringComparison.Ordinal),
            "Riot Key 出现在状态或结果文本里。");
        Require(!File.ReadAllText(Path.Combine(root, "riot-api.json")).Contains(key, StringComparison.Ordinal),
            "在未勾选记住时 Key 仍被写入磁盘。");
    })),
    ("Riot 查询：勾选记住后 Key 只以密文落盘并可恢复", () => WithMain(async (main, root, _) =>
    {
        const string key = "RGAPI-remember-me-0000";
        var stub = new RiotStub(request => request.RequestUri!.AbsolutePath.Contains("/ids", StringComparison.Ordinal)
            ? RiotStub.Json("""["KR_1"]""")
            : request.RequestUri.AbsolutePath.Contains("by-riot-id", StringComparison.Ordinal)
                ? RiotStub.Json("""{"puuid":"OWNER-PUUID","gameName":"Tester","tagLine":"KR1"}""")
                : RiotStub.Json(RiotStub.Match("KR_1", "OWNER-PUUID")));
        main.Performance.CreateRiotClient = _ => new LoLKarmaBook.Riot.RiotApiClient(stub,
            (_, _) => Task.CompletedTask);
        main.Performance.ApiKey = key;
        main.Performance.RememberApiKey = true;
        main.Performance.RiotIdText = "Tester#KR1";
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");

        await Run(main.Performance.FetchRiotCommand);
        Require(main.Performance.ErrorText.Length == 0, "Riot 查询报错：" + main.Performance.ErrorText);

        string file = Path.Combine(root, "riot-api.json");
        Require(File.Exists(file), "勾选记住后设置文件未生成。");
        string raw = File.ReadAllText(file);
        Require(!raw.Contains(key, StringComparison.Ordinal), "设置文件里出现了明文 Key。");
        Require(raw.Contains("\"protectedKey\"", StringComparison.Ordinal), "设置文件没有保存受保护的 Key。");

        RiotLookupPreferences loaded = main.RiotKeys.Load();
        Require(loaded.ApiKey == key, "重启后无法恢复 Key。");
        Require(loaded.PlatformId == "KR" && loaded.RiotId == "Tester#KR1" && loaded.QueueId == 420,
            "重启后区服、Riot ID 或队列选择丢失。");
    })),
    ("外服 Riot ID 校验保留且大陆是独立客户端入口", () => WithMain((main, _, _) =>
    {
        var stub = new RiotStub(_ => throw new InvalidOperationException("不应发起网络请求。"));
        main.Performance.CreateRiotClient = _ => new LoLKarmaBook.Riot.RiotApiClient(stub,
            (_, _) => Task.CompletedTask);
        main.Performance.ApiKey = "RGAPI-test";
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");
        main.Performance.RiotIdText = "Hide on bush";
        Require(!main.Performance.FetchRiotCommand.CanExecute(null), "缺少 # 标签时仍可发起查询。");

        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");
        main.Performance.RiotIdText = "Hide on bush#KR1";
        Require(main.Performance.FetchRiotCommand.CanExecute(null), "合法的 Riot ID 无法发起查询。");

        var option = new RiotPlatformOption("HN1", "国服 HN1");
        Require(option.Region is null && !option.IsSupported, "本机分区不应被假映射为 Riot 外服接口。");
        var mainland = main.Performance.Platforms.Single(p => p.Platform == "CN");
        Require(mainland.IsMainland && mainland.IsSupported && mainland.Region is null, "大陆没有使用独立客户端入口。");
        Require(stub.Requests.Count == 0, "校验失败时仍发起了网络请求。");
        return Task.CompletedTask;
    })),
    ("Riot 查询：401 只显示错误提示，状态与日志都不含 Key", () => WithMain(async (main, root, _) =>
    {
        const string key = "RGAPI-rejected-key";
        var stub = new RiotStub(_ => RiotStub.Json("""{"status":{"status_code":401}}""", 401));
        main.Performance.CreateRiotClient = _ => new LoLKarmaBook.Riot.RiotApiClient(stub,
            (_, _) => Task.CompletedTask);
        main.Performance.ApiKey = key;
        main.Performance.RiotIdText = "Tester#KR1";
        main.Performance.SelectedPlatform = main.Performance.Platforms.Single(p => p.Platform == "KR");

        await Run(main.Performance.FetchRiotCommand);
        Require(main.Performance.ErrorText.Contains("拒绝", StringComparison.Ordinal), "401 没有给出 Key 被拒的提示。");
        Require(main.Status.Severity == StatusSeverity.Error, "401 没有以错误状态呈现。");
        Require(!main.Performance.ErrorText.Contains(key, StringComparison.Ordinal)
                && !main.Status.Message.Contains(key, StringComparison.Ordinal),
            "错误提示里出现了 Key。");
        Require(!File.ReadAllText(LogFile(root)).Contains(key, StringComparison.Ordinal), "Key 出现在日志里。");
        Require(!main.Performance.HasResult, "查询失败却显示了结果。");
    }))
};

static string LogFile(string root)
{
    string directory = Path.Combine(root, "logs");
    return Directory.GetFiles(directory).Single();
}

static LcuHistoryResult LocalHistorySuccess(string source)
{
    PerformanceDataset sample = LoLKarmaBook.Scoring.SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo(3).Dataset;
    var dataset = new PerformanceDataset { Source = source, Matches = sample.Matches, PlayerId = sample.PlayerId, Roster = sample.Roster };
    return new LcuHistoryResult(LcuHistoryStatus.Success, "success", Account("local-owner", "国服玩家"), dataset, 3, 0);
}

int failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"桌面逻辑监督测试：{tests.Length - failures}/{tests.Length} 通过（不含 WPF/DPAPI）。");
return failures == 0 ? 0 : 1;

static async Task Run(IAsyncCommand command)
{
    Require(command.CanExecute(null), "操作当前不可执行。");
    command.Execute(null);
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (command.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(5);
    Require(!command.IsRunning, "操作超过 5 秒仍未结束。");
}

static async Task WithMain(Func<MainViewModel, string, DpapiTextProtector, Task> action, ILcuClientService? clientService = null,
    bool enableRealtime = false)
{
    var root = Path.Combine(Path.GetTempPath(), "lolkarmabook-desktop-logic-" + Guid.NewGuid().ToString("N"));
    var paths = new AppPaths(root);
    paths.EnsureDirectories();
    using var protector = new DpapiTextProtector();
    await using var main = new MainViewModel(paths, new DiagnosticLog(paths.LogDirectory), protector, new DesktopSettings(),
        enableRealtime: enableRealtime, clientService: clientService);
    try
    {
        await main.SwitchProfileAsync(main.Profiles.Single(p => p.Id == ProfileCatalog.DefaultProfileId));
        Require(main.IsProfileReady, "测试空间打开失败：" + main.ProfileSwitchError);
        await action(main, root, protector);
    }
    finally
    {
        Confirmation.Handler = null;
        await main.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static Task WithLiveMain(Func<MainViewModel, FakeClient, Task> action)
{
    var client = new FakeClient();
    return WithMain((main, _, _) => action(main, client), client);
}

/// <summary>
/// The demo space is no longer offered by the product, but the profile-scoped
/// behaviour still needs a second, non-account space to switch to. Tests add a
/// descriptor with the reserved id so switch/maintenance paths stay covered.
/// </summary>
static ProfileDescriptor DemoProfile(MainViewModel main)
{
    ProfileDescriptor? existing = main.Profiles.FirstOrDefault(p => p.Id == ProfileCatalog.DemoProfileId);
    if (existing is not null)
    {
        return existing;
    }

    var demo = new ProfileDescriptor(
        ProfileCatalog.DemoProfileId, "模拟演示空间", "测试用演示空间", isDemo: true);
    main.Profiles.Add(demo);
    return demo;
}

static ClientAccount Account(string id, string name) => new(id, name, "CN", "HN1", ClientAccount.CreateProfileKey(id));

static ClientSnapshot Connected(ClientAccount account, MatchSession? session) => new(ClientConnectionStatus.Connected,
    "正在实时检测当前对局。", account, session, session?.Phase ?? GamePhase.Lobby, DateTimeOffset.UtcNow);

static MatchSession LiveSession(MatchSession template, string localId, long sequence, bool reverse = false, string? external = null,
    GamePhase phase = GamePhase.ChampionSelect) =>
    // Stable identity across phases: the client reveals the match id only after
    // the roster exists, so the test keeps one identity for the same match.
    new(localId, SourceKind.Live, "desktop-test-client", 1, sequence, DateTimeOffset.UtcNow, phase,
        template.QueueId, template.MapId, external ?? "lcu:test:" + localId, true,
        reverse ? template.Participants.Reverse() : template.Participants);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeClient : ILcuClientService
{    private int _concurrent;
    public int PollCount { get; private set; }
    public int MaxConcurrent { get; private set; }
    public int DelayMilliseconds { get; set; }
    public bool IsDisposed { get; private set; }
    public ClientSnapshot Snapshot { get; set; } = new(ClientConnectionStatus.NotRunning, "等待客户端", null, null,
        GamePhase.Unknown, DateTimeOffset.UtcNow);
    public async Task<ClientSnapshot> PollAsync(string? clientDirectory = null, CancellationToken cancellationToken = default)
    {
        PollCount++;
        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _concurrent));
        try
        {
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
            return Snapshot;
        }
        finally { Interlocked.Decrement(ref _concurrent); }
    }
    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// Answers Riot requests from canned JSON. The URL host is checked so a test
/// cannot accidentally pass by calling a different endpoint than the client should.
/// </summary>
sealed class FakeTagSource : ILcuTagSource
{
    public Task<TagSeedCatalog?> ReadTagSeedsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<TagSeedCatalog?>(new TagSeedCatalog(
            new[] { new TagSeed(999999, "测试英雄", "测试", "TestHero") },
            new[] { new TagSeed(77777, "测试装备") }));
}

sealed class RiotStub : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public RiotStub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Json(string json, int status = 200) =>
        new((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    /// <summary>One 5v5 Summoner's Rift win for <paramref name="owner"/>.</summary>
    public static string Match(string matchId, string owner, bool win = true, int mapId = 11, int queueId = 420)
    {
        static string P(string puuid, int team, bool win, int k, int d, int a) => $$$"""
            {"puuid":"{{{puuid}}}","teamId":{{{team}}},"win":{{{(win ? "true" : "false")}}},
             "kills":{{{k}}},"deaths":{{{d}}},"assists":{{{a}}},
             "totalDamageDealtToChampions":{{{k * 900}}},"totalDamageTaken":{{{d * 1500 + 2000}}},"goldEarned":{{{k * 400 + 6000}}}}
            """;

        var players = new List<string> { P(owner, 100, win, 6, 2, 8) };
        for (var i = 1; i < 5; i++) players.Add(P(owner + "-ally-" + i, 100, win, 2, 4, 6));
        for (var i = 1; i <= 5; i++) players.Add(P(owner + "-foe-" + i, 200, !win, 3, 5, 4));

        // Same shape as Riot's match-v5 detail: metadata and info are siblings.
        string body = "{"
                      + "\"metadata\":{\"matchId\":\"" + matchId + "\"},"
                      + "\"info\":{\"mapId\":" + mapId + ",\"queueId\":" + queueId + ",\"gameEndId\":2,\"gameDuration\":1800,"
                      + "\"gameStartTimestamp\":1767225600000,\"gameCreation\":1767225600000,"
                      + "\"participants\":[" + string.Join(",", players) + "]}}";
        return body;
    }
}

sealed class FakeHistoryService : ILcuHistoryService
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<LcuHistoryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Directory { get; private set; }
    public int Count { get; private set; }
    public int? QueueId { get; private set; }
    public CancellationToken Token { get; private set; }
    public bool IsDisposed { get; private set; }
    public Task<LcuHistoryResult> GetHistoryAsync(string? clientDirectory, int count = 20, int? queueId = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory = clientDirectory;
        Count = count;
        QueueId = queueId;
        Token = cancellationToken;
        Started.TrySetResult(true);
        return Completion.Task;
    }
    public void Dispose() => IsDisposed = true;
}

/// <summary>Serves the three Hexdata calls locally so sign-in and reports stay offline in tests.</summary>
sealed class HexdataStub : HttpMessageHandler
{
    public const string ReportJson = """
    {"report":{"schemaVersion":"hexdata-public-match-report-v1","resolvedGameId":"Name#1234","regionKey":"hn1","generatedAt":"2026-10-27T17:00:00Z","modelVersion":"v1","targetEstimate":{"status":"ready","score":1234,"confidence":"high","basis":"direct_snapshot","intervalHalfWidth":25,"percentileBps":6250},"recentMatches":[{"gameId":"HN1_1","startedAt":"2026-10-27T16:00:00Z","durationSeconds":900,"targetWin":true,"detailsAvailable":true,"teamDelta":3,"targetMatchSummary":{"championId":103,"championName":"九尾妖狐","kills":5,"deaths":2,"assists":9,"win":true,"durationSeconds":900,"matchHexScore":88},"teams":[{"side":"blue","estimateCoverage":{"scored":5,"total":5},"participants":[{"publicGameId":"P1#1","championId":1,"championName":"安妮","kills":1,"deaths":2,"assists":3,"matchHexScore":50,"isTarget":true,"hidden":false,"currentEstimate":{"score":1000,"confidence":"medium","basis":"direct_snapshot"}},{"publicGameId":"P2#2","championId":2,"championName":"奥拉夫","kills":2,"deaths":2,"assists":4,"matchHexScore":51,"isTarget":false,"hidden":false},{"publicGameId":"P3#3","championId":3,"championName":"盖伦","kills":3,"deaths":2,"assists":5,"matchHexScore":52,"isTarget":false,"hidden":false},{"publicGameId":"P4#4","championId":4,"championName":"艾希","kills":4,"deaths":2,"assists":6,"matchHexScore":53,"isTarget":false,"hidden":false},{"publicGameId":"P5#5","championId":5,"championName":"索拉卡","kills":5,"deaths":2,"assists":7,"matchHexScore":54,"isTarget":false,"hidden":false}]},{"side":"red","estimateCoverage":{"scored":5,"total":5},"participants":[{"publicGameId":"Q1#1","championId":6,"championName":"卡特琳娜","kills":1,"deaths":5,"assists":2,"matchHexScore":40,"isTarget":false,"hidden":false},{"publicGameId":"Q2#2","championId":7,"championName":"拉克丝","kills":2,"deaths":4,"assists":3,"matchHexScore":41,"isTarget":false,"hidden":false},{"publicGameId":"Q3#3","championId":8,"championName":"易大师","kills":3,"deaths":3,"assists":4,"matchHexScore":42,"isTarget":false,"hidden":false},{"publicGameId":"Q4#4","championId":9,"championName":"贾克斯","kills":4,"deaths":2,"assists":5,"matchHexScore":43,"isTarget":false,"hidden":false},{"publicGameId":"Q5#5","championId":10,"championName":"泰达米尔","kills":5,"deaths":1,"assists":6,"matchHexScore":44,"isTarget":false,"hidden":false}]}]}]}}
    """;

    public string? LastReportBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/v1/public/match-report")
        {
            LastReportBody = body;
        }

        string? token = request.Headers.Authorization?.Parameter;
        bool authorized = token == "token-1";
        return path switch
        {
            "/v1/auth/request-code" => Json("{}"),
            "/v1/auth/verify-code" => Json("""{"sessionToken":"token-1"}"""),
            "/v1/me" => authorized
                ? Json("""{"authenticated":true,"account":{"email":"player@example.com"},"membership":{"plan":"free","isActive":true}}""")
                : new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized),
            "/v1/public/match-report" => Json(ReportJson),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    }

    private static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
